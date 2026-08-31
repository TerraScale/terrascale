//! Lease-backed durable replication members.
//!
//! This is the production-shaped companion to [`crate::compose`].  It does
//! not open a second engine: the caller lends it an already-admitted
//! `DatabaseLease`, and the lease remains held until [`DurableMember::drain`]
//! releases it.  Replica snapshots and an operation ledger are staged beside
//! that database, so replay and restart use the same durable identity.

use std::collections::{BTreeMap, BTreeSet};
use std::fs::{self, File, OpenOptions};
use std::io::Write;
use std::path::{Path, PathBuf};

use terrabase::CommitReceipt;
use terrabase::engine::{CommitOutcome, DurableResolution};
use terrabase::member::DatabaseLease;
use terrabase::types::{
    CollectionName, Document, DocumentId, DocumentValue, ExpectedVersion, LocalDurability,
    OperationId, TenantId,
};
use terrabase_causal::{CanonicalEncode, canonical_hash};

use crate::{
    ReplicaDoc, ReplicaError, ReplicaExchange, ReplicaFrontier, ReplicaScope, ReplicaValue,
};

const COLLECTION: &str = "documents";
const SNAPSHOT: &str = "replica.snapshot";
const OPERATIONS: &str = "replica.operations";
const STATE: &str = "replica.state";
const STATE_TEMP: &str = "replica.state.tmp";
const STATE_MAGIC: &[u8] = b"terrabase-durable-replica-state\0";
const STATE_VERSION: u8 = 1;
const MAX_STATE_BYTES: usize = 16 << 20;
const MAX_SNAPSHOT_BYTES: usize = 16 << 20;
const MAX_OPERATION_IDS: usize = 65_536;
const MAX_OPERATION_ID_BYTES: usize = 256;

type PersistedState = (Vec<u8>, BTreeSet<String>);

/// Durable progress exposed by one replica member.
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct DurableMemberProgress {
    stored: u64,
    applied: u64,
    verified: u64,
}

impl DurableMemberProgress {
    /// Latest engine publication sequence durably stored for this member.
    #[must_use]
    pub const fn stored(self) -> u64 {
        self.stored
    }

    /// Number of CRDT operations applied to this member's durable replica.
    #[must_use]
    pub const fn applied(self) -> u64 {
        self.applied
    }

    /// Number of operations verified by a completed peer exchange.
    #[must_use]
    pub const fn verified(self) -> u64 {
        self.verified
    }
}

/// Why a lease-backed durable replica operation failed.
#[derive(Debug)]
pub enum DurableMemberError {
    /// The engine refused an operation or read.
    Engine,
    /// The durable replica rejected an operation or snapshot.
    Replica(ReplicaError),
    /// An operation, document, tenant, or collection identifier was invalid.
    InvalidIdentifier,
    /// A durable sidecar could not be read or written.
    Persistence,
    /// Snapshot adoption would discard operations authored by the receiver.
    SnapshotAdoptionWouldDropOwnOps,
    /// The member has already been drained.
    Draining,
    /// A bounded exchange deadline elapsed.
    Timeout,
    /// The post-exchange Merkle roots did not verify.
    Verification,
}

impl From<ReplicaError> for DurableMemberError {
    fn from(error: ReplicaError) -> Self {
        Self::Replica(error)
    }
}

/// One database's engine lease and durable CRDT replica.
pub struct DurableMember {
    lease: Option<DatabaseLease>,
    database: terrabase::member::DatabaseId,
    location: PathBuf,
    tenant: TenantId,
    replica: ReplicaDoc,
    operations: BTreeSet<String>,
    known_documents: BTreeSet<String>,
    progress: DurableMemberProgress,
    reads: u64,
}

impl DurableMember {
    /// Opens a replica over an already-admitted database lease.
    ///
    /// # Errors
    ///
    /// Returns an error if the persisted replica state cannot be read or
    /// decoded, the snapshot cannot be restored, or the engine cannot report
    /// its published sequence.
    pub fn open(
        lease: DatabaseLease,
        member_identity: &str,
        tenant: TenantId,
    ) -> Result<Self, DurableMemberError> {
        let location = lease.location().to_path_buf();
        let actor = actor_for(member_identity);
        let (snapshot, operations) = if let Some((snapshot, operations)) = read_state(&location)? {
            (ReplicaDoc::from_snapshot(actor, &snapshot)?, operations)
        } else {
            // Read the old pair only when the atomic state record does
            // not exist. The next successful persist migrates it.
            let snapshot = match read_legacy_snapshot(&location)? {
                Some(bytes) => ReplicaDoc::from_snapshot(actor, &bytes)?,
                None => ReplicaDoc::new(actor),
            };
            (snapshot, read_legacy_operations(&location)?)
        };
        let known_documents = snapshot.materialized().into_keys().collect();
        let progress = DurableMemberProgress {
            stored: lease.published_sequence().map_err(|_| DurableMemberError::Engine)?.get(),
            applied: snapshot.operation_count(),
            verified: snapshot.operation_count(),
        };
        Ok(Self {
            database: lease.database_id().clone(),
            location,
            tenant,
            replica: snapshot,
            lease: Some(lease),
            operations,
            known_documents,
            progress,
            reads: 0,
        })
    }

    /// Returns this member's opaque database identity.
    #[must_use]
    pub fn database_id(&self) -> &terrabase::member::DatabaseId {
        &self.database
    }

    /// Returns this member's current causal frontier.
    #[must_use]
    pub fn frontier(&self) -> ReplicaFrontier {
        self.replica.frontier()
    }

    /// Returns this member's durable stored/applied/verified progress.
    #[must_use]
    pub const fn progress(&self) -> DurableMemberProgress {
        self.progress
    }

    /// Returns the dual Merkle roots of the current durable replica.
    ///
    /// # Errors
    ///
    /// Returns an error if the anti-entropy tree cannot be built.
    pub fn merkle_roots(&self) -> Result<crate::anti_entropy::MerkleRoots, DurableMemberError> {
        crate::anti_entropy::tree_for_replica(&self.replica, 64, 4096)
            .map(|tree| tree.roots())
            .map_err(|_| DurableMemberError::Verification)
    }

    /// Returns the merged durable replica state.
    #[must_use]
    pub fn merged_state(&self) -> ReplicaScope {
        self.replica.materialized()
    }

    /// Returns the engine-backed document, if present.
    ///
    /// # Errors
    ///
    /// Returns an error if the member is draining, an identifier is invalid,
    /// or the engine refuses the read transaction.
    pub fn read(
        &mut self,
        document: &str,
    ) -> Result<Option<BTreeMap<String, DocumentValue>>, DurableMemberError> {
        let lease = self.lease.as_ref().ok_or(DurableMemberError::Draining)?;
        self.reads = self.reads.saturating_add(1);
        let operation = OperationId::try_new(&format!(
            "replica-read-{}-{}",
            self.reads,
            self.database.as_str()
        ))
        .map_err(|_| DurableMemberError::InvalidIdentifier)?;
        let id =
            DocumentId::try_new(document).map_err(|_| DurableMemberError::InvalidIdentifier)?;
        let scope = lease.scope_for(&self.tenant);
        let snapshot = lease.read_snapshot(&scope).map_err(|_| DurableMemberError::Engine)?;
        let transaction =
            lease.begin(&scope, snapshot, operation).map_err(|_| DurableMemberError::Engine)?;
        let document = transaction.read(&id).map_err(|_| DurableMemberError::Engine)?;
        Ok(document.map(|document| match document.value() {
            DocumentValue::Object(body) => body.clone(),
            other => BTreeMap::from([("value".to_owned(), other.clone())]),
        }))
    }

    /// Applies one client-originated put to the engine and replica.
    ///
    /// # Errors
    ///
    /// Returns an error if the member is draining, input identifiers or
    /// capacity are invalid, the engine or replica rejects the write, or the
    /// durable replica state cannot be persisted.
    pub fn put(
        &mut self,
        operation: &str,
        document: &str,
        body: &BTreeMap<String, DocumentValue>,
    ) -> Result<CommitReceipt, DurableMemberError> {
        self.ensure_live()?;
        let operation_id =
            OperationId::try_new(operation).map_err(|_| DurableMemberError::InvalidIdentifier)?;
        let document_id =
            DocumentId::try_new(document).map_err(|_| DurableMemberError::InvalidIdentifier)?;
        let collection = CollectionName::try_new(COLLECTION)
            .map_err(|_| DurableMemberError::InvalidIdentifier)?;
        let lease = self.lease.as_ref().ok_or(DurableMemberError::Draining)?;
        let scope = lease.scope_for(&self.tenant);
        if self.operations.contains(operation) {
            return committed_receipt(lease, &scope, operation_id);
        }
        ensure_operation_capacity(&self.operations, operation)?;
        let snapshot = lease.read_snapshot(&scope).map_err(|_| DurableMemberError::Engine)?;
        let mut transaction = lease
            .begin(&scope, snapshot, operation_id.clone())
            .map_err(|_| DurableMemberError::Engine)?;
        transaction
            .put(
                Document::new(document_id, collection, DocumentValue::Object(body.clone())),
                ExpectedVersion::Any,
            )
            .map_err(|_| DurableMemberError::Engine)?;
        let receipt = match transaction.commit(LocalDurability::Synced) {
            CommitOutcome::Committed(receipt) => receipt,
            CommitOutcome::Rejected(_) | CommitOutcome::Unknown(_) => {
                return Err(DurableMemberError::Engine);
            }
        };
        let fields = body.iter().map(|(key, value)| (key.clone(), to_replica(value))).collect();
        self.replica.apply_put(now_millis(), document, &fields)?;
        self.operations.insert(operation.to_owned());
        self.known_documents.insert(document.to_owned());
        self.progress.stored = receipt.sequence().get();
        self.progress.applied = self.replica.operation_count();
        self.persist()?;
        Ok(receipt)
    }

    /// Applies one client-originated delete to the engine and replica.
    ///
    /// # Errors
    ///
    /// Returns an error if the member is draining, input identifiers or
    /// capacity are invalid, the engine or replica rejects the delete, or the
    /// durable replica state cannot be persisted.
    pub fn delete(
        &mut self,
        operation: &str,
        document: &str,
    ) -> Result<CommitReceipt, DurableMemberError> {
        self.ensure_live()?;
        let operation_id =
            OperationId::try_new(operation).map_err(|_| DurableMemberError::InvalidIdentifier)?;
        let document_id =
            DocumentId::try_new(document).map_err(|_| DurableMemberError::InvalidIdentifier)?;
        let lease = self.lease.as_ref().ok_or(DurableMemberError::Draining)?;
        let scope = lease.scope_for(&self.tenant);
        if self.operations.contains(operation) {
            return committed_receipt(lease, &scope, operation_id);
        }
        ensure_operation_capacity(&self.operations, operation)?;
        let snapshot = lease.read_snapshot(&scope).map_err(|_| DurableMemberError::Engine)?;
        let mut transaction =
            lease.begin(&scope, snapshot, operation_id).map_err(|_| DurableMemberError::Engine)?;
        transaction
            .delete(document_id, ExpectedVersion::Any)
            .map_err(|_| DurableMemberError::Engine)?;
        let receipt = match transaction.commit(LocalDurability::Synced) {
            CommitOutcome::Committed(receipt) => receipt,
            CommitOutcome::Rejected(_) | CommitOutcome::Unknown(_) => {
                return Err(DurableMemberError::Engine);
            }
        };
        self.replica.apply_delete(now_millis(), document)?;
        self.operations.insert(operation.to_owned());
        self.known_documents.insert(document.to_owned());
        self.progress.stored = receipt.sequence().get();
        self.progress.applied = self.replica.operation_count();
        self.persist()?;
        Ok(receipt)
    }

    /// Exchanges deltas in both directions, using snapshot fallback where a
    /// peer is behind the retained floor, then reconciles both engines.
    ///
    /// # Errors
    ///
    /// Returns an error if either member cannot exchange, reconcile, verify,
    /// or persist its replica state.
    pub fn reconcile_with(&mut self, other: &mut Self) -> Result<(), DurableMemberError> {
        self.reconcile_with_timeout(other, std::time::Duration::MAX)
    }

    /// Exchanges and reconciles with a peer before `timeout` elapses.
    ///
    /// # Errors
    ///
    /// Returns an error if the deadline elapses or either member cannot
    /// exchange, reconcile, verify, or persist its replica state.
    pub fn reconcile_with_timeout(
        &mut self,
        other: &mut Self,
        timeout: std::time::Duration,
    ) -> Result<(), DurableMemberError> {
        self.ensure_live()?;
        other.ensure_live()?;
        let started = std::time::Instant::now();
        loop {
            if started.elapsed() >= timeout {
                return Err(DurableMemberError::Timeout);
            }
            let mine = self.replica.frontier();
            let theirs = other.replica.frontier();
            if mine.covers(&theirs) && theirs.covers(&mine) {
                break;
            }
            exchange(self, other, &theirs)?;
            exchange(other, self, &mine)?;
        }
        if started.elapsed() >= timeout {
            return Err(DurableMemberError::Timeout);
        }
        self.reconcile_engine()?;
        other.reconcile_engine()?;
        if self.merkle_roots()? != other.merkle_roots()? {
            return Err(DurableMemberError::Verification);
        }
        self.progress.applied = self.replica.operation_count();
        other.progress.applied = other.replica.operation_count();
        self.progress.verified = self.progress.applied;
        other.progress.verified = other.progress.applied;
        self.persist()?;
        other.persist()?;
        Ok(())
    }

    /// Persists the final snapshot and releases the database lease.
    ///
    /// # Errors
    ///
    /// Returns an error if the replica state cannot be persisted.
    pub fn drain(&mut self) -> Result<(), DurableMemberError> {
        self.persist()?;
        self.lease.take();
        Ok(())
    }

    fn ensure_live(&self) -> Result<(), DurableMemberError> {
        self.lease.as_ref().map(|_| ()).ok_or(DurableMemberError::Draining)
    }

    fn reconcile_engine(&mut self) -> Result<(), DurableMemberError> {
        let merged = self.replica.materialized();
        let documents = merged
            .iter()
            .map(|(document, body)| {
                (
                    document.clone(),
                    body.iter()
                        .map(|(key, value)| (key.clone(), from_replica(value.clone())))
                        .collect::<BTreeMap<_, _>>(),
                )
            })
            .collect::<BTreeMap<_, _>>();
        for (document, body) in &documents {
            // Keep the engine-side document inventory even when this body was
            // already current. A later replicated delete must be able to
            // identify and remove every document materialized by a peer.
            self.known_documents.insert(document.clone());
            if self.read(document)?.as_ref() != Some(body) {
                let id = format!(
                    "replica-reconcile-{}-{}",
                    stable_hash(document),
                    self.replica.operation_count()
                );
                let receipt = self.engine_put(&id, document, body)?;
                self.progress.stored = receipt.sequence().get();
            }
        }
        for document in self.known_documents.clone() {
            if !documents.contains_key(&document) && self.read(&document)?.is_some() {
                let id = format!(
                    "replica-reconcile-delete-{}-{}",
                    stable_hash(&document),
                    self.replica.operation_count()
                );
                let receipt = self.engine_delete(&id, &document)?;
                self.progress.stored = receipt.sequence().get();
            }
        }
        Ok(())
    }

    fn engine_put(
        &self,
        operation: &str,
        document: &str,
        body: &BTreeMap<String, DocumentValue>,
    ) -> Result<CommitReceipt, DurableMemberError> {
        let operation_id =
            OperationId::try_new(operation).map_err(|_| DurableMemberError::InvalidIdentifier)?;
        let document_id =
            DocumentId::try_new(document).map_err(|_| DurableMemberError::InvalidIdentifier)?;
        let collection = CollectionName::try_new(COLLECTION)
            .map_err(|_| DurableMemberError::InvalidIdentifier)?;
        let lease = self.lease.as_ref().ok_or(DurableMemberError::Draining)?;
        let scope = lease.scope_for(&self.tenant);
        let snapshot = lease.read_snapshot(&scope).map_err(|_| DurableMemberError::Engine)?;
        let mut transaction =
            lease.begin(&scope, snapshot, operation_id).map_err(|_| DurableMemberError::Engine)?;
        transaction
            .put(
                Document::new(document_id, collection, DocumentValue::Object(body.clone())),
                ExpectedVersion::Any,
            )
            .map_err(|_| DurableMemberError::Engine)?;
        match transaction.commit(LocalDurability::Synced) {
            CommitOutcome::Committed(receipt) => Ok(receipt),
            CommitOutcome::Rejected(_) | CommitOutcome::Unknown(_) => {
                Err(DurableMemberError::Engine)
            }
        }
    }

    fn engine_delete(
        &self,
        operation: &str,
        document: &str,
    ) -> Result<CommitReceipt, DurableMemberError> {
        let operation_id =
            OperationId::try_new(operation).map_err(|_| DurableMemberError::InvalidIdentifier)?;
        let document_id =
            DocumentId::try_new(document).map_err(|_| DurableMemberError::InvalidIdentifier)?;
        let lease = self.lease.as_ref().ok_or(DurableMemberError::Draining)?;
        let scope = lease.scope_for(&self.tenant);
        let snapshot = lease.read_snapshot(&scope).map_err(|_| DurableMemberError::Engine)?;
        let mut transaction =
            lease.begin(&scope, snapshot, operation_id).map_err(|_| DurableMemberError::Engine)?;
        transaction
            .delete(document_id, ExpectedVersion::Any)
            .map_err(|_| DurableMemberError::Engine)?;
        match transaction.commit(LocalDurability::Synced) {
            CommitOutcome::Committed(receipt) => Ok(receipt),
            CommitOutcome::Rejected(_) | CommitOutcome::Unknown(_) => {
                Err(DurableMemberError::Engine)
            }
        }
    }

    fn persist(&mut self) -> Result<(), DurableMemberError> {
        let snapshot = self.replica.snapshot_bytes()?;
        let bytes = encode_state(&snapshot, &self.operations)?;
        let temporary = self.location.join(STATE_TEMP);
        let mut options = OpenOptions::new();
        options.create_new(true).write(true);
        #[cfg(unix)]
        std::os::unix::fs::OpenOptionsExt::custom_flags(&mut options, 0x20000);
        let mut file = match options.open(&temporary) {
            Ok(file) => file,
            Err(error) if error.kind() == std::io::ErrorKind::AlreadyExists => {
                let metadata = fs::symlink_metadata(&temporary)
                    .map_err(|_| DurableMemberError::Persistence)?;
                if metadata.file_type().is_symlink() {
                    return Err(DurableMemberError::Persistence);
                }
                fs::remove_file(&temporary).map_err(|_| DurableMemberError::Persistence)?;
                let mut retry = OpenOptions::new();
                retry.create_new(true).write(true);
                #[cfg(unix)]
                std::os::unix::fs::OpenOptionsExt::custom_flags(&mut retry, 0x20000);
                retry.open(&temporary).map_err(|_| DurableMemberError::Persistence)?
            }
            Err(_) => return Err(DurableMemberError::Persistence),
        };
        file.write_all(&bytes).map_err(|_| DurableMemberError::Persistence)?;
        file.sync_all().map_err(|_| DurableMemberError::Persistence)?;
        drop(file);
        fs::rename(&temporary, self.location.join(STATE))
            .map_err(|_| DurableMemberError::Persistence)?;
        File::open(&self.location)
            .and_then(|directory| directory.sync_all())
            .map_err(|_| DurableMemberError::Persistence)
    }
}

fn exchange(
    source: &mut DurableMember,
    target: &mut DurableMember,
    frontier: &ReplicaFrontier,
) -> Result<(), DurableMemberError> {
    match source.replica.delta_for(frontier)? {
        ReplicaExchange::UpToDate => Ok(()),
        ReplicaExchange::Delta(bytes) => target.replica.absorb(&bytes).map_err(Into::into),
        ReplicaExchange::BehindRetainedFloor { .. } => {
            if !target.replica.export_own_ops()?.is_empty() {
                return Err(DurableMemberError::SnapshotAdoptionWouldDropOwnOps);
            }
            let snapshot = source.replica.snapshot_bytes()?;
            let actor = target.replica.actor();
            target.replica = ReplicaDoc::from_snapshot(actor, &snapshot)?;
            Ok(())
        }
    }
}

fn committed_receipt(
    engine: &DatabaseLease,
    scope: &terrabase::TenantScope,
    operation: OperationId,
) -> Result<CommitReceipt, DurableMemberError> {
    match engine.resolve_commit(scope, operation).map_err(|_| DurableMemberError::Engine)? {
        DurableResolution::Committed(receipt) => Ok(receipt),
        DurableResolution::Rejected(_) | DurableResolution::Pending => {
            Err(DurableMemberError::Engine)
        }
    }
}

struct ChecksumInput<'a>(&'a [u8]);

impl CanonicalEncode for ChecksumInput<'_> {
    fn encode_canonical(&self, output: &mut Vec<u8>) {
        output.extend_from_slice(&(self.0.len() as u64).to_le_bytes());
        output.extend_from_slice(self.0);
    }
}

fn encode_state(
    snapshot: &[u8],
    operations: &BTreeSet<String>,
) -> Result<Vec<u8>, DurableMemberError> {
    if snapshot.len() > MAX_SNAPSHOT_BYTES || operations.len() > MAX_OPERATION_IDS {
        return Err(DurableMemberError::Persistence);
    }
    let snapshot_len =
        u64::try_from(snapshot.len()).map_err(|_| DurableMemberError::Persistence)?;
    let operation_count =
        u32::try_from(operations.len()).map_err(|_| DurableMemberError::Persistence)?;
    let mut body = Vec::with_capacity(STATE_MAGIC.len() + 1 + 12 + snapshot.len());
    body.extend_from_slice(STATE_MAGIC);
    body.push(STATE_VERSION);
    body.extend_from_slice(&snapshot_len.to_le_bytes());
    body.extend_from_slice(&operation_count.to_le_bytes());
    body.extend_from_slice(snapshot);
    for operation in operations {
        validate_operation_id(operation)?;
        let length = u32::try_from(operation.len()).map_err(|_| DurableMemberError::Persistence)?;
        body.extend_from_slice(&length.to_le_bytes());
        body.extend_from_slice(operation.as_bytes());
    }
    let checksum = canonical_hash("durable-replica-state", &ChecksumInput(&body))
        .map_err(|_| DurableMemberError::Persistence)?;
    body.extend_from_slice(&checksum);
    if body.len() > MAX_STATE_BYTES {
        return Err(DurableMemberError::Persistence);
    }
    Ok(body)
}

fn read_state(location: &Path) -> Result<Option<PersistedState>, DurableMemberError> {
    let path = location.join(STATE);
    let metadata = match fs::symlink_metadata(&path) {
        Ok(metadata) => metadata,
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => return Ok(None),
        Err(_) => return Err(DurableMemberError::Persistence),
    };
    if metadata.file_type().is_symlink() || !metadata.file_type().is_file() {
        return Err(DurableMemberError::Persistence);
    }
    let bytes = fs::read(path).map_err(|_| DurableMemberError::Persistence)?;
    decode_state(&bytes).map(Some)
}

fn decode_state(bytes: &[u8]) -> Result<(Vec<u8>, BTreeSet<String>), DurableMemberError> {
    if bytes.len() > MAX_STATE_BYTES || bytes.len() < 32 {
        return Err(DurableMemberError::Persistence);
    }
    let split = bytes.len() - 32;
    let (body, checksum) = bytes.split_at(split);
    let expected = canonical_hash("durable-replica-state", &ChecksumInput(body))
        .map_err(|_| DurableMemberError::Persistence)?;
    if checksum != expected {
        return Err(DurableMemberError::Persistence);
    }
    let mut cursor = 0;
    if take(body, &mut cursor, STATE_MAGIC.len()) != Some(STATE_MAGIC)
        || take(body, &mut cursor, 1) != Some(&[STATE_VERSION])
    {
        return Err(DurableMemberError::Persistence);
    }
    let snapshot_len = u64::from_le_bytes(
        take(body, &mut cursor, 8)
            .ok_or(DurableMemberError::Persistence)?
            .try_into()
            .map_err(|_| DurableMemberError::Persistence)?,
    );
    let snapshot_len =
        usize::try_from(snapshot_len).map_err(|_| DurableMemberError::Persistence)?;
    if snapshot_len > MAX_SNAPSHOT_BYTES {
        return Err(DurableMemberError::Persistence);
    }
    let count = u32::from_le_bytes(
        take(body, &mut cursor, 4)
            .ok_or(DurableMemberError::Persistence)?
            .try_into()
            .map_err(|_| DurableMemberError::Persistence)?,
    ) as usize;
    if count > MAX_OPERATION_IDS {
        return Err(DurableMemberError::Persistence);
    }
    let snapshot =
        take(body, &mut cursor, snapshot_len).ok_or(DurableMemberError::Persistence)?.to_vec();
    let mut operations = BTreeSet::new();
    for _ in 0..count {
        let length = u32::from_le_bytes(
            take(body, &mut cursor, 4)
                .ok_or(DurableMemberError::Persistence)?
                .try_into()
                .map_err(|_| DurableMemberError::Persistence)?,
        ) as usize;
        if length == 0 || length > MAX_OPERATION_ID_BYTES {
            return Err(DurableMemberError::Persistence);
        }
        let operation = std::str::from_utf8(
            take(body, &mut cursor, length).ok_or(DurableMemberError::Persistence)?,
        )
        .map_err(|_| DurableMemberError::Persistence)?;
        OperationId::try_new(operation).map_err(|_| DurableMemberError::Persistence)?;
        if !operations.insert(operation.to_owned()) {
            return Err(DurableMemberError::Persistence);
        }
    }
    if cursor != body.len() {
        return Err(DurableMemberError::Persistence);
    }
    Ok((snapshot, operations))
}

fn take<'a>(bytes: &'a [u8], cursor: &mut usize, length: usize) -> Option<&'a [u8]> {
    let end = cursor.checked_add(length)?;
    let value = bytes.get(*cursor..end)?;
    *cursor = end;
    Some(value)
}

fn read_legacy_snapshot(location: &Path) -> Result<Option<Vec<u8>>, DurableMemberError> {
    let path = location.join(SNAPSHOT);
    let metadata = match fs::symlink_metadata(&path) {
        Ok(metadata) => metadata,
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => return Ok(None),
        Err(_) => return Err(DurableMemberError::Persistence),
    };
    if metadata.file_type().is_symlink() || !metadata.file_type().is_file() {
        return Err(DurableMemberError::Persistence);
    }
    let bytes = fs::read(path).map_err(|_| DurableMemberError::Persistence)?;
    if bytes.len() > MAX_SNAPSHOT_BYTES {
        return Err(DurableMemberError::Persistence);
    }
    Ok(Some(bytes))
}

fn read_legacy_operations(location: &Path) -> Result<BTreeSet<String>, DurableMemberError> {
    let path = location.join(OPERATIONS);
    let metadata = match fs::symlink_metadata(&path) {
        Ok(metadata) => metadata,
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => return Ok(BTreeSet::new()),
        Err(_) => return Err(DurableMemberError::Persistence),
    };
    if metadata.file_type().is_symlink() || !metadata.file_type().is_file() {
        return Err(DurableMemberError::Persistence);
    }
    let bytes = fs::read(path).map_err(|_| DurableMemberError::Persistence)?;
    if bytes.len() > MAX_STATE_BYTES {
        return Err(DurableMemberError::Persistence);
    }
    let text = std::str::from_utf8(&bytes).map_err(|_| DurableMemberError::Persistence)?;
    let mut operations = BTreeSet::new();
    for operation in text.lines().filter(|line| !line.is_empty()) {
        ensure_operation_capacity(&operations, operation)?;
        if !operations.insert(operation.to_owned()) {
            return Err(DurableMemberError::Persistence);
        }
    }
    Ok(operations)
}

fn ensure_operation_capacity(
    operations: &BTreeSet<String>,
    operation: &str,
) -> Result<(), DurableMemberError> {
    validate_operation_id(operation)?;
    if operations.len() >= MAX_OPERATION_IDS {
        return Err(DurableMemberError::Persistence);
    }
    Ok(())
}

fn validate_operation_id(operation: &str) -> Result<(), DurableMemberError> {
    if operation.is_empty() || operation.len() > MAX_OPERATION_ID_BYTES {
        return Err(DurableMemberError::Persistence);
    }
    OperationId::try_new(operation).map_err(|_| DurableMemberError::Persistence)?;
    Ok(())
}

fn to_replica(value: &DocumentValue) -> ReplicaValue {
    match value {
        DocumentValue::Null => ReplicaValue::Null,
        DocumentValue::Boolean(value) => ReplicaValue::Boolean(*value),
        DocumentValue::Integer(value) => ReplicaValue::Integer(*value),
        DocumentValue::Number(value) => ReplicaValue::number(*value),
        DocumentValue::String(value) => ReplicaValue::Text(value.clone()),
        DocumentValue::Array(values) => {
            ReplicaValue::Array(values.iter().map(to_replica).collect())
        }
        DocumentValue::Object(values) => ReplicaValue::Object(
            values.iter().map(|(key, value)| (key.clone(), to_replica(value))).collect(),
        ),
    }
}

fn from_replica(value: ReplicaValue) -> DocumentValue {
    match value {
        ReplicaValue::Null => DocumentValue::Null,
        ReplicaValue::Boolean(value) => DocumentValue::Boolean(value),
        ReplicaValue::Integer(value) => DocumentValue::Integer(value),
        ReplicaValue::Number { bits } => DocumentValue::Number(f64::from_bits(bits)),
        ReplicaValue::Text(value) => DocumentValue::String(value),
        ReplicaValue::Array(values) => {
            DocumentValue::Array(values.into_iter().map(from_replica).collect())
        }
        ReplicaValue::Object(values) => DocumentValue::Object(
            values.into_iter().map(|(key, value)| (key, from_replica(value))).collect(),
        ),
    }
}

fn actor_for(identity: &str) -> terrabase_causal::ActorId {
    let mut hash = 0xcbf2_9ce4_8422_2325_u64;
    for byte in identity.as_bytes() {
        hash = (hash ^ u64::from(*byte)).wrapping_mul(0x0000_0100_0000_01B3);
    }
    crate::actor_from_peer(hash)
}

fn stable_hash(value: &str) -> u64 {
    value.bytes().fold(0xcbf2_9ce4_8422_2325_u64, |hash, byte| {
        (hash ^ u64::from(byte)).wrapping_mul(0x0000_0100_0000_01B3)
    })
}

fn now_millis() -> u64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map_or(u64::MAX, |duration| u64::try_from(duration.as_millis()).unwrap_or(u64::MAX))
}

#[cfg(test)]
mod tests {
    use super::*;
    use terrabase::member::{AdmissionLimits, DatabaseSpec, Member};

    fn root(tag: &str) -> PathBuf {
        let root = std::env::temp_dir().join(format!(
            "terrabase-durable-replica-{tag}-{}-{}",
            std::process::id(),
            now_millis()
        ));
        let _ = fs::remove_dir_all(&root);
        fs::create_dir_all(&root).expect("test root");
        root
    }

    fn member(root: &Path, name: &str) -> (Member, DurableMember) {
        let database = DatabaseSpec::new("database-a", "database-a");
        let catalog = Member::open(root, [database], AdmissionLimits::default()).expect("catalog");
        let lease = catalog
            .acquire(&terrabase::member::DatabaseId::try_new("database-a").expect("database"))
            .expect("lease");
        let tenant = TenantId::try_new("tenant-a").expect("tenant");
        let replica = DurableMember::open(lease, name, tenant).expect("replica");
        (catalog, replica)
    }

    fn body(value: &str) -> BTreeMap<String, DocumentValue> {
        BTreeMap::from([("value".to_owned(), DocumentValue::String(value.to_owned()))])
    }

    #[test]
    fn duplicate_operation_replays_one_durable_receipt() {
        let path = root("replay");
        let (catalog, mut replica) = member(&path, "member-a");
        let first = replica.put("operation-a", "document-a", &body("one")).expect("put");
        let second = replica.put("operation-a", "document-a", &body("one")).expect("replay");
        assert_eq!(first.sequence(), second.sequence());
        assert_eq!(replica.progress().applied(), 1);
        replica.drain().expect("drain");
        catalog.close(std::time::Duration::from_secs(1)).expect("close");
        let _ = fs::remove_dir_all(path);
    }

    #[test]
    fn atomic_state_record_rejects_truncation_and_trailing_bytes() {
        let operations = BTreeSet::from(["operation-a".to_owned()]);
        let encoded = encode_state(b"snapshot", &operations).expect("encode");
        let decoded = decode_state(&encoded).expect("decode");
        assert_eq!(decoded, (b"snapshot".to_vec(), operations));
        assert!(decode_state(&encoded[..encoded.len() - 1]).is_err());
        let mut trailing = encoded;
        trailing.push(0);
        assert!(decode_state(&trailing).is_err());
    }

    #[test]
    fn atomic_state_record_rejects_duplicate_or_oversized_operation_ids() {
        let duplicate = BTreeSet::from(["operation-a".to_owned()]);
        let mut encoded = encode_state(b"snapshot", &duplicate).expect("encode");
        // Altering the operation count without recomputing the checksum is a
        // malformed crash-state record and must fail closed.
        let count_offset = STATE_MAGIC.len() + 1 + 8;
        encoded[count_offset..count_offset + 4].copy_from_slice(&2_u32.to_le_bytes());
        assert!(decode_state(&encoded).is_err());

        let oversized = "x".repeat(MAX_OPERATION_ID_BYTES + 1);
        let operations = BTreeSet::from([oversized]);
        assert!(encode_state(b"snapshot", &operations).is_err());
    }

    #[cfg(unix)]
    #[test]
    fn atomic_state_record_rejects_symlink_authority() {
        let path = root("symlink");
        let target = path.join("target");
        fs::write(&target, b"not-a-state").expect("target");
        std::os::unix::fs::symlink(&target, path.join(STATE)).expect("symlink");
        assert!(read_state(&path).is_err());
        let _ = fs::remove_dir_all(path);
    }

    #[test]
    fn restart_recovers_snapshot_and_repairs_peer_engine() {
        let first_path = root("first");
        let second_path = root("second");
        let (first_catalog, mut first) = member(&first_path, "member-a");
        let (second_catalog, mut second) = member(&second_path, "member-b");
        first.put("operation-a", "document-a", &body("one")).expect("put");
        first.reconcile_with(&mut second).expect("repair");
        assert_eq!(second.read("document-a").expect("read"), Some(body("one")));
        assert_eq!(
            first.merkle_roots().expect("first roots"),
            second.merkle_roots().expect("second roots")
        );
        assert_eq!(first.progress().applied(), first.progress().verified());
        assert_eq!(second.progress().applied(), second.progress().verified());
        first.delete("operation-delete", "document-a").expect("delete");
        first.reconcile_with(&mut second).expect("delete repair");
        assert_eq!(second.read("document-a").expect("deleted read"), None);
        second.drain().expect("drain second");
        first.drain().expect("drain first");
        second_catalog.close(std::time::Duration::from_secs(1)).expect("close second");
        first_catalog.close(std::time::Duration::from_secs(1)).expect("close first");

        let (revived_second_catalog, mut revived_second) = member(&second_path, "member-b");
        assert_eq!(revived_second.read("document-a").expect("deleted read after restart"), None);
        revived_second.drain().expect("drain revived second");
        revived_second_catalog
            .close(std::time::Duration::from_secs(1))
            .expect("close revived second");
        let (revived_first_catalog, mut revived_first) = member(&first_path, "member-a");
        assert_eq!(revived_first.read("document-a").expect("recovered read"), None);
        revived_first.drain().expect("drain revived first");
        revived_first_catalog
            .close(std::time::Duration::from_secs(1))
            .expect("close revived first");
        let _ = fs::remove_dir_all(first_path);
        let _ = fs::remove_dir_all(second_path);
    }
}
