//! Lease-backed durable replication members.
//!
//! This is the production-shaped companion to [`crate::compose`].  It does
//! not open a second engine: the caller lends it an already-admitted
//! `DatabaseLease`, and the lease remains held until [`DurableMember::drain`]
//! releases it.  Replica snapshots and an operation ledger are staged beside
//! that database, so replay and restart use the same durable identity.

use std::collections::{BTreeMap, BTreeSet};
use std::fs::{self, OpenOptions};
use std::io::Write;
use std::path::{Path, PathBuf};

use terrabase::CommitReceipt;
use terrabase::engine::{CommitOutcome, DurableResolution};
use terrabase::member::DatabaseLease;
use terrabase::types::{
    CollectionName, Document, DocumentId, DocumentValue, ExpectedVersion, LocalDurability,
    OperationId, TenantId,
};

use crate::{
    ReplicaDoc, ReplicaError, ReplicaExchange, ReplicaFrontier, ReplicaScope, ReplicaValue,
};

const COLLECTION: &str = "documents";
const SNAPSHOT: &str = "replica.snapshot";
const SNAPSHOT_TEMP: &str = "replica.snapshot.tmp";
const OPERATIONS: &str = "replica.operations";

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
    persisted_operations: BTreeSet<String>,
    known_documents: BTreeSet<String>,
    progress: DurableMemberProgress,
    reads: u64,
}

impl DurableMember {
    /// Opens a replica over an already-admitted database lease.
    pub fn open(
        lease: DatabaseLease,
        member_identity: &str,
        tenant: TenantId,
    ) -> Result<Self, DurableMemberError> {
        let location = lease.location().to_path_buf();
        let actor = actor_for(member_identity);
        let snapshot = match fs::read(location.join(SNAPSHOT)) {
            Ok(bytes) => ReplicaDoc::from_snapshot(actor, &bytes)?,
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => ReplicaDoc::new(actor),
            Err(_) => return Err(DurableMemberError::Persistence),
        };
        let operations = load_operations(&location)?;
        let persisted_operations = operations.clone();
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
            persisted_operations,
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
        Ok(document.and_then(|document| match document.value() {
            DocumentValue::Object(body) => Some(body.clone()),
            other => Some(BTreeMap::from([("value".to_owned(), other.clone())])),
        }))
    }

    /// Applies one client-originated put to the engine and replica.
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
    pub fn reconcile_with(&mut self, other: &mut Self) -> Result<(), DurableMemberError> {
        self.reconcile_with_timeout(other, std::time::Duration::MAX)
    }

    /// Exchanges and reconciles with a peer before `timeout` elapses.
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
        let bytes = self.replica.snapshot_bytes()?;
        let temporary = self.location.join(SNAPSHOT_TEMP);
        fs::write(&temporary, bytes).map_err(|_| DurableMemberError::Persistence)?;
        fs::rename(temporary, self.location.join(SNAPSHOT))
            .map_err(|_| DurableMemberError::Persistence)?;
        let mut file = OpenOptions::new()
            .create(true)
            .append(true)
            .open(self.location.join(OPERATIONS))
            .map_err(|_| DurableMemberError::Persistence)?;
        // The operation ledger is a set in memory; appending only newly seen
        // operations keeps replay checks durable without changing snapshot
        // codec bytes.
        for operation in self.operations.difference(&self.persisted_operations) {
            let _ = writeln!(file, "{operation}");
        }
        file.sync_data().map_err(|_| DurableMemberError::Persistence)?;
        self.persisted_operations = self.operations.clone();
        Ok(())
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

fn load_operations(location: &Path) -> Result<BTreeSet<String>, DurableMemberError> {
    let bytes = match fs::read(location.join(OPERATIONS)) {
        Ok(bytes) => bytes,
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => return Ok(BTreeSet::new()),
        Err(_) => return Err(DurableMemberError::Persistence),
    };
    let text = std::str::from_utf8(&bytes).map_err(|_| DurableMemberError::Persistence)?;
    Ok(text.lines().filter(|line| !line.is_empty()).map(str::to_owned).collect())
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
        let database = DatabaseSpec::new("database-a", root.join("database-a"));
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
        second.drain().expect("drain second");
        first.drain().expect("drain first");
        second_catalog.close(std::time::Duration::from_secs(1)).expect("close second");
        first_catalog.close(std::time::Duration::from_secs(1)).expect("close first");

        let (_, mut revived) = member(&first_path, "member-a");
        assert_eq!(revived.read("document-a").expect("recovered read"), Some(body("one")));
        revived.drain().expect("drain revived");
        let _ = fs::remove_dir_all(first_path);
        let _ = fs::remove_dir_all(second_path);
    }
}
