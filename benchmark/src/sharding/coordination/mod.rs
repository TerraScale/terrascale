//! Sharded failover, cluster-scoped operations, and per-shard recovery
//! coordination (SH-05, PB-5040/5041/5048; ADR 0011 D1/D4).
//!
//! This family owns the coordination decisions SH-01's dispositions left to
//! SH-05 (`docs/sharding/dispositions.md`), on the accepted model: cluster
//! coordination runs through the existing replication plane and
//! `TopologyView` ownership epochs — no second consensus service, no
//! engine-internal orchestrator, no Rachis port.
//!
//! - **Loss and failover** ([`FailoverPlan`]): a [`ShardLoss`] assessment
//!   names which shards lost their owning Member, in which Region and
//!   failure domain, and which shards keep serving — availability truth per
//!   shard, never a database-wide verdict. A [`FailoverDecision`] validates
//!   the Platform's replacement view: only shards whose owners are declared
//!   lost may change hands, successors must survive, the declared-lost
//!   owner is never re-seated, healthy shards may neither swap owners nor
//!   shrink, shard identities entering the table are judged as claims on
//!   the buckets they take (their owner must survive the loss and their
//!   epoch must fence everything those buckets held), and every transfer
//!   ends at a raised ownership epoch so the deposed owner is fenced by the
//!   existing placement-authorization checks.
//! - **Cluster-scoped operations** ([`ClusterStage`],
//!   [`ClusterOutcome`]): a [`ClusterTransaction`] routes its
//!   staged writes through the seated topology and executes them shard by
//!   shard. A single-shard transaction commits atomically as one
//!   shard-local engine transaction; a cross-shard transaction is
//!   **explicitly limited, never silently atomic**: every shard's
//!   observable commit or refusal is recorded, the shards a refusal
//!   stopped execution from reaching are recorded as unattempted, a
//!   refused shard does not roll back the shards that already committed,
//!   and a re-drive replays committed shards idempotently. A shard-local
//!   receipt is a `LocalDurability::Synced` fact about one shard and is
//!   never presented as database-wide acknowledgement.
//! - **Per-shard recovery** ([`ShardRecovery`]): a
//!   [`ShardRecoveryReport`] names every
//!   shard and every Member that contributed to a recovery observation,
//!   with the owner's verdict per shard — the shard-identifying receipt
//!   shape PB-5040 requires for replication, backup, and rejoin
//!   interactions.
//!
//! Boundaries: the Platform supplies every table and view (placement policy
//! stays out of the engine); the engine validates, executes, and reports.
//! The qualification scenarios that prove this seam run in the
//! crate-private lane under `lane.rs` and their receipts are recorded under
//! `evidence/sharding/`. No timing, production-topology, or performance
//! claim is made anywhere in this family.

pub(crate) mod failover;
pub(crate) mod recovery;
pub(crate) mod transactions;

#[cfg(test)]
pub(crate) mod lane;

pub use failover::{
    DispersedShard, FailoverDecision, FailoverError, FailoverPlan, FailoverReceipt, LostShard,
    ServingShard, ShardLoss, ShardTransfer,
};
pub use recovery::{MemberRecovery, RecoveryError, ShardRecovery, ShardRecoveryReport};
pub use transactions::{
    ATOMIC_WITHIN_ONE_SHARD, CROSS_SHARD_NOT_ATOMIC, ClusterOutcome, ClusterStage,
    ClusterTransaction, ClusterTransactionError, EngineWriteServing, NO_CROSS_SHARD_ROLLBACK,
    RoutedTransaction, SHARD_LOCAL_ACKNOWLEDGEMENT, ShardCommit, ShardRefusal, ShardWriteError,
    ShardWriteServing, ShardWriteSet,
};
