//! Bounded, database-scoped physical snapshot bootstrap over admitted peers.
//!
//! The writer captures an immutable file pair under its engine barrier. Network
//! transfer runs after releasing the barrier. The receiver publishes only after
//! every file hash, SQLite integrity, and the scoped WAL checkpoint agree.
//! Destination paths are local configuration, never supplied by network frames.

use std::io::{Read, Seek, SeekFrom, Write};
use std::path::PathBuf;
use std::sync::{Arc, Mutex};
use terrabase::storage::{
    create_physical_file, open_physical_file, EncryptedStorage, PhysicalFile,
};

use crate::production::DatabaseRouteScope;
use crate::wal_driver::{
    WalAuthenticatedPeer, WalCheckpointMessage, WalEngineHandle, WalFollowerDriver,
};
use crate::wal_follower::{WalCheckpoint, WalFollower, WalFollowerError, MAX_WAL_BATCH_BYTES};
use crate::wal_quic::WalQuicError;
use crate::wal_route::{WalReplicaBinding, WalRouteError};
use crate::wal_wire::WalWireError;

const FILES: [&str; 3] = ["terrabase.sqlite3", "terrabase.sqlite3-wal", "owner.tbv1"];
const PREFIX: usize = 94;
const CHUNK_PREFIX: usize = PREFIX + 32 + 1 + 8 + 32;

/// Physical snapshot transfer failure; no installed follower receipt is implied.
#[derive(Debug)]
pub enum WalSnapshotError {
    /// Authentication, epoch fencing, worker admission, or scoped protocol failure.
    Admission(WalQuicError),
    /// Failed file operation or SQLite snapshot validation.
    Storage(WalFollowerError),
    /// Invalid manifest, digest, chunk range, or incomplete transfer.
    InvalidSnapshot,
    /// The configured receiver byte limit would be exceeded.
    QuotaExceeded,
}

impl From<std::io::Error> for WalSnapshotError {
    fn from(error: std::io::Error) -> Self {
        Self::Storage(error.into())
    }
}

impl From<WalFollowerError> for WalSnapshotError {
    fn from(error: WalFollowerError) -> Self {
        Self::Storage(error)
    }
}

impl From<WalQuicError> for WalSnapshotError {
    fn from(error: WalQuicError) -> Self {
        Self::Admission(error)
    }
}

#[derive(Clone, Debug, Eq, PartialEq, serde::Serialize, serde::Deserialize)]
#[serde(deny_unknown_fields)]
struct FileManifest {
    bytes: u64,
    digest: [u8; 32],
}

/// Immutable snapshot identity and bounded file inventory. File names cannot
/// be selected by a sender: the protocol permits only the three SQLite files.
#[derive(Clone, Debug)]
pub struct WalSnapshotManifest {
    /// Exact immutable database routing identity.
    pub scope: DatabaseRouteScope,
    /// Accepted replica epoch.
    pub replica_epoch: u64,
    identity: [u8; 32],
    contents: ManifestContents,
}

#[derive(Clone, Debug, serde::Serialize, serde::Deserialize)]
#[serde(deny_unknown_fields)]
struct ManifestContents {
    files: [Option<FileManifest>; 3],
    checkpoint: WalCheckpoint,
    sequence: u64,
}

impl WalSnapshotManifest {
    /// Returns the content identity bound to the database scope and epoch.
    #[must_use]
    pub fn identity(&self) -> [u8; 32] {
        self.identity
    }

    /// Conservative sequence observed before capturing the consistent snapshot.
    #[must_use]
    pub fn sequence(&self) -> u64 {
        self.contents.sequence
    }

    /// Returns the exact durable position installed with this file snapshot.
    #[must_use]
    pub fn checkpoint(&self) -> WalCheckpoint {
        self.contents.checkpoint.clone()
    }

    /// Returns the byte size of a fixed file slot, or absence of an optional file.
    #[must_use]
    pub fn file_bytes(&self, slot: u8) -> Option<u64> {
        self.contents.files.get(usize::from(slot))?.as_ref().map(|file| file.bytes)
    }

    /// Encodes the bounded inventory and its content identity.
    ///
    /// # Errors
    /// Returns a malformed scope or invalid manifest error.
    pub fn encode(&self) -> Result<Vec<u8>, WalSnapshotError> {
        self.validate()?;
        let mut bytes = prefix(b"TBWSM\0\0\x01", self.scope, self.replica_epoch)?;
        bytes.extend_from_slice(&self.identity);
        bytes.extend(
            serde_json::to_vec(&self.contents).map_err(|_| WalSnapshotError::InvalidSnapshot)?,
        );
        Ok(bytes)
    }

    /// Decodes the inventory with a fixed allocation ceiling.
    ///
    /// # Errors
    /// Rejects oversized input, invalid routing, identity, file inventory, or epoch.
    pub fn decode(bytes: &[u8]) -> Result<Self, WalSnapshotError> {
        if bytes.len() > 4096 || bytes.len() < PREFIX + 32 {
            return Err(WalSnapshotError::InvalidSnapshot);
        }
        let (scope, replica_epoch) = decode_prefix(bytes, b"TBWSM\0\0\x01")?;
        let identity =
            bytes[PREFIX..PREFIX + 32].try_into().map_err(|_| WalSnapshotError::InvalidSnapshot)?;
        let contents = serde_json::from_slice(&bytes[PREFIX + 32..])
            .map_err(|_| WalSnapshotError::InvalidSnapshot)?;
        let manifest = Self { scope, replica_epoch, identity, contents };
        manifest.validate()?;
        Ok(manifest)
    }

    fn validate(&self) -> Result<(), WalSnapshotError> {
        if self.replica_epoch == 0
            || self.contents.files[0].as_ref().is_none_or(|file| file.bytes == 0)
        {
            return Err(WalSnapshotError::InvalidSnapshot);
        }
        if self.identity != manifest_identity(self.scope, self.replica_epoch, &self.contents)? {
            return Err(WalSnapshotError::InvalidSnapshot);
        }
        Ok(())
    }

    fn authorize(&self, binding: &WalReplicaBinding) -> Result<(), WalSnapshotError> {
        if self.scope != binding.scope() || self.replica_epoch != binding.epoch() {
            return Err(WalSnapshotError::Admission(WalQuicError::Route(
                WalRouteError::Unauthorized,
            )));
        }
        self.validate()
    }
}

/// Local evidence produced only by successful durable snapshot installation.
/// It is not a serving proof: live catch-up and cross-region reconciliation
/// still need independent observations under the same accepted assignment.
#[derive(Clone, Debug)]
pub struct WalInstalledSnapshotProof {
    scope: DatabaseRouteScope,
    generation: u64,
    epoch: u64,
    snapshot: [u8; 32],
    checkpoint: [u8; 32],
    sequence: u64,
}

impl WalInstalledSnapshotProof {
    /// Accepted placement generation at installation.
    #[must_use]
    pub fn generation(&self) -> u64 {
        self.generation
    }
    /// Database whose files were durably installed.
    #[must_use]
    pub fn scope(&self) -> DatabaseRouteScope {
        self.scope
    }
    /// Accepted replica epoch of installation.
    #[must_use]
    pub fn epoch(&self) -> u64 {
        self.epoch
    }
    /// Scoped snapshot manifest content identity.
    #[must_use]
    pub fn snapshot_digest(&self) -> [u8; 32] {
        self.snapshot
    }
    /// Durable WAL checkpoint content identity.
    #[must_use]
    pub fn checkpoint_digest(&self) -> [u8; 32] {
        self.checkpoint
    }
    /// Conservative SQLite sequence included in the installed snapshot.
    #[must_use]
    pub fn sequence(&self) -> u64 {
        self.sequence
    }
}

/// Snapshot-specific durable installation receipt, sent only after sink finish.
#[derive(Clone, Debug)]
pub struct WalSnapshotAck {
    proof: WalInstalledSnapshotProof,
}

impl WalSnapshotAck {
    /// Encodes installation evidence for its authenticated writer.
    ///
    /// # Errors
    /// Rejects an invalid replica epoch.
    pub fn encode(&self) -> Result<Vec<u8>, WalSnapshotError> {
        let mut bytes = prefix(b"TBWSA\0\0\x01", self.proof.scope, self.proof.epoch)?;
        bytes.extend_from_slice(&self.proof.snapshot);
        bytes.extend_from_slice(&self.proof.checkpoint);
        bytes.extend_from_slice(&self.proof.sequence.to_be_bytes());
        bytes.extend_from_slice(&self.proof.generation.to_be_bytes());
        Ok(bytes)
    }

    /// Decodes untrusted receipt bytes; only source authentication can produce
    /// install evidence from this wire object.
    ///
    /// # Errors
    /// Rejects wrong framing, invalid scope, epoch, and trailing bytes.
    pub fn decode(bytes: &[u8]) -> Result<Self, WalSnapshotError> {
        if bytes.len() != PREFIX + 80 {
            return Err(WalSnapshotError::InvalidSnapshot);
        }
        let (scope, epoch) = decode_prefix(bytes, b"TBWSA\0\0\x01")?;
        let snapshot =
            bytes[PREFIX..PREFIX + 32].try_into().map_err(|_| WalSnapshotError::InvalidSnapshot)?;
        let checkpoint = bytes[PREFIX + 32..PREFIX + 64]
            .try_into()
            .map_err(|_| WalSnapshotError::InvalidSnapshot)?;
        let sequence = u64::from_be_bytes(
            bytes[PREFIX + 64..PREFIX + 72]
                .try_into()
                .map_err(|_| WalSnapshotError::InvalidSnapshot)?,
        );
        let generation = u64::from_be_bytes(
            bytes[PREFIX + 72..].try_into().map_err(|_| WalSnapshotError::InvalidSnapshot)?,
        );
        if generation == 0 {
            return Err(WalSnapshotError::InvalidSnapshot);
        }
        Ok(Self {
            proof: WalInstalledSnapshotProof {
                scope,
                epoch,
                snapshot,
                checkpoint,
                sequence,
                generation,
            },
        })
    }

    /// Database scope used by the endpoint dispatcher before authentication.
    #[must_use]
    pub fn scope(&self) -> DatabaseRouteScope {
        self.proof.scope
    }
}

impl WalInstalledSnapshotProof {
    /// Produces a scoped receipt from this successfully installed snapshot.
    #[must_use]
    pub fn acknowledgment(&self) -> WalSnapshotAck {
        WalSnapshotAck { proof: self.clone() }
    }
}

/// Request for the next bounded slice of one immutable snapshot file.
#[derive(Clone, Debug)]
pub struct WalSnapshotRequest {
    /// Database scope, not a path.
    pub scope: DatabaseRouteScope,
    /// Accepted replica epoch.
    pub replica_epoch: u64,
    /// Manifest identity selected by the receiving session.
    pub identity: [u8; 32],
    /// Fixed slot: SQLite database, WAL, or owner record.
    pub file: u8,
    /// Contiguous byte offset; resumed requests may repeat an already synced chunk.
    pub offset: u64,
}

impl WalSnapshotRequest {
    /// Creates a request bound to the authenticated manifest.
    #[must_use]
    pub fn new(manifest: &WalSnapshotManifest, file: u8, offset: u64) -> Self {
        Self {
            scope: manifest.scope,
            replica_epoch: manifest.replica_epoch,
            identity: manifest.identity,
            file,
            offset,
        }
    }

    /// Encodes the fixed-size chunk request.
    ///
    /// # Errors
    /// Rejects invalid file slots or replica epochs.
    pub fn encode(&self) -> Result<Vec<u8>, WalSnapshotError> {
        if usize::from(self.file) >= FILES.len() {
            return Err(WalSnapshotError::InvalidSnapshot);
        }
        let mut bytes = prefix(b"TBWSR\0\0\x01", self.scope, self.replica_epoch)?;
        bytes.extend_from_slice(&self.identity);
        bytes.push(self.file);
        bytes.extend_from_slice(&self.offset.to_be_bytes());
        Ok(bytes)
    }

    /// Decodes a fixed-size chunk request.
    ///
    /// # Errors
    /// Rejects unknown versions, scope errors, trailing bytes, and invalid slots.
    pub fn decode(bytes: &[u8]) -> Result<Self, WalSnapshotError> {
        if bytes.len() != CHUNK_PREFIX - 32 {
            return Err(WalSnapshotError::InvalidSnapshot);
        }
        let (scope, replica_epoch) = decode_prefix(bytes, b"TBWSR\0\0\x01")?;
        let identity =
            bytes[PREFIX..PREFIX + 32].try_into().map_err(|_| WalSnapshotError::InvalidSnapshot)?;
        let file = bytes[PREFIX + 32];
        if usize::from(file) >= FILES.len() {
            return Err(WalSnapshotError::InvalidSnapshot);
        }
        let offset = u64::from_be_bytes(
            bytes[PREFIX + 33..].try_into().map_err(|_| WalSnapshotError::InvalidSnapshot)?,
        );
        Ok(Self { scope, replica_epoch, identity, file, offset })
    }
}

/// A checksummed bounded file slice. Hashes detect corruption; authentication
/// and authority always come from the actual production connection.
#[derive(Clone, Debug)]
pub struct WalSnapshotChunk {
    request: WalSnapshotRequest,
    bytes: Vec<u8>,
}

impl WalSnapshotChunk {
    /// Identifies the response's exact snapshot, file, and offset.
    #[must_use]
    pub fn request(&self) -> &WalSnapshotRequest {
        &self.request
    }

    /// Returns the bounded plaintext logical file bytes transported inside QUIC.
    #[must_use]
    pub fn bytes(&self) -> &[u8] {
        &self.bytes
    }

    /// Encodes a bounded slice and checksum without JSON-expanding file bytes.
    ///
    /// # Errors
    /// Rejects an empty or oversized slice and invalid request metadata.
    pub fn encode(&self) -> Result<Vec<u8>, WalSnapshotError> {
        if self.bytes.is_empty() || self.bytes.len() > MAX_WAL_BATCH_BYTES {
            return Err(WalSnapshotError::InvalidSnapshot);
        }
        let mut bytes = self.request.encode()?;
        bytes[..8].copy_from_slice(b"TBWSC\0\0\x01");
        bytes.extend_from_slice(blake3::hash(&self.bytes).as_bytes());
        bytes.extend_from_slice(&self.bytes);
        Ok(bytes)
    }

    /// Decodes and verifies a bounded file slice before allocating its payload.
    ///
    /// # Errors
    /// Rejects bad scope, length, checksum, or protocol version.
    pub fn decode(bytes: &[u8]) -> Result<Self, WalSnapshotError> {
        if bytes.len() <= CHUNK_PREFIX || bytes.len() > CHUNK_PREFIX + MAX_WAL_BATCH_BYTES {
            return Err(WalSnapshotError::InvalidSnapshot);
        }
        decode_prefix(bytes, b"TBWSC\0\0\x01")?;
        if blake3::hash(&bytes[CHUNK_PREFIX..]).as_bytes()
            != &bytes[CHUNK_PREFIX - 32..CHUNK_PREFIX]
        {
            return Err(WalSnapshotError::InvalidSnapshot);
        }
        let mut request = bytes[..CHUNK_PREFIX - 32].to_vec();
        request[..8].copy_from_slice(b"TBWSR\0\0\x01");
        Ok(Self {
            request: WalSnapshotRequest::decode(&request)?,
            bytes: bytes[CHUNK_PREFIX..].to_vec(),
        })
    }
}

/// Immutable source files whose lifetime spans retries and reconnects.
pub struct WalSnapshotSource {
    binding: WalReplicaBinding,
    snapshot: Arc<SnapshotDirectory>,
    manifest: WalSnapshotManifest,
}

struct SnapshotDirectory {
    path: PathBuf,
    _encryption: Option<EncryptedStorage>,
    retain: bool,
}
impl Drop for SnapshotDirectory {
    fn drop(&mut self) {
        if !self.retain {
            let _ = std::fs::remove_dir_all(&self.path);
        }
    }
}

impl WalSnapshotSource {
    /// Captures a consistent physical snapshot into an exclusive local staging
    /// directory. The caller supplies a fresh path whose parent already exists.
    ///
    /// # Errors
    /// Rejects fenced authority, an occupied path, capture failure, or disk I/O.
    pub async fn capture(
        binding: WalReplicaBinding,
        engine: Arc<dyn WalEngineHandle>,
        staging: PathBuf,
    ) -> Result<Self, WalSnapshotError> {
        binding.authorize_local(true).map_err(WalQuicError::Route)?;
        let permit = binding.reserve_worker().map_err(WalQuicError::Route)?;
        let worker_binding = binding.clone();
        let (snapshot, manifest) = tokio::task::spawn_blocking(move || {
            let _permit = permit;
            worker_binding
                .with_admission(|| {
                    let tenant = terrabase::types::TenantId::try_new(
                        &worker_binding.scope().tenant().to_text(),
                    )
                    .map_err(|_| WalRouteError::Unauthorized)?;
                    let scope = engine.engine().scope_for(&tenant);
                    let sequence = engine
                        .engine()
                        .read_snapshot(&scope)
                        .map_err(|_| WalRouteError::Unauthorized)?
                        .sequence()
                        .get();
                    let (follower, checkpoint) =
                        WalFollower::bootstrap_engine(engine.engine(), &staging)
                            .map_err(WalRouteError::Storage)?;
                    let encryption = EncryptedStorage::for_related_location(&staging, &staging)
                        .map_err(|error| WalRouteError::Storage(error.into()))?;
                    drop(follower);
                    let snapshot = Arc::new(SnapshotDirectory {
                        path: staging,
                        _encryption: encryption,
                        retain: false,
                    });
                    let mut files: [Option<FileManifest>; 3] = [None, None, None];
                    for (slot, name) in FILES.iter().enumerate() {
                        let path = snapshot.path.join(name);
                        match open_physical_file(&path) {
                            Ok(mut file) => {
                                files[slot] = Some(FileManifest {
                                    bytes: file
                                        .len()
                                        .map_err(|error| WalRouteError::Storage(error.into()))?,
                                    digest: file_digest(&mut file)
                                        .map_err(WalRouteError::Storage)?,
                                });
                            }
                            Err(error)
                                if error.kind() == std::io::ErrorKind::NotFound && slot != 0 => {}
                            Err(error) => return Err(WalRouteError::Storage(error.into())),
                        }
                    }
                    Ok((snapshot, ManifestContents { files, checkpoint, sequence }))
                })
                .map_err(|error| WalSnapshotError::Admission(WalQuicError::Route(error)))
        })
        .await
        .map_err(|_| WalQuicError::WorkerUnavailable)??;
        let identity = manifest_identity(binding.scope(), binding.epoch(), &manifest)?;
        let manifest = WalSnapshotManifest {
            scope: binding.scope(),
            replica_epoch: binding.epoch(),
            identity,
            contents: manifest,
        };
        manifest.validate()?;
        Ok(Self { binding, snapshot, manifest })
    }

    /// The immutable, scoped inventory to transmit to the admitted follower.
    #[must_use]
    pub fn manifest(&self) -> &WalSnapshotManifest {
        &self.manifest
    }

    /// Local parent directory authorized for refreshed immutable snapshots.
    #[must_use]
    pub fn staging_parent(&self) -> &std::path::Path {
        self.snapshot.path.parent().expect("captured snapshot has a parent")
    }

    /// Verifies that the admitted follower installed this exact immutable
    /// snapshot and its durable checkpoint before granting writer-side evidence.
    ///
    /// # Errors
    /// Rejects wrong identity, epoch, manifest, checkpoint, or sequence.
    pub fn authenticate_ack(
        &self,
        peer: &WalAuthenticatedPeer,
        ack: WalSnapshotAck,
    ) -> Result<WalInstalledSnapshotProof, WalSnapshotError> {
        peer.authorize(&self.binding, true)?;
        let expected = *blake3::hash(
            &serde_json::to_vec(&self.manifest.contents.checkpoint)
                .map_err(|_| WalSnapshotError::InvalidSnapshot)?,
        )
        .as_bytes();
        if ack.proof.generation != self.binding.generation()
            || ack.proof.scope != self.manifest.scope
            || ack.proof.epoch != self.manifest.replica_epoch
            || ack.proof.snapshot != self.manifest.identity
            || ack.proof.checkpoint != expected
            || ack.proof.sequence != self.manifest.contents.sequence
        {
            return Err(WalSnapshotError::InvalidSnapshot);
        }
        Ok(ack.proof)
    }

    /// Reads one bounded chunk for the authenticated follower, permitting exact
    /// offset retries without retaining per-request network buffers.
    ///
    /// # Errors
    /// Rejects wrong peers, scopes, snapshots, ranges, or failed file reads.
    pub async fn read(
        &self,
        peer: &WalAuthenticatedPeer,
        request: WalSnapshotRequest,
    ) -> Result<WalSnapshotChunk, WalSnapshotError> {
        peer.authorize(&self.binding, true)?;
        self.read_request(request).await
    }

    /// Reads immutable bytes for native backup under the existing admitted
    /// writer binding; no synthetic network identity is created.
    ///
    /// # Errors
    /// Rejects fences, invalid slots or offsets, and disk failures.
    pub async fn read_local(
        &self,
        file: u8,
        offset: u64,
    ) -> Result<WalSnapshotChunk, WalSnapshotError> {
        self.binding.authorize_local(true).map_err(WalQuicError::Route)?;
        self.read_request(WalSnapshotRequest::new(&self.manifest, file, offset)).await
    }

    async fn read_request(
        &self,
        request: WalSnapshotRequest,
    ) -> Result<WalSnapshotChunk, WalSnapshotError> {
        authorize_request(&self.manifest, &request)?;
        let size =
            self.manifest.file_bytes(request.file).ok_or(WalSnapshotError::InvalidSnapshot)?;
        if request.offset >= size {
            return Err(WalSnapshotError::InvalidSnapshot);
        }
        let length = usize::try_from((size - request.offset).min(MAX_WAL_BATCH_BYTES as u64))
            .map_err(|_| WalSnapshotError::InvalidSnapshot)?;
        let permit = self.binding.reserve_worker().map_err(WalQuicError::Route)?;
        let binding = self.binding.clone();
        let snapshot = self.snapshot.clone();
        tokio::task::spawn_blocking(move || {
            let _permit = permit;
            binding
                .with_admission(|| {
                    let mut file =
                        open_physical_file(&snapshot.path.join(FILES[usize::from(request.file)]))
                            .map_err(|error| WalRouteError::Storage(error.into()))?;
                    file.seek(SeekFrom::Start(request.offset))
                        .map_err(|error| WalRouteError::Storage(error.into()))?;
                    let mut bytes = vec![0; length];
                    file.read_exact(&mut bytes)
                        .map_err(|error| WalRouteError::Storage(error.into()))?;
                    Ok(WalSnapshotChunk { request, bytes })
                })
                .map_err(|error| WalSnapshotError::Admission(WalQuicError::Route(error)))
        })
        .await
        .map_err(|_| WalQuicError::WorkerUnavailable)?
    }
}

/// A receiver stages bounded chunks without opening a writable SQLite engine.
pub struct WalSnapshotSink {
    binding: WalReplicaBinding,
    state: Arc<Mutex<SinkState>>,
}

struct SinkState {
    staging: SnapshotDirectory,
    destination: PathBuf,
    manifest: WalSnapshotManifest,
    received: [u64; 3],
    keys: Option<Arc<dyn terrabase::encryption::DatabaseKeySource>>,
}

impl WalSnapshotSink {
    /// Starts a private receive directory after peer, scope, epoch, and disk
    /// quota admission. The caller exclusively owns the local destination.
    ///
    /// # Errors
    /// Rejects foreign authority, invalid inventory, excess bytes, or disk errors.
    pub async fn begin(
        binding: WalReplicaBinding,
        peer: &WalAuthenticatedPeer,
        manifest: WalSnapshotManifest,
        destination: PathBuf,
        max_bytes: u64,
        keys: Option<Arc<dyn terrabase::encryption::DatabaseKeySource>>,
    ) -> Result<Self, WalSnapshotError> {
        peer.authorize(&binding, false)?;
        Self::begin_local(binding, manifest, destination, max_bytes, keys).await
    }

    /// Starts local installation of an authenticated native backup artifact.
    /// The caller verifies its backup manifest before handing physical bytes to
    /// this admitted follower assignment.
    ///
    /// # Errors
    /// Rejects invalid authority, manifests, quotas, or storage failures.
    pub async fn begin_local(
        binding: WalReplicaBinding,
        manifest: WalSnapshotManifest,
        destination: PathBuf,
        max_bytes: u64,
        keys: Option<Arc<dyn terrabase::encryption::DatabaseKeySource>>,
    ) -> Result<Self, WalSnapshotError> {
        binding.authorize_local(false).map_err(WalQuicError::Route)?;
        manifest.authorize(&binding)?;
        let total = manifest
            .contents
            .files
            .iter()
            .flatten()
            .try_fold(0u64, |sum, file| sum.checked_add(file.bytes))
            .ok_or(WalSnapshotError::QuotaExceeded)?;
        if total > max_bytes {
            return Err(WalSnapshotError::QuotaExceeded);
        }
        let permit = binding.reserve_worker().map_err(WalQuicError::Route)?;
        let worker_binding = binding.clone();
        let state = tokio::task::spawn_blocking(move || {
            let _permit = permit;
            worker_binding
                .with_admission(|| {
                    let parent = destination.parent().ok_or(WalRouteError::Unauthorized)?;
                    if let Some(keys) = &keys {
                        let lease =
                            keys.current_lease().map_err(|_| WalRouteError::Unauthorized)?;
                        let key_scope = lease.metadata().scope();
                        if key_scope.tenant() != manifest.scope.tenant()
                            || key_scope.project() != Some(manifest.scope.project())
                            || key_scope.database() != manifest.scope.database()
                            || key_scope.purpose() != terrabase::encryption::KeyPurpose::Data
                        {
                            return Err(WalRouteError::Unauthorized);
                        }
                    }
                    let staging_path = parent.join(format!(
                        ".wal-snapshot-{}",
                        terrabase::identity::DatabaseUlid::generate()
                    ));
                    let mut builder = std::fs::DirBuilder::new();
                    #[cfg(unix)]
                    {
                        use std::os::unix::fs::DirBuilderExt as _;
                        builder.mode(0o700);
                    }
                    builder
                        .create(&staging_path)
                        .map_err(|error| WalRouteError::Storage(error.into()))?;
                    let encryption = keys
                        .clone()
                        .map(|keys| EncryptedStorage::register(&staging_path, keys))
                        .transpose()
                        .map_err(|error| WalRouteError::Storage(error.into()))?;
                    let staging = SnapshotDirectory {
                        path: staging_path,
                        _encryption: encryption,
                        retain: false,
                    };
                    for (slot, file) in manifest.contents.files.iter().enumerate() {
                        if file.is_some() {
                            create_physical_file(&staging.path.join(FILES[slot]))
                                .map_err(|error| WalRouteError::Storage(error.into()))?;
                            #[cfg(unix)]
                            {
                                use std::os::unix::fs::PermissionsExt as _;
                                std::fs::set_permissions(
                                    staging.path.join(FILES[slot]),
                                    std::fs::Permissions::from_mode(0o600),
                                )
                                .map_err(|error| WalRouteError::Storage(error.into()))?;
                            }
                        }
                    }
                    Ok(SinkState { staging, destination, manifest, received: [0; 3], keys })
                })
                .map_err(|error| WalSnapshotError::Admission(WalQuicError::Route(error)))
        })
        .await
        .map_err(|_| WalQuicError::WorkerUnavailable)??;
        Ok(Self { binding, state: Arc::new(Mutex::new(state)) })
    }

    /// Writes and syncs a contiguous chunk. Exact already-synced retransmissions
    /// are compared byte-for-byte and accepted without advancing twice.
    ///
    /// # Errors
    /// Rejects gaps, overlaps, altered retries, wrong scope/epoch, or disk failure.
    pub async fn write(
        &mut self,
        peer: &WalAuthenticatedPeer,
        chunk: WalSnapshotChunk,
    ) -> Result<u64, WalSnapshotError> {
        peer.authorize(&self.binding, false)?;
        self.write_local(chunk).await
    }

    /// Writes an authenticated native backup chunk to the admitted local sink.
    ///
    /// # Errors
    /// Rejects fences, invalid chunk ranges or hashes, and disk failures.
    pub async fn write_local(&mut self, chunk: WalSnapshotChunk) -> Result<u64, WalSnapshotError> {
        self.binding.authorize_local(false).map_err(WalQuicError::Route)?;
        let permit = self.binding.reserve_worker().map_err(WalQuicError::Route)?;
        let binding = self.binding.clone();
        let state = self.state.clone();
        tokio::task::spawn_blocking(move || {
            let _permit = permit;
            binding
                .with_admission(|| {
                    let mut state = state.lock().map_err(|_| WalRouteError::Fenced)?;
                    apply_chunk(&mut state, chunk).map_err(|error| match error {
                        WalSnapshotError::Storage(error) => WalRouteError::Storage(error),
                        _ => WalRouteError::Unauthorized,
                    })
                })
                .map_err(|error| WalSnapshotError::Admission(WalQuicError::Route(error)))
        })
        .await
        .map_err(|_| WalQuicError::WorkerUnavailable)?
    }

    /// Verifies and durably installs the complete snapshot before producing a
    /// persistent follower and scoped restart receipt. The destination must be
    /// absent or empty; an existing replica is never overwritten implicitly.
    ///
    /// # Errors
    /// Rejects incomplete files, digest mismatch, failed integrity or checkpoint
    /// validation, fenced assignments, and failed durable publication.
    pub async fn finish(
        self,
        peer: &WalAuthenticatedPeer,
    ) -> Result<(WalFollowerDriver, WalCheckpointMessage), WalSnapshotError> {
        peer.authorize(&self.binding, false)?;
        self.finish_local().await
    }

    /// Replaces the consumed physical follower at its fixed catalog location
    /// using Linux's atomic directory exchange. The old files remain at the
    /// private stage until the parent directory sync and new-file recovery pass.
    /// No SQLite engine or other follower may own either directory.
    ///
    /// # Errors
    /// Rejects invalid chunks, mismatched old assignments/paths, open workers,
    /// failed integrity, atomic exchange, sync, or recovery. A post-exchange
    /// failure preserves the retired directory and issues no receipt.
    pub async fn finish_replacing(
        self,
        peer: &WalAuthenticatedPeer,
        old: WalFollowerDriver,
    ) -> Result<(WalFollowerDriver, WalCheckpointMessage), WalSnapshotError> {
        peer.authorize(&self.binding, false)?;
        self.finish_replacing_admitted(old).await
    }

    async fn finish_replacing_admitted(
        self,
        old: WalFollowerDriver,
    ) -> Result<(WalFollowerDriver, WalCheckpointMessage), WalSnapshotError> {
        self.binding.authorize_local(false).map_err(WalQuicError::Route)?;
        let permit = self.binding.reserve_worker().map_err(WalQuicError::Route)?;
        let binding = self.binding.clone();
        let state = self.state.clone();
        let (follower, receipt, proof) = tokio::task::spawn_blocking(move || {
            let _permit = permit;
            binding
                .with_admission(|| {
                    let mut state = state.lock().map_err(|_| WalRouteError::Fenced)?;
                    for (slot, expected) in state.manifest.contents.files.iter().enumerate() {
                        if let Some(expected) = expected {
                            if state.received[slot] != expected.bytes {
                                return Err(WalRouteError::Unauthorized);
                            }
                            let mut file =
                                open_physical_file(&state.staging.path.join(FILES[slot]))
                                    .map_err(|error| WalRouteError::Storage(error.into()))?;
                            if file_digest(&mut file).map_err(WalRouteError::Storage)?
                                != expected.digest
                            {
                                return Err(WalRouteError::Unauthorized);
                            }
                        }
                    }
                    let (verified, checkpoint) = WalFollower::open_existing(&state.staging.path)
                        .map_err(WalRouteError::Storage)?;
                    drop(verified);
                    if checkpoint != state.manifest.contents.checkpoint {
                        return Err(WalRouteError::Unauthorized);
                    }
                    old.retire_for_replacement(&binding, &state.destination)?;
                    // Both VFS registrations must release path-bound handles before
                    // renaming. The admitted source itself survives in `keys`.
                    state.staging._encryption = None;
                    state.staging.retain = true;
                    rustix::fs::renameat_with(
                        rustix::fs::CWD,
                        &state.staging.path,
                        rustix::fs::CWD,
                        &state.destination,
                        rustix::fs::RenameFlags::EXCHANGE,
                    )
                    .map_err(|error| WalRouteError::Storage(std::io::Error::from(error).into()))?;
                    let parent = state.destination.parent().ok_or(WalRouteError::Unauthorized)?;
                    std::fs::File::open(parent)
                        .and_then(|file| file.sync_all())
                        .map_err(|error| WalRouteError::Storage(error.into()))?;
                    let _registration = state
                        .keys
                        .clone()
                        .map(|keys| EncryptedStorage::register(&state.destination, keys))
                        .transpose()
                        .map_err(|error| WalRouteError::Storage(error.into()))?;
                    let (follower, installed) = WalFollower::open_existing(&state.destination)
                        .map_err(WalRouteError::Storage)?;
                    if installed != checkpoint {
                        return Err(WalRouteError::Unauthorized);
                    }
                    state.staging.retain = false;
                    let checkpoint_bytes =
                        serde_json::to_vec(&installed).map_err(|_| WalRouteError::Unauthorized)?;
                    let proof = WalInstalledSnapshotProof {
                        scope: state.manifest.scope,
                        generation: binding.generation(),
                        epoch: state.manifest.replica_epoch,
                        snapshot: state.manifest.identity,
                        checkpoint: *blake3::hash(&checkpoint_bytes).as_bytes(),
                        sequence: state.manifest.contents.sequence,
                    };
                    let receipt = WalCheckpointMessage {
                        scope: state.manifest.scope,
                        replica_epoch: state.manifest.replica_epoch,
                        checkpoint: Some(checkpoint),
                    };
                    Ok((follower, receipt, proof))
                })
                .map_err(|error| WalSnapshotError::Admission(WalQuicError::Route(error)))
        })
        .await
        .map_err(|_| WalQuicError::WorkerUnavailable)??;
        Ok((
            WalFollowerDriver::new(self.binding.clone(), follower).with_installed_snapshot(proof),
            receipt,
        ))
    }

    /// Verifies and installs authenticated native backup bytes under the
    /// accepted follower assignment. This does not admit public serving.
    ///
    /// # Errors
    /// Rejects incomplete data, invalid checksums, fences, and disk failures.
    pub async fn finish_local(
        self,
    ) -> Result<(WalFollowerDriver, WalCheckpointMessage), WalSnapshotError> {
        self.binding.authorize_local(false).map_err(WalQuicError::Route)?;
        let permit = self.binding.reserve_worker().map_err(WalQuicError::Route)?;
        let binding = self.binding.clone();
        let state = self.state.clone();
        let (follower, receipt, proof) = tokio::task::spawn_blocking(move || {
            let _permit = permit;
            binding
                .with_admission(|| {
                    let state = state.lock().map_err(|_| WalRouteError::Fenced)?;
                    for (slot, expected) in state.manifest.contents.files.iter().enumerate() {
                        if let Some(expected) = expected {
                            if state.received[slot] != expected.bytes {
                                return Err(WalRouteError::Unauthorized);
                            }
                            let mut file =
                                open_physical_file(&state.staging.path.join(FILES[slot]))
                                    .map_err(|error| WalRouteError::Storage(error.into()))?;
                            if file_digest(&mut file).map_err(WalRouteError::Storage)?
                                != expected.digest
                            {
                                return Err(WalRouteError::Unauthorized);
                            }
                        }
                    }
                    let (_, checkpoint) = WalFollower::open_existing(&state.staging.path)
                        .map_err(WalRouteError::Storage)?;
                    if checkpoint != state.manifest.contents.checkpoint {
                        return Err(WalRouteError::Unauthorized);
                    }
                    let (follower, installed) =
                        WalFollower::bootstrap(&state.staging.path, &state.destination)
                            .map_err(WalRouteError::Storage)?;
                    if installed != checkpoint {
                        return Err(WalRouteError::Unauthorized);
                    }
                    let receipt = WalCheckpointMessage {
                        scope: state.manifest.scope,
                        replica_epoch: state.manifest.replica_epoch,
                        checkpoint: Some(checkpoint),
                    };
                    let checkpoint_bytes =
                        serde_json::to_vec(&installed).map_err(|_| WalRouteError::Unauthorized)?;
                    let proof = WalInstalledSnapshotProof {
                        generation: binding.generation(),
                        scope: state.manifest.scope,
                        epoch: state.manifest.replica_epoch,
                        snapshot: state.manifest.identity,
                        checkpoint: *blake3::hash(&checkpoint_bytes).as_bytes(),
                        sequence: state.manifest.contents.sequence,
                    };
                    Ok((follower, receipt, proof))
                })
                .map_err(|error| WalSnapshotError::Admission(WalQuicError::Route(error)))
        })
        .await
        .map_err(|_| WalQuicError::WorkerUnavailable)??;
        Ok((
            WalFollowerDriver::new(self.binding.clone(), follower).with_installed_snapshot(proof),
            receipt,
        ))
    }
}

fn apply_chunk(state: &mut SinkState, chunk: WalSnapshotChunk) -> Result<u64, WalSnapshotError> {
    authorize_request(&state.manifest, &chunk.request)?;
    if chunk.bytes.is_empty() || chunk.bytes.len() > MAX_WAL_BATCH_BYTES {
        return Err(WalSnapshotError::InvalidSnapshot);
    }
    let slot = usize::from(chunk.request.file);
    let size =
        state.manifest.file_bytes(chunk.request.file).ok_or(WalSnapshotError::InvalidSnapshot)?;
    let end = chunk
        .request
        .offset
        .checked_add(chunk.bytes.len() as u64)
        .filter(|end| *end <= size)
        .ok_or(WalSnapshotError::InvalidSnapshot)?;
    let mut file = open_physical_file(&state.staging.path.join(FILES[slot]))?;
    file.seek(SeekFrom::Start(chunk.request.offset))?;
    if chunk.request.offset < state.received[slot] {
        if end > state.received[slot] {
            return Err(WalSnapshotError::InvalidSnapshot);
        }
        let mut prior = vec![0; chunk.bytes.len()];
        file.read_exact(&mut prior)?;
        if prior != chunk.bytes {
            return Err(WalSnapshotError::InvalidSnapshot);
        }
        return Ok(state.received[slot]);
    }
    if chunk.request.offset != state.received[slot] {
        return Err(WalSnapshotError::InvalidSnapshot);
    }
    file.write_all(&chunk.bytes)?;
    file.sync_all()?;
    state.received[slot] = end;
    Ok(end)
}

fn authorize_request(
    manifest: &WalSnapshotManifest,
    request: &WalSnapshotRequest,
) -> Result<(), WalSnapshotError> {
    if request.scope != manifest.scope
        || request.replica_epoch != manifest.replica_epoch
        || request.identity != manifest.identity
        || usize::from(request.file) >= FILES.len()
    {
        return Err(WalSnapshotError::InvalidSnapshot);
    }
    Ok(())
}

fn file_digest(file: &mut PhysicalFile) -> Result<[u8; 32], WalFollowerError> {
    file.rewind()?;
    let mut hasher = blake3::Hasher::new();
    let mut buffer = vec![0; MAX_WAL_BATCH_BYTES];
    loop {
        let count = file.read(&mut buffer)?;
        if count == 0 {
            break;
        }
        hasher.update(&buffer[..count]);
    }
    Ok(*hasher.finalize().as_bytes())
}

fn manifest_identity(
    scope: DatabaseRouteScope,
    epoch: u64,
    contents: &ManifestContents,
) -> Result<[u8; 32], WalSnapshotError> {
    let mut hasher = blake3::Hasher::new();
    hasher.update(&prefix(b"TBWSM\0\0\x01", scope, epoch)?);
    hasher.update(&serde_json::to_vec(contents).map_err(|_| WalSnapshotError::InvalidSnapshot)?);
    Ok(*hasher.finalize().as_bytes())
}

fn prefix(
    magic: &[u8; 8],
    scope: DatabaseRouteScope,
    epoch: u64,
) -> Result<Vec<u8>, WalSnapshotError> {
    if epoch == 0 {
        return Err(WalSnapshotError::InvalidSnapshot);
    }
    let mut bytes = magic.to_vec();
    for id in [scope.tenant().to_text(), scope.project().to_text(), scope.database().to_text()] {
        bytes.extend_from_slice(id.as_bytes());
    }
    bytes.extend_from_slice(&epoch.to_be_bytes());
    Ok(bytes)
}

fn decode_prefix(
    bytes: &[u8],
    magic: &[u8; 8],
) -> Result<(DatabaseRouteScope, u64), WalSnapshotError> {
    if bytes.len() < PREFIX || &bytes[..8] != magic {
        return Err(WalSnapshotError::InvalidSnapshot);
    }
    let id = |start| {
        std::str::from_utf8(&bytes[start..start + 26])
            .map_err(|_| WalSnapshotError::InvalidSnapshot)
    };
    let scope = DatabaseRouteScope::try_new(id(8)?, id(34)?, id(60)?)
        .map_err(|_| WalSnapshotError::InvalidSnapshot)?;
    let epoch = u64::from_be_bytes(
        bytes[86..94].try_into().map_err(|_| WalSnapshotError::InvalidSnapshot)?,
    );
    if epoch == 0 {
        return Err(WalSnapshotError::Admission(WalQuicError::Wire(WalWireError::InvalidScope)));
    }
    Ok((scope, epoch))
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::production::{MemberWorkloadIdentity, ProductionPlacement, ProductionRegionSeats};
    use terrabase::identity::MemberUlid;
    use terrabase::types::{
        CollectionName, Document, DocumentId, DocumentValue, ExpectedVersion, LocalDurability,
        OperationId, TenantId,
    };
    use terrabase::{Engine, EngineConfig, RegionCode};

    #[test]
    fn native_snapshot_transfer_is_bounded_verified_and_durable() {
        let writer_id = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
        let follower_id = "01ARZ3NDEKTSV4RRFFQ69G5FAW";
        let scope = DatabaseRouteScope::try_new(writer_id, writer_id, writer_id).unwrap();
        let binding = |local| {
            WalReplicaBinding::new(
                scope,
                &ProductionPlacement::try_new(
                    MemberUlid::from_text(local).unwrap(),
                    1,
                    7,
                    [ProductionRegionSeats::try_new(
                        RegionCode::GRU,
                        [
                            MemberUlid::from_text(writer_id).unwrap(),
                            MemberUlid::from_text(follower_id).unwrap(),
                        ],
                    )
                    .unwrap()],
                )
                .unwrap(),
                MemberWorkloadIdentity::try_new("prod", "cluster", writer_id).unwrap(),
                MemberWorkloadIdentity::try_new("prod", "cluster", follower_id).unwrap(),
            )
            .unwrap()
        };
        let root = std::env::temp_dir().join(format!(
            "tb-snapshot-{}-{}",
            std::process::id(),
            std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap().as_nanos()
        ));
        let engine =
            Arc::new(Engine::open(EngineConfig::durable(root.join("writer")).unwrap()).unwrap());
        let tenant = engine.scope_for(&TenantId::try_new(writer_id).unwrap());
        let mut tx = engine
            .begin(
                &tenant,
                engine.read_snapshot(&tenant).unwrap(),
                OperationId::try_new("snapshot-doc").unwrap(),
            )
            .unwrap();
        tx.put(
            Document::new(
                DocumentId::try_new("doc").unwrap(),
                CollectionName::try_new("docs").unwrap(),
                DocumentValue::String("snapshot-content".into()),
            ),
            ExpectedVersion::New,
        )
        .unwrap();
        assert!(matches!(
            tx.commit(LocalDurability::Synced),
            terrabase::engine::CommitOutcome::Committed(_)
        ));
        let runtime = tokio::runtime::Builder::new_current_thread().enable_time().build().unwrap();
        runtime.block_on(async {
            let source =
                WalSnapshotSource::capture(binding(writer_id), engine.clone(), root.join("source"))
                    .await
                    .unwrap();
            let manifest =
                WalSnapshotManifest::decode(&source.manifest().encode().unwrap()).unwrap();
            assert!(manifest.sequence() > 0);
            assert!(WalSnapshotSink::begin_local(
                binding(follower_id),
                manifest.clone(),
                root.join("too-small"),
                1,
                None
            )
            .await
            .is_err());
            let mut sink = WalSnapshotSink::begin_local(
                binding(follower_id),
                manifest.clone(),
                root.join("installed"),
                64 * 1024 * 1024,
                None,
            )
            .await
            .unwrap();
            for slot in 0..3 {
                let mut offset = 0;
                while let Some(size) = manifest.file_bytes(slot) {
                    if offset == size {
                        break;
                    }
                    let chunk = source.read_local(slot, offset).await.unwrap();
                    assert!(chunk.bytes().len() <= MAX_WAL_BATCH_BYTES);
                    let encoded = chunk.encode().unwrap();
                    let mut corrupt = encoded.clone();
                    *corrupt.last_mut().unwrap() ^= 1;
                    assert!(WalSnapshotChunk::decode(&corrupt).is_err());
                    let wire = WalSnapshotChunk::decode(&encoded).unwrap();
                    offset = sink.write_local(wire.clone()).await.unwrap();
                    assert_eq!(sink.write_local(wire).await.unwrap(), offset);
                }
            }
            let (follower, receipt) = sink.finish_local().await.unwrap();
            assert_eq!(receipt.checkpoint, Some(manifest.checkpoint()));
            let proof = follower.installed_snapshot().unwrap();
            assert_eq!(proof.snapshot_digest(), manifest.identity());
            assert_eq!(proof.scope(), scope);
            assert_eq!(proof.sequence(), manifest.sequence());
            let replacement_binding = binding(follower_id);
            let mut replacement = WalSnapshotSink::begin_local(
                replacement_binding.clone(),
                manifest.clone(),
                root.join("installed"),
                64 * 1024 * 1024,
                None,
            )
            .await
            .unwrap();
            for slot in 0..3 {
                let mut offset = 0;
                while let Some(size) = manifest.file_bytes(slot) {
                    if offset == size {
                        break;
                    }
                    offset = replacement
                        .write_local(source.read_local(slot, offset).await.unwrap())
                        .await
                        .unwrap();
                }
            }
            let (mut replaced, replacement_receipt) =
                replacement.finish_replacing_admitted(follower).await.unwrap();
            assert_eq!(replacement_receipt.checkpoint, Some(manifest.checkpoint()));
            assert_eq!(
                replaced.installed_snapshot().unwrap().snapshot_digest(),
                manifest.identity()
            );
            let before = replaced.physical_boundary().await.unwrap().unwrap();
            assert!(matches!(
                replaced.retired_physical_boundary().await,
                Err(WalQuicError::Route(WalRouteError::Unauthorized))
            ));
            let held = replacement_binding.reserve_worker().unwrap();
            replacement_binding.fence();
            let mut pending = Box::pin(replaced.retired_physical_boundary());
            assert!(tokio::time::timeout(std::time::Duration::from_millis(20), &mut pending)
                .await
                .is_err());
            drop(held);
            let retired = pending.await.unwrap();
            assert_eq!(retired.scope(), before.scope());
            assert_eq!(retired.generation(), before.generation());
            assert_eq!(retired.epoch(), before.epoch());
            assert_eq!(retired.snapshot_digest(), before.snapshot_digest());
            assert_eq!(retired.checkpoint_digest(), before.checkpoint_digest());
            assert_eq!(retired.sequence(), before.sequence());

            drop(replaced);
            let missing_binding = binding(follower_id);
            let (raw, _) = WalFollower::open_existing(&root.join("installed")).unwrap();
            let mut missing = WalFollowerDriver::new(missing_binding.clone(), raw);
            missing_binding.fence();
            assert!(matches!(
                missing.retired_physical_boundary().await,
                Err(WalQuicError::Route(WalRouteError::Unauthorized))
            ));
            let (_, recovered) = WalFollower::open_existing(&root.join("installed")).unwrap();
            assert_eq!(recovered, manifest.checkpoint());
        });
        drop(runtime);
        drop(engine);
        std::fs::remove_dir_all(root).unwrap();
    }
}
