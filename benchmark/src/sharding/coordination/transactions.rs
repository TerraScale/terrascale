//! Cluster-scoped transactions over a seated shard topology (SH-05,
//! PB-5041 under `docs/sharding/dispositions.md`).
//!
//! V1 coordinates cluster transactions through Rachis command commitment;
//! under ADR 0011 D4 that machinery stays out, and the observable contract
//! is delivered through the accepted seams: a cluster transaction routes
//! its staged writes shard key -> bucket -> shard -> owner, and executes
//! one shard-local engine transaction per shard.
//!
//! The atomicity truth is structural, not asserted:
//!
//! - **One shard**: all stages land in one engine transaction, so the
//!   commit is atomic with the observable `CommitReceipt` of that
//!   transaction ([`ClusterOutcome::Atomic`]).
//! - **Several shards**: there is no cross-shard atomic commit in the
//!   accepted model and none is faked. Every shard commits its own
//!   shard-local transaction; the outcome is [`ClusterOutcome::PerShard`]
//!   carrying each shard's receipt and the recorded limitation. A refused
//!   shard makes the outcome [`ClusterOutcome::Partial`]: the shards that
//!   already committed stay committed (there is no cross-shard rollback),
//!   the refusal names the shard and the reason, and a re-drive replays
//!   committed shards idempotently by operation identity while retrying
//!   only what refused.
//!
//! Execution re-authorizes every placement against the topology it is
//! handed, which may be newer than the one the transaction routed under:
//! a failover that lands between routing and execution refuses the stale
//! shard with the sharding seam's own fencing facts instead of writing
//! through a deposed owner.
//!
//! Acknowledgement truth: a shard's receipt is a
//! [`LocalDurability::Synced`](crate::types::LocalDurability::Synced) fact
//! about that shard's engine only. Cross-member acknowledgement is the
//! replication plane's own qualified machinery and is never inferred here.

use crate::engine::{
    CommitError, CommitOutcome, CommitReceipt, DurableResolution, Engine, EngineError,
};
use crate::replication::MemberId;
use crate::sharding::ShardingError;
use crate::types::{
    CollectionName, Document, DocumentId, ExpectedVersion, LocalDurability, OperationId, TenantId,
    TenantScope,
};

use super::super::{RoutePlacement, RoutingRequest, ShardId, ShardTopology};

/// The recorded atomicity scope of a single-shard cluster transaction.
pub const ATOMIC_WITHIN_ONE_SHARD: &str =
    "single-shard cluster operations are atomic within one shard-local engine transaction";

/// The recorded limit of every cross-shard cluster transaction.
pub const CROSS_SHARD_NOT_ATOMIC: &str = "cross-shard cluster operations are not atomic across shards: each shard commits its own shard-local transaction and the outcome records every shard's observable result";

/// The recorded rollback limit of a partially refused cluster transaction.
pub const NO_CROSS_SHARD_ROLLBACK: &str = "a refused shard does not roll back shards that already committed; the outcome names the committed and refused shards and a re-drive replays committed shards idempotently";

/// The recorded acknowledgement scope of any cluster commit.
pub const SHARD_LOCAL_ACKNOWLEDGEMENT: &str = "a shard's commit receipt is a shard-local durability fact; database-wide acknowledgement is the replication plane's own observation and is never inferred here";

/// One staged write of a cluster transaction.
#[derive(Clone, Debug, PartialEq)]
pub enum ClusterStage {
    /// Puts one document.
    Put(Document),
    /// Deletes one document of one collection.
    Delete {
        /// The document identity to delete.
        id: DocumentId,
        /// The collection the identity lives in.
        collection: CollectionName,
    },
}

impl ClusterStage {
    fn document_id(&self) -> &DocumentId {
        match self {
            Self::Put(document) => document.id(),
            Self::Delete { id, .. } => id,
        }
    }

    fn collection(&self) -> &CollectionName {
        match self {
            Self::Put(document) => document.collection(),
            Self::Delete { collection, .. } => collection,
        }
    }
}

/// One cluster-scoped operation: a tenant, one stable operation identity,
/// and the staged writes it coordinates.
#[derive(Clone, Debug, PartialEq)]
pub struct ClusterTransaction {
    tenant: TenantId,
    operation: OperationId,
    stages: Vec<ClusterStage>,
}

impl ClusterTransaction {
    /// Builds one cluster transaction for `tenant` under one stable
    /// operation identity.
    ///
    /// The identity is what makes re-drives idempotent: every shard that
    /// already committed the operation replays its receipt instead of
    /// committing twice.
    ///
    /// # Errors
    ///
    /// Returns [`ClusterTransactionError::Empty`] when no stage was given:
    /// an operation that writes nothing has no observable result to
    /// preserve.
    pub fn try_new(
        tenant: TenantId,
        operation: OperationId,
        stages: Vec<ClusterStage>,
    ) -> Result<Self, ClusterTransactionError> {
        if stages.is_empty() {
            return Err(ClusterTransactionError::Empty);
        }
        Ok(Self { tenant, operation, stages })
    }

    /// Returns the tenant this transaction writes for.
    #[must_use]
    pub fn tenant(&self) -> &TenantId {
        &self.tenant
    }

    /// Returns the stable operation identity of this transaction.
    #[must_use]
    pub fn operation(&self) -> &OperationId {
        &self.operation
    }

    /// Returns the staged writes, in staging order.
    #[must_use]
    pub fn stages(&self) -> &[ClusterStage] {
        &self.stages
    }

    /// Routes every stage through the topology: the shard each write
    /// belongs to, the Member that owns it, and the placement the write
    /// path must validate against.
    ///
    /// # Errors
    ///
    /// Returns [`ClusterTransactionError::Routing`] with the sharding
    /// seam's own refusal (unknown shard-key field, unsupported value,
    /// shard without owner): a cluster operation never routes around a
    /// refusal.
    pub fn route(
        &self,
        topology: &ShardTopology,
    ) -> Result<RoutedTransaction, ClusterTransactionError> {
        let mut placements = Vec::new();
        let mut values: Vec<Option<&Document>> = Vec::new();
        for stage in &self.stages {
            let mut request =
                RoutingRequest::by_identity(&self.tenant, stage.collection(), stage.document_id());
            if let ClusterStage::Put(document) = stage {
                request = request.with_value(document.value());
                values.push(Some(document));
            } else {
                values.push(None);
            }
            placements.push(topology.route(&request).map_err(ClusterTransactionError::Routing)?);
        }

        let mut groups: Vec<ShardWriteSet> = Vec::new();
        for shard in topology.table().shards() {
            let mut shard_placements = Vec::new();
            let mut shard_stages = Vec::new();
            for (placement, stage) in placements.iter().zip(&self.stages) {
                if placement.shard() == shard {
                    shard_placements.push(placement.clone());
                    shard_stages.push(stage.clone());
                }
            }
            if shard_placements.is_empty() {
                continue;
            }
            let owner = shard_placements[0].owner().clone();
            let epoch = shard_placements[0].epoch();
            groups.push(ShardWriteSet {
                shard,
                owner,
                epoch,
                placements: shard_placements,
                stages: shard_stages,
            });
        }

        Ok(RoutedTransaction {
            tenant: self.tenant.clone(),
            operation: self.operation.clone(),
            groups,
        })
    }
}

/// The typed refusals of cluster-transaction construction and routing.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum ClusterTransactionError {
    /// The transaction staged no writes.
    Empty,
    /// Routing refused a stage; the sharding seam's own refusal stands.
    Routing(ShardingError),
}

/// One routed cluster transaction: the tenant, the stable operation
/// identity every shard executes under, and the per-shard write sets in
/// the seated table's canonical shard order.
#[derive(Clone, Debug, PartialEq)]
pub struct RoutedTransaction {
    tenant: TenantId,
    operation: OperationId,
    groups: Vec<ShardWriteSet>,
}

impl RoutedTransaction {
    /// Returns the per-shard write sets, in canonical shard order.
    #[must_use]
    pub fn groups(&self) -> &[ShardWriteSet] {
        &self.groups
    }

    /// Returns the number of shards this transaction spans.
    #[must_use]
    pub fn shard_count(&self) -> usize {
        self.groups.len()
    }

    /// Reports whether every stage routes to one shard.
    #[must_use]
    pub fn spans_single_shard(&self) -> bool {
        self.groups.len() == 1
    }

    /// Returns the tenant this transaction was routed for.
    #[must_use]
    pub fn tenant(&self) -> &TenantId {
        &self.tenant
    }

    /// Returns the stable operation identity of this transaction.
    #[must_use]
    pub fn operation(&self) -> &OperationId {
        &self.operation
    }

    /// Executes the transaction shard by shard against `serving` through
    /// the seated tenant scopes, re-authorizing every placement against
    /// `topology`.
    ///
    /// `topology` may be a newer seating than the one this transaction
    /// routed under: a failover between the two refuses the stale shard
    /// with the sharding seam's fencing facts. Shards execute in canonical
    /// order; the first refusal stops the execution with the shards that
    /// already committed recorded truthfully. A group's owner with no
    /// seated scope refuses that shard: without backend-supplied tenant
    /// context the shard cannot execute.
    ///
    /// # Panics
    ///
    /// Panics only if the single-group commit invariant fails (a routed
    /// single-shard transaction whose group did not commit and did not
    /// refuse); every refusal path returns before it, making it
    /// unreachable.
    #[must_use]
    pub fn execute(
        &self,
        topology: &ShardTopology,
        serving: &dyn ShardWriteServing,
        scopes: &[(MemberId, TenantScope)],
    ) -> ClusterOutcome {
        let mut committed: Vec<ShardCommit> = Vec::new();
        for group in &self.groups {
            for placement in group.placements() {
                if let Err(error) = topology.authorize_placement(placement, &self.tenant) {
                    return refusal_outcome(
                        committed,
                        ShardRefusal::new(group.shard(), ShardWriteError::PlacementFenced(error)),
                    );
                }
            }
            let Some(scope) = scopes.iter().find(|(member, _)| member == group.owner()) else {
                return refusal_outcome(
                    committed,
                    ShardRefusal::new(
                        group.shard(),
                        ShardWriteError::ScopeMissing { owner: group.owner().clone() },
                    ),
                );
            };
            match serving.resolve(group.owner(), &scope.1, &self.operation) {
                Ok(Some(receipt)) => {
                    committed.push(ShardCommit { shard: group.shard(), receipt, replayed: true });
                    continue;
                }
                Ok(None) => {}
                Err(error) => {
                    return refusal_outcome(committed, ShardRefusal::new(group.shard(), error));
                }
            }
            match serving.commit(group.owner(), &scope.1, &self.operation, group.stages()) {
                Ok(receipt) => {
                    committed.push(ShardCommit { shard: group.shard(), receipt, replayed: false });
                }
                Err(error) => {
                    return refusal_outcome(committed, ShardRefusal::new(group.shard(), error));
                }
            }
        }

        if self.groups.len() == 1 {
            let single = committed.pop().expect("the single group committed");
            return ClusterOutcome::Atomic(Box::new(single));
        }
        ClusterOutcome::PerShard { committed }
    }
}

/// One shard's routed write set: the shard, its owner at the routed epoch,
/// and the placements the write path must validate against.
#[derive(Clone, Debug, PartialEq)]
pub struct ShardWriteSet {
    shard: ShardId,
    owner: MemberId,
    epoch: u64,
    placements: Vec<RoutePlacement>,
    stages: Vec<ClusterStage>,
}

impl ShardWriteSet {
    /// Returns the shard these writes belong to.
    #[must_use]
    pub const fn shard(&self) -> ShardId {
        self.shard
    }

    /// Returns the Member that owns the shard's write path at route time.
    #[must_use]
    pub fn owner(&self) -> &MemberId {
        &self.owner
    }

    /// Returns the ownership epoch the routed placements name.
    #[must_use]
    pub const fn epoch(&self) -> u64 {
        self.epoch
    }

    /// Returns the routed placements of this shard's writes.
    #[must_use]
    pub fn placements(&self) -> &[RoutePlacement] {
        &self.placements
    }

    /// Returns the staged writes of this shard.
    #[must_use]
    pub fn stages(&self) -> &[ClusterStage] {
        &self.stages
    }
}

/// One shard's committed fact inside a cluster outcome.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ShardCommit {
    shard: ShardId,
    receipt: CommitReceipt,
    replayed: bool,
}

impl ShardCommit {
    /// Returns the shard that committed.
    #[must_use]
    pub const fn shard(&self) -> ShardId {
        self.shard
    }

    /// Returns the observable commit receipt of the shard-local
    /// transaction.
    #[must_use]
    pub fn receipt(&self) -> &CommitReceipt {
        &self.receipt
    }

    /// Reports whether this commit was replayed from the durable operation
    /// ledger by identity rather than freshly committed.
    #[must_use]
    pub const fn replayed(&self) -> bool {
        self.replayed
    }
}

/// One shard's refusal inside a cluster outcome: the shard and the typed
/// reason, never a document value or path.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ShardRefusal {
    shard: ShardId,
    error: ShardWriteError,
}

impl ShardRefusal {
    /// Assembles one refusal from its facts.
    #[must_use]
    pub const fn new(shard: ShardId, error: ShardWriteError) -> Self {
        Self { shard, error }
    }

    /// Returns the shard that refused.
    #[must_use]
    pub const fn shard(&self) -> ShardId {
        self.shard
    }

    /// Returns the typed refusal.
    #[must_use]
    pub const fn error(&self) -> &ShardWriteError {
        &self.error
    }
}

/// The typed reasons one shard refuses to execute its write set.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum ShardWriteError {
    /// The serving set has no endpoint for the shard's owning Member: the
    /// shard cannot execute and nothing is written for it.
    MemberUnserved {
        /// The Member with no endpoint.
        owner: MemberId,
    },
    /// The placement was fenced by the execution topology: the shard's
    /// owner or epoch changed (a failover or transfer landed between
    /// routing and execution).
    PlacementFenced(ShardingError),
    /// The execution set seats no tenant scope for the shard's owner:
    /// without backend-supplied tenant context the shard cannot execute.
    ScopeMissing {
        /// The owner with no seated scope.
        owner: MemberId,
    },
    /// The engine seam refused the shard-local transaction.
    Engine(EngineError),
    /// The shard-local transaction was rejected before publication; the
    /// typed reason preserves the observable failure result.
    Rejected {
        /// The engine's typed rejection reason.
        reason: CommitError,
    },
    /// The shard-local transaction committed with an unknown outcome that
    /// stayed pending after resolution.
    Pending,
}

/// The outcome of one executed cluster transaction.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum ClusterOutcome {
    /// The transaction spanned one shard and committed as one shard-local
    /// engine transaction: atomic with the receipt's observable result.
    Atomic(Box<ShardCommit>),
    /// The transaction spanned several shards and every shard committed
    /// its own shard-local transaction. Not atomic across shards — the
    /// recorded limits say so.
    PerShard {
        /// Every shard's commit, in canonical shard order.
        committed: Vec<ShardCommit>,
    },
    /// Some shards committed and one refused: the committed shards stay
    /// committed (no cross-shard rollback), the refusal names the refused
    /// shard, and a re-drive is idempotent per shard.
    Partial {
        /// The shards that committed before the refusal, in canonical
        /// shard order.
        committed: Vec<ShardCommit>,
        /// The first refusal, which stopped the execution.
        refused: ShardRefusal,
    },
    /// The first shard refused before anything committed.
    Refused {
        /// The refusal.
        refused: ShardRefusal,
    },
}

impl ClusterOutcome {
    /// Returns the recorded limits of this outcome: the atomicity scope
    /// for an atomic commit, and the non-atomicity, no-rollback, and
    /// shard-local-acknowledgement limits for every limited outcome.
    #[must_use]
    pub fn limits(&self) -> &'static [&'static str] {
        match self {
            Self::Atomic(_) => &[ATOMIC_WITHIN_ONE_SHARD, SHARD_LOCAL_ACKNOWLEDGEMENT],
            Self::PerShard { .. } | Self::Partial { .. } | Self::Refused { .. } => {
                &[CROSS_SHARD_NOT_ATOMIC, NO_CROSS_SHARD_ROLLBACK, SHARD_LOCAL_ACKNOWLEDGEMENT]
            }
        }
    }
}

/// Assembles the outcome for a refusal after `committed` shards.
fn refusal_outcome(committed: Vec<ShardCommit>, refused: ShardRefusal) -> ClusterOutcome {
    if committed.is_empty() {
        ClusterOutcome::Refused { refused }
    } else {
        ClusterOutcome::Partial { committed, refused }
    }
}

/// The serving seam one cluster transaction executes through: resolve a
/// committed operation by identity, and commit one shard-local transaction.
///
/// Each call carries the Platform-minted [`TenantScope`] of the addressed
/// Member's Engine — tenant context is backend-supplied
/// (PD-2026-08-26-11) and the engine seam has no public scope
/// constructor, so the executor's caller seats one scope per Member.
pub trait ShardWriteServing {
    /// Resolves the durable outcome of one operation identity at the
    /// endpoint serving `owner`, when that endpoint holds it committed.
    ///
    /// # Errors
    ///
    /// Returns the endpoint's own refusal, which always names the Member
    /// when no endpoint is seated for it.
    fn resolve(
        &self,
        owner: &MemberId,
        scope: &TenantScope,
        operation: &OperationId,
    ) -> Result<Option<CommitReceipt>, ShardWriteError>;

    /// Commits one shard's staged writes as one shard-local engine
    /// transaction at `LocalDurability::Synced`.
    ///
    /// # Errors
    ///
    /// Returns the endpoint's refusal: [`ShardWriteError::MemberUnserved`]
    /// when no endpoint serves the owner, and the engine's typed failures
    /// otherwise. A commit that resolves to pending after an unknown
    /// outcome returns [`ShardWriteError::Pending`] — never a guess.
    fn commit(
        &self,
        owner: &MemberId,
        scope: &TenantScope,
        operation: &OperationId,
        stages: &[ClusterStage],
    ) -> Result<CommitReceipt, ShardWriteError>;
}

/// The in-process serving set: one durable Engine per Member, exactly the
/// serving shape of the sharded query seam.
pub struct EngineWriteServing<'engine> {
    endpoints: Vec<(MemberId, &'engine Engine)>,
}

impl<'engine> EngineWriteServing<'engine> {
    /// Seats one Engine endpoint per Member.
    ///
    /// # Errors
    ///
    /// Returns the duplicated Member when one Member is seated twice: a
    /// serving set that seated a Member twice could route one shard's
    /// writes to two engines, so seating refuses it.
    pub fn try_new(endpoints: Vec<(MemberId, &'engine Engine)>) -> Result<Self, MemberId> {
        for (index, (member, _)) in endpoints.iter().enumerate() {
            if endpoints[..index].iter().any(|(seated, _)| seated == member) {
                return Err(member.clone());
            }
        }
        Ok(Self { endpoints })
    }

    fn engine_of(&self, owner: &MemberId) -> Result<&'engine Engine, ShardWriteError> {
        self.endpoints
            .iter()
            .find(|(seated, _)| seated == owner)
            .map(|(_, engine)| *engine)
            .ok_or(ShardWriteError::MemberUnserved { owner: owner.clone() })
    }
}

impl ShardWriteServing for EngineWriteServing<'_> {
    fn resolve(
        &self,
        owner: &MemberId,
        scope: &TenantScope,
        operation: &OperationId,
    ) -> Result<Option<CommitReceipt>, ShardWriteError> {
        let engine = self.engine_of(owner)?;
        match engine.resolve_commit(scope, operation.clone()) {
            Ok(DurableResolution::Committed(receipt)) => Ok(Some(receipt)),
            Ok(_) => Ok(None),
            Err(error) => Err(ShardWriteError::Engine(error)),
        }
    }

    fn commit(
        &self,
        owner: &MemberId,
        scope: &TenantScope,
        operation: &OperationId,
        stages: &[ClusterStage],
    ) -> Result<CommitReceipt, ShardWriteError> {
        let engine = self.engine_of(owner)?;
        // Idempotent by identity: an operation this engine already
        // committed resolves instead of committing twice.
        if let DurableResolution::Committed(receipt) =
            engine.resolve_commit(scope, operation.clone()).map_err(ShardWriteError::Engine)?
        {
            return Ok(receipt);
        }
        let snapshot = engine.read_snapshot(scope).map_err(ShardWriteError::Engine)?;
        let mut transaction =
            engine.begin(scope, snapshot, operation.clone()).map_err(ShardWriteError::Engine)?;
        for stage in stages {
            match stage {
                ClusterStage::Put(document) => {
                    transaction
                        .put(document.clone(), ExpectedVersion::Any)
                        .map_err(ShardWriteError::Engine)?;
                }
                ClusterStage::Delete { id, .. } => {
                    transaction
                        .delete(id.clone(), ExpectedVersion::Any)
                        .map_err(ShardWriteError::Engine)?;
                }
            }
        }
        match transaction.commit(LocalDurability::Synced) {
            CommitOutcome::Committed(receipt) => Ok(receipt),
            CommitOutcome::Rejected(reason) => Err(ShardWriteError::Rejected { reason }),
            CommitOutcome::Unknown(unknown) => {
                match engine.resolve_commit(scope, unknown.operation_id().clone()) {
                    Ok(DurableResolution::Committed(receipt)) => Ok(receipt),
                    Ok(_) => Err(ShardWriteError::Pending),
                    Err(error) => Err(ShardWriteError::Engine(error)),
                }
            }
        }
    }
}
