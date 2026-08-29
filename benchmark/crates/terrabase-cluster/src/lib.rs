//! The cluster lifecycle crate (P5): immutable cluster identity,
//! membership epochs, and the cluster-mode serving seam.
//!
//! The frozen contracts this crate implements: the backup-cluster
//! contract (mode frozen at creation, backup terminal, no promotion
//! path anywhere, permanent typed write refusal with the replication
//! position attached) and the NATS control contract (control metadata
//! only — data rides engine-owned QUIC links; the in-process control
//! plane here carries the typed semantics, and the live-server rows
//! are recorded unrun where no broker runs).

pub mod backup;
pub mod control;
pub mod durability;
pub mod host;
pub mod identity;
pub mod member;
pub mod reads;

pub use backup::{BackupOutcome, BackupPolicy, BackupSet};
pub use control::{
    ConfigEpochRecord, ControlCluster, ControlError, CtrlReply, CtrlRequest, CtrlSubject,
    InProcessControlPlane, JoinToken, KvBucket, KvPayload,
};
pub use durability::{
    DurabilityCoordinator, DurabilityError, DurabilityFailure, DurabilityLevel, DurabilityReceipt,
    DurableAck, Membership, MembershipError, PartialObservation, RegionMembers,
};
pub use host::{ClusterError, ClusterHost, ClusterWriteRefusal, MemberSpec};

// Re-exported so the serving seam builds writes without a direct
// dependency on the replication core.
pub use identity::{ClusterIdentity, ClusterMode, IdentityError};
pub use member::{ClusterMember, MemberError, MutationRefusal};
pub use terrabase_replication_core::compose::ComposedWrite;
