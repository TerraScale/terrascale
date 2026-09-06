//! Bounded, database-scoped physical snapshot bootstrap over admitted peers.
//!
//! The writer captures an immutable file pair under its engine barrier. Network
//! transfer runs after releasing the barrier. The receiver publishes only after
//! every file hash, SQLite integrity, and the scoped WAL checkpoint agree.
//! Destination paths are local configuration, never supplied by network frames.

use std::fs::{File, OpenOptions};
use std::io::{Read, Seek, SeekFrom, Write};
use std::path::PathBuf;
use terrabase::storage::{open_physical_file, create_physical_file, PhysicalFile, EncryptedStorage};
use std::sync::{Arc, Mutex};

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
}

impl WalSnapshotManifest {
    /// Returns the content identity bound to the database scope and epoch.
    #[must_use]
    pub fn identity(&self) -> [u8; 32] {
        self.identity
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
}
impl Drop for SnapshotDirectory {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.path);
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
        let permit = binding.reserve_worker().map_err(|error| WalQuicError::Route(error))?;
        let worker_binding = binding.clone();
        let (snapshot, manifest) = tokio::task::spawn_blocking(move || {
            let _permit = permit;
            worker_binding
                .with_admission(|| {
                    let (follower, checkpoint) =
                        WalFollower::bootstrap_engine(engine.engine(), &staging)
                            .map_err(WalRouteError::Storage)?;
                    drop(follower);
                    let snapshot = Arc::new(SnapshotDirectory { path: staging });
                    let mut files: [Option<FileManifest>; 3] = [None, None, None];
                    for (slot, name) in FILES.iter().enumerate() {
                        let path = snapshot.path.join(name);
                        match open_physical_file(&path) {
                            Ok(mut file) => {
                                files[slot] = Some(FileManifest {
                                    bytes: file
                                        .metadata()
                                        .map_err(|error| WalRouteError::Storage(error.into()))?
                                        .len(),
                                    digest: file_digest(&mut file)
                                        .map_err(WalRouteError::Storage)?,
                                });
                            }
                            Err(error)
                                if error.kind() == std::io::ErrorKind::NotFound && slot != 0 => {}
                            Err(error) => return Err(WalRouteError::Storage(error.into())),
                        }
                    }
                    Ok((snapshot, ManifestContents { files, checkpoint }))
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
                    let mut file = open_physical_file(&snapshot.path.join(FILES[usize::from(request.file)]))
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
    ) -> Result<Self, WalSnapshotError> {
        peer.authorize(&binding, false)?;
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
                    let staging = SnapshotDirectory { path: staging_path };
                    for (slot, file) in manifest.contents.files.iter().enumerate() {
                        if file.is_some() {
                            let mut options = OpenOptions::new();
                            options.write(true).create_new(true);
                            #[cfg(unix)]
                            {
                                use std::os::unix::fs::OpenOptionsExt as _;
                                options.mode(0o600);
                            }
                            options
                                .open(staging.path.join(FILES[slot]))
                                .map_err(|error| WalRouteError::Storage(error.into()))?;
                        }
                    }
                    Ok(SinkState { staging, destination, manifest, received: [0; 3] })
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
        let permit = self.binding.reserve_worker().map_err(WalQuicError::Route)?;
        let binding = self.binding.clone();
        let state = self.state.clone();
        let (follower, receipt) = tokio::task::spawn_blocking(move || {
            let _permit = permit;
            binding
                .with_admission(|| {
                    let state = state.lock().map_err(|_| WalRouteError::Fenced)?;
                    for (slot, expected) in state.manifest.contents.files.iter().enumerate() {
                        if let Some(expected) = expected {
                            if state.received[slot] != expected.bytes {
                                return Err(WalRouteError::Unauthorized);
                            }
                            let mut file = open_physical_file(&state.staging.path.join(FILES[slot]))
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
                    Ok((follower, receipt))
                })
                .map_err(|error| WalSnapshotError::Admission(WalQuicError::Route(error)))
        })
        .await
        .map_err(|_| WalQuicError::WorkerUnavailable)??;
        Ok((WalFollowerDriver::new(self.binding.clone(), follower), receipt))
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
    let mut file =
        OpenOptions::new().read(true).write(true).open(state.staging.path.join(FILES[slot]))?;
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
