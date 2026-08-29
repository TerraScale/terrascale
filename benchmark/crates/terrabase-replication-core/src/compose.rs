//! The composed Member: the engine and the CRDT plane in one server
//! (P2 composition).
//!
//! A [`ComposedMember`] is what "Loro leaderless multi-writer between
//! servers" means concretely: one Member whose accepted writes land in
//! its durable engine AND its own-peer [`ReplicaDoc`], and whose
//! anti-entropy with any other Member heals both planes — the CRDTs
//! merge causally, then each Member's engine is reconciled to the
//! merged CRDT state, so every server serves the same converged state
//! without an election, a leader, or a redirect.
//!
//! The reconcile path is idempotent per merged state: its engine
//! operations carry identities derived from the document and the
//! replica's frontier fingerprint, so re-running a heal at an unchanged
//! frontier replays the recorded receipts and commits nothing new.
//!
//! Honest limits of this landing: the CRDT state is in-memory (its
//! durable staging through the object-store tier lands with the
//! transport wiring), and reconcile enumerates the documents the Member
//! knows — durable re-enumeration after a restart needs the public
//! tenant-read surface that lands with P4.

use std::collections::{BTreeMap, BTreeSet};
use std::path::{Path, PathBuf};

use loro::LoroValue;
use terrabase_v2::CommitReceipt;
use terrabase_v2::engine::{CommitOutcome, Engine, EngineConfig};
use terrabase_v2::replication::{LeaderlessLevel, MemberId, RegionId, ReplicaSet};
use terrabase_v2::types::{
    CollectionName, Document, DocumentId, DocumentValue, ExpectedVersion, LocalDurability,
    OperationId, TenantId,
};

use crate::{DOCS_ROOT, ReplicaDoc, ReplicaError, ReplicaExchange, ReplicaFrontier};

/// The single collection the composed plane materializes into.
const COLLECTION: &str = "documents";

/// Why a composed-member operation failed. Typed and bounded: no
/// engine internals cross the seam.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ComposeError {
    /// The engine refused the local write or read.
    EngineRefused,
    /// The CRDT plane refused.
    Replica(ReplicaError),
    /// A merged value has no engine representation.
    UnsupportedValue,
    /// An identifier was invalid.
    InvalidIdentifier,
}

/// One leaderless write in the composed vocabulary: the operation
/// identity, the document, and its whole-body change.
#[derive(Clone, Debug, PartialEq)]
pub enum ComposedWrite {
    /// Puts one document body.
    Put {
        /// The stable operation identity.
        operation: String,
        /// The document identity.
        document: String,
        /// The whole new body.
        body: BTreeMap<String, DocumentValue>,
    },
    /// Deletes one document.
    Delete {
        /// The stable operation identity.
        operation: String,
        /// The document identity.
        document: String,
    },
}

impl ComposedWrite {
    /// Returns the stable operation identity text.
    #[must_use]
    pub fn operation(&self) -> &str {
        match self {
            Self::Put { operation, .. } | Self::Delete { operation, .. } => operation,
        }
    }
}

/// One composed Member's durable acknowledgement of one coordinated
/// write.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ComposedAck {
    member: MemberId,
    region: RegionId,
    epoch: u64,
    sequence: u64,
}

impl ComposedAck {
    /// Returns the acknowledging Member.
    #[must_use]
    pub fn member(&self) -> &MemberId {
        &self.member
    }

    /// Returns the acknowledging Member's Region.
    #[must_use]
    pub fn region(&self) -> &RegionId {
        &self.region
    }

    /// Returns the replica-set epoch the acknowledgement was collected
    /// at.
    #[must_use]
    pub const fn epoch(&self) -> u64 {
        self.epoch
    }

    /// Returns the storage sequence the Member's engine published.
    #[must_use]
    pub const fn sequence(&self) -> u64 {
        self.sequence
    }
}

/// The receipt of one coordinated composed write: the achieved level,
/// every contributing Member and Region, the accepting Member, and the
/// epoch the quorums were evaluated at.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ComposedReceipt {
    operation: String,
    requested: LeaderlessLevel,
    achieved: LeaderlessLevel,
    accepting_member: MemberId,
    accepting_region: RegionId,
    epoch: u64,
    contributors: Vec<ComposedAck>,
    local: CommitReceipt,
}

impl ComposedReceipt {
    /// Returns the stable operation identity text.
    #[must_use]
    pub fn operation(&self) -> &str {
        &self.operation
    }

    /// Returns the requested acknowledgement level.
    #[must_use]
    pub const fn requested(&self) -> LeaderlessLevel {
        self.requested
    }

    /// Returns the achieved acknowledgement level.
    #[must_use]
    pub const fn achieved(&self) -> LeaderlessLevel {
        self.achieved
    }

    /// Returns the request-scoped coordinating Member.
    #[must_use]
    pub fn accepting_member(&self) -> &MemberId {
        &self.accepting_member
    }

    /// Returns the accepting Region.
    #[must_use]
    pub fn accepting_region(&self) -> &RegionId {
        &self.accepting_region
    }

    /// Returns the replica-set epoch the quorums were evaluated at.
    #[must_use]
    pub const fn epoch(&self) -> u64 {
        self.epoch
    }

    /// Returns every contributing Member's acknowledgement, in
    /// canonical Member order.
    #[must_use]
    pub fn contributors(&self) -> &[ComposedAck] {
        &self.contributors
    }

    /// Returns the accepting Member's own local commit receipt.
    #[must_use]
    pub fn local(&self) -> &CommitReceipt {
        &self.local
    }
}

/// Why a coordinated composed write did not succeed. The same lattice
/// as the engine-side coordinator's errors, in the composed vocabulary.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum ComposedCoordinationError {
    /// The accepting Member is not an eligible voter of the set's
    /// epoch.
    NotEligible {
        /// The rejecting epoch.
        epoch: u64,
    },
    /// One Member's apply failed.
    ApplyFailed {
        /// The Member whose apply failed.
        member: MemberId,
    },
    /// The requested level was not met; the durable partial
    /// contributors are named for the retry.
    QuorumNotReached {
        /// The durable partial contributors at the deadline.
        contributors: Vec<ComposedAck>,
    },
}

/// What one reconcile pass changed in the member's engine.
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct ReconcileReport {
    puts: u64,
    deletes: u64,
}

impl ReconcileReport {
    /// Returns the number of documents reconciled forward.
    #[must_use]
    pub const fn puts(&self) -> u64 {
        self.puts
    }

    /// Returns the number of documents reconciled to deleted.
    #[must_use]
    pub const fn deletes(&self) -> u64 {
        self.deletes
    }
}

/// FNV-1a over bytes — a stable, dependency-free fingerprint for
/// deterministic reconcile identities.
fn fnv1a(bytes: &[u8]) -> u64 {
    let mut hash: u64 = 0xcbf2_9ce4_8422_2325;
    for byte in bytes {
        hash ^= u64::from(*byte);
        hash = hash.wrapping_mul(0x0000_0100_0000_01B3);
    }
    hash
}

/// Converts one engine value into its CRDT form.
fn to_loro(value: &DocumentValue) -> LoroValue {
    match value {
        DocumentValue::Null => LoroValue::Null,
        DocumentValue::Boolean(flag) => LoroValue::from(*flag),
        DocumentValue::Integer(number) => LoroValue::from(*number),
        DocumentValue::Number(number) => LoroValue::from(*number),
        DocumentValue::String(text) => LoroValue::from(text.as_str()),
        DocumentValue::Array(items) => {
            LoroValue::List(items.iter().map(to_loro).collect::<Vec<_>>().into())
        }
        DocumentValue::Object(fields) => LoroValue::Map(
            fields
                .iter()
                .map(|(key, field)| (key.clone(), to_loro(field)))
                .collect::<Vec<_>>()
                .into(),
        ),
    }
}

/// Converts one merged CRDT value into its engine form.
fn from_loro(value: LoroValue) -> Result<DocumentValue, ComposeError> {
    match value {
        LoroValue::Null => Ok(DocumentValue::Null),
        LoroValue::Bool(flag) => Ok(DocumentValue::Boolean(flag)),
        LoroValue::Double(number) => Ok(DocumentValue::Number(number)),
        LoroValue::I64(number) => Ok(DocumentValue::Integer(number)),
        LoroValue::String(text) => Ok(DocumentValue::String(text.to_string())),
        LoroValue::List(items) => {
            let mut converted = Vec::new();
            for item in items.unwrap() {
                converted.push(from_loro(item)?);
            }
            Ok(DocumentValue::Array(converted))
        }
        LoroValue::Map(fields) => {
            let mut converted = BTreeMap::new();
            for (field, value) in fields.unwrap() {
                converted.insert(field, from_loro(value)?);
            }
            Ok(DocumentValue::Object(converted))
        }
        LoroValue::Binary(_) | LoroValue::Container(_) => Err(ComposeError::UnsupportedValue),
    }
}

/// Extracts the merged documents of a whole CRDT scope.
fn crdt_documents(
    state: &LoroValue,
) -> Result<BTreeMap<String, BTreeMap<String, DocumentValue>>, ComposeError> {
    let mut documents = BTreeMap::new();
    let Some(docs) = state.get_by_key(DOCS_ROOT).cloned() else {
        return Ok(documents);
    };
    let LoroValue::Map(entries) = docs else {
        return Ok(documents);
    };
    for (document, body) in entries.unwrap() {
        let LoroValue::Map(fields) = body else {
            continue;
        };
        let mut converted = BTreeMap::new();
        for (field, value) in fields.unwrap() {
            converted.insert(field, from_loro(value)?);
        }
        documents.insert(document, converted);
    }
    Ok(documents)
}

/// One server Member: a durable engine plus a same-peer CRDT replica.
pub struct ComposedMember {
    member: MemberId,
    peer_id: u64,
    location: PathBuf,
    engine: Engine,
    replica: ReplicaDoc,
    tenant: TenantId,
    reads: u64,
    known: BTreeSet<String>,
}

impl ComposedMember {
    /// Opens one Member's state at a durable location, recovering the
    /// persisted CRDT snapshot when one is staged there. The Member's
    /// CRDT peer identity derives deterministically from its name —
    /// one identity source, assigned names by the configuration plane.
    ///
    /// # Errors
    ///
    /// Returns [`ComposeError::EngineRefused`] when the location or
    /// engine cannot open, [`ComposeError::Replica`] when the peer
    /// identity or the persisted snapshot is rejected, and
    /// [`ComposeError::InvalidIdentifier`] for invalid member or
    /// tenant text.
    pub fn open(
        member_name: &str,
        location: &Path,
        tenant_name: &str,
    ) -> Result<Self, ComposeError> {
        let member = MemberId::try_new(member_name).map_err(|_| ComposeError::InvalidIdentifier)?;
        let peer_id = fnv1a(member.as_str().as_bytes());
        let engine =
            Engine::open(EngineConfig::durable(location).map_err(|_| ComposeError::EngineRefused)?)
                .map_err(|_| ComposeError::EngineRefused)?;
        let snapshot = location.join("replica.snapshot");
        let replica = match std::fs::read(&snapshot) {
            Ok(bytes) => {
                ReplicaDoc::from_snapshot(peer_id, &bytes).map_err(ComposeError::Replica)?
            }
            Err(_) => ReplicaDoc::new(peer_id).map_err(ComposeError::Replica)?,
        };
        let tenant = TenantId::try_new(tenant_name).map_err(|_| ComposeError::InvalidIdentifier)?;
        Ok(Self {
            member,
            peer_id,
            location: location.to_path_buf(),
            engine,
            replica,
            tenant,
            reads: 0,
            known: BTreeSet::new(),
        })
    }

    /// Returns the Member's identity.
    #[must_use]
    pub fn member_id(&self) -> &MemberId {
        &self.member
    }

    /// Returns the Member's CRDT peer identity.
    #[must_use]
    pub const fn peer_id(&self) -> u64 {
        self.peer_id
    }

    /// Returns the Member's causal frontier.
    #[must_use]
    pub fn frontier(&self) -> ReplicaFrontier {
        self.replica.frontier()
    }

    /// Returns the merged CRDT state of the Member's scope — the
    /// convergence observation surface.
    #[must_use]
    pub fn merged_state(&self) -> LoroValue {
        self.replica.deep_value()
    }

    /// Accepts one whole-body put durably: the engine commits it and
    /// the Member's own-peer CRDT op records it.
    ///
    /// # Errors
    ///
    /// Returns the typed compose failures of both planes.
    pub fn accept_put(
        &mut self,
        operation: &str,
        document: &str,
        body: &BTreeMap<String, DocumentValue>,
    ) -> Result<CommitReceipt, ComposeError> {
        let receipt = self.engine_put(operation, document, body)?;
        let mut merged = BTreeMap::new();
        for (field, value) in body {
            merged.insert(field.clone(), to_loro(value));
        }
        self.replica.apply_put(document, &merged).map_err(ComposeError::Replica)?;
        self.known.insert(document.to_owned());
        self.stage_snapshot()?;
        Ok(receipt)
    }

    /// Accepts one deletion durably on both planes.
    ///
    /// # Errors
    ///
    /// Returns the typed compose failures of both planes.
    pub fn accept_delete(
        &mut self,
        operation: &str,
        document: &str,
    ) -> Result<CommitReceipt, ComposeError> {
        let receipt = self.engine_delete(operation, document)?;
        self.replica.apply_delete(document).map_err(ComposeError::Replica)?;
        self.stage_snapshot()?;
        Ok(receipt)
    }

    /// Returns the document body this Member's engine serves, or
    /// `None` when the engine holds no visible version.
    ///
    /// # Errors
    ///
    /// Returns [`ComposeError::EngineRefused`] when the engine refuses
    /// the read and [`ComposeError::InvalidIdentifier`] for invalid
    /// document text.
    pub fn visible(
        &mut self,
        document: &str,
    ) -> Result<Option<BTreeMap<String, DocumentValue>>, ComposeError> {
        self.reads += 1;
        let operation = OperationId::try_new(&format!("rd-{}-{}", self.peer_id, self.reads))
            .map_err(|_| ComposeError::InvalidIdentifier)?;
        let document_id =
            DocumentId::try_new(document).map_err(|_| ComposeError::InvalidIdentifier)?;
        let scope = self.engine.scope_for(&self.tenant);
        let snapshot =
            self.engine.read_snapshot(&scope).map_err(|_| ComposeError::EngineRefused)?;
        let transaction = self
            .engine
            .begin(&scope, snapshot, operation)
            .map_err(|_| ComposeError::EngineRefused)?;
        let found = transaction.read(&document_id).map_err(|_| ComposeError::EngineRefused)?;
        drop(transaction);
        match found {
            None => Ok(None),
            Some(document) => match document.value().clone() {
                DocumentValue::Object(fields) => Ok(Some(fields)),
                // A non-object body still round-trips: one field.
                other => Ok(Some(BTreeMap::from([("value".to_owned(), other)]))),
            },
        }
    }

    /// Runs anti-entropy with one other Member and reconciles both
    /// engines to the merged state.
    ///
    /// Ownership-agnostic by construction: either Member may initiate
    /// with the other. Returns both Members' reconcile reports.
    ///
    /// # Errors
    ///
    /// Returns the typed compose failures of both planes.
    pub fn anti_entropy_with(
        &mut self,
        other: &mut Self,
    ) -> Result<(ReconcileReport, ReconcileReport), ComposeError> {
        loop {
            let mine = self.replica.frontier();
            let theirs = other.replica.frontier();
            if mine.covers(&theirs) && theirs.covers(&mine) {
                break;
            }
            if let ReplicaExchange::Delta(bytes) =
                self.replica.delta_for(&theirs).map_err(ComposeError::Replica)?
            {
                other.replica.absorb(&bytes).map_err(ComposeError::Replica)?;
            }
            if let ReplicaExchange::Delta(bytes) =
                other.replica.delta_for(&mine).map_err(ComposeError::Replica)?
            {
                self.replica.absorb(&bytes).map_err(ComposeError::Replica)?;
            }
        }
        let first = self.reconcile()?;
        let second = other.reconcile()?;
        self.stage_snapshot()?;
        other.stage_snapshot()?;
        Ok((first, second))
    }

    /// Reconciles this Member's engine to its merged CRDT state.
    ///
    /// Idempotent per merged state: the operation identities derive
    /// from the document and the frontier fingerprint, so an unchanged
    /// frontier replays the recorded receipts and commits nothing.
    ///
    /// # Errors
    ///
    /// Returns the typed compose failures of both planes.
    pub fn reconcile(&mut self) -> Result<ReconcileReport, ComposeError> {
        let merged = crdt_documents(&self.replica.deep_value())?;
        let fingerprint = self.frontier_fingerprint();
        let mut report = ReconcileReport::default();
        for (document, body) in &merged {
            if self.visible(document)?.as_ref() != Some(body) {
                let identity = format!("ae-{:x}-{fingerprint:x}", fnv1a(document.as_bytes()));
                self.engine_put(&identity, document, body)?;
                self.known.insert(document.clone());
                report.puts += 1;
            }
        }
        for document in self.known.clone() {
            if !merged.contains_key(&document) && self.visible(&document)?.is_some() {
                let identity = format!("aed-{:x}-{fingerprint:x}", fnv1a(document.as_bytes()));
                self.engine_delete(&identity, &document)?;
                report.deletes += 1;
            }
        }
        Ok(report)
    }

    /// Closes the Member, flushing its durable engine.
    ///
    /// # Errors
    ///
    /// Returns [`ComposeError::EngineRefused`] when the final flush
    /// fails.
    pub fn shutdown(self) -> Result<(), ComposeError> {
        self.engine.shutdown().map_err(|_| ComposeError::EngineRefused)
    }

    fn frontier_fingerprint(&self) -> u64 {
        use std::fmt::Write as _;
        let mut text = String::new();
        for (peer, end) in self.replica.frontier().ends() {
            let _ = write!(text, "{peer:x}-{end:x};");
        }
        fnv1a(text.as_bytes())
    }

    /// Stages the CRDT snapshot durably beside the engine: temp file,
    /// rename, so a reopen never sees a torn snapshot. Test-grade
    /// persistence — a full snapshot per state change; incremental
    /// journal staging lands with the transport wiring.
    fn stage_snapshot(&self) -> Result<(), ComposeError> {
        let bytes = self.replica.snapshot_bytes().map_err(ComposeError::Replica)?;
        let final_path = self.location.join("replica.snapshot");
        let temp_path = self.location.join("replica.snapshot.tmp");
        std::fs::write(&temp_path, &bytes).map_err(|_| ComposeError::EngineRefused)?;
        std::fs::rename(&temp_path, &final_path).map_err(|_| ComposeError::EngineRefused)?;
        Ok(())
    }

    fn engine_put(
        &self,
        operation: &str,
        document: &str,
        body: &BTreeMap<String, DocumentValue>,
    ) -> Result<CommitReceipt, ComposeError> {
        let operation_id =
            OperationId::try_new(operation).map_err(|_| ComposeError::InvalidIdentifier)?;
        let document_id =
            DocumentId::try_new(document).map_err(|_| ComposeError::InvalidIdentifier)?;
        let collection =
            CollectionName::try_new(COLLECTION).map_err(|_| ComposeError::InvalidIdentifier)?;
        let scope = self.engine.scope_for(&self.tenant);
        let snapshot =
            self.engine.read_snapshot(&scope).map_err(|_| ComposeError::EngineRefused)?;
        let mut transaction = self
            .engine
            .begin(&scope, snapshot, operation_id)
            .map_err(|_| ComposeError::EngineRefused)?;
        transaction
            .put(
                Document::new(document_id, collection, DocumentValue::Object(body.clone())),
                ExpectedVersion::Any,
            )
            .map_err(|_| ComposeError::EngineRefused)?;
        match transaction.commit(LocalDurability::Synced) {
            CommitOutcome::Committed(receipt) => Ok(receipt),
            CommitOutcome::Rejected(_) | CommitOutcome::Unknown(_) => {
                Err(ComposeError::EngineRefused)
            }
        }
    }

    fn engine_delete(
        &self,
        operation: &str,
        document: &str,
    ) -> Result<CommitReceipt, ComposeError> {
        let operation_id =
            OperationId::try_new(operation).map_err(|_| ComposeError::InvalidIdentifier)?;
        let document_id =
            DocumentId::try_new(document).map_err(|_| ComposeError::InvalidIdentifier)?;
        let scope = self.engine.scope_for(&self.tenant);
        let snapshot =
            self.engine.read_snapshot(&scope).map_err(|_| ComposeError::EngineRefused)?;
        let mut transaction = self
            .engine
            .begin(&scope, snapshot, operation_id)
            .map_err(|_| ComposeError::EngineRefused)?;
        transaction
            .delete(document_id, ExpectedVersion::Any)
            .map_err(|_| ComposeError::EngineRefused)?;
        match transaction.commit(LocalDurability::Synced) {
            CommitOutcome::Committed(receipt) => Ok(receipt),
            CommitOutcome::Rejected(_) | CommitOutcome::Unknown(_) => {
                Err(ComposeError::EngineRefused)
            }
        }
    }
}

/// Coordinates one leaderless write across the reachable composed
/// Members of a replica set (contract §3, the product write path).
///
/// The accepting Member is the request-scoped coordinator only: every
/// eligible reachable Member accepts the same operation on BOTH of its
/// planes — durable engine commit plus own-peer CRDT op — and success
/// is decided purely by the collected durable acknowledgements against
/// the epoch's replica set. No ownership, no lease, no redirect.
///
/// # Errors
///
/// Returns [`ComposedCoordinationError::NotEligible`] when the
/// accepting Member is not an eligible voter of the set's epoch,
/// [`ComposedCoordinationError::ApplyFailed`] when one reachable
/// Member's apply fails, and
/// [`ComposedCoordinationError::QuorumNotReached`] when the requested
/// level is not met — naming the durable partial contributors for the
/// retry through any Member.
pub fn coordinate_composed_write(
    set: &ReplicaSet,
    accepting: &MemberId,
    members: &mut [ComposedMember],
    write: &ComposedWrite,
    level: LeaderlessLevel,
) -> Result<ComposedReceipt, ComposedCoordinationError> {
    let Some(accepting_region) = set.region_of(accepting) else {
        return Err(ComposedCoordinationError::NotEligible { epoch: set.epoch() });
    };
    let mut contributors: Vec<ComposedAck> = Vec::new();
    let mut local: Option<CommitReceipt> = None;
    for member in members {
        // A seat in the set is eligibility: unseated Members are not
        // fanned out to.
        let Some(region) = set.region_of(member.member_id()) else {
            continue;
        };
        let receipt = accept_on(member, write).map_err(|_| {
            ComposedCoordinationError::ApplyFailed { member: member.member_id().clone() }
        })?;
        if member.member_id() == accepting {
            local = Some(receipt.clone());
        }
        contributors.push(ComposedAck {
            member: member.member_id().clone(),
            region,
            epoch: set.epoch(),
            sequence: receipt.sequence().get(),
        });
    }
    let Some(local) = local else {
        return Err(ComposedCoordinationError::QuorumNotReached { contributors });
    };
    let regional_count =
        contributors.iter().filter(|ack| *ack.region() == accepting_region).count();
    if regional_count < set.regional_threshold(&accepting_region) {
        return Err(ComposedCoordinationError::QuorumNotReached { contributors });
    }
    if level == LeaderlessLevel::GlobalQuorum {
        let qualified_regions = set
            .regions()
            .into_iter()
            .filter(|region| {
                let count = contributors.iter().filter(|ack| ack.region() == region).count();
                count >= set.regional_threshold(region)
            })
            .count();
        if qualified_regions < set.global_threshold() {
            return Err(ComposedCoordinationError::QuorumNotReached { contributors });
        }
    }
    contributors.sort_by(|left, right| left.member().as_str().cmp(right.member().as_str()));
    Ok(ComposedReceipt {
        operation: write.operation().to_owned(),
        requested: level,
        achieved: level,
        accepting_member: accepting.clone(),
        accepting_region,
        epoch: set.epoch(),
        contributors,
        local,
    })
}

/// Applies one write on one Member's both planes.
fn accept_on(
    member: &mut ComposedMember,
    write: &ComposedWrite,
) -> Result<CommitReceipt, ComposeError> {
    match write {
        ComposedWrite::Put { operation, document, body } => {
            member.accept_put(operation, document, body)
        }
        ComposedWrite::Delete { operation, document } => member.accept_delete(operation, document),
    }
}
