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

pub mod host;
pub mod identity;
pub mod member;

pub use host::{ClusterError, ClusterHost, ClusterWriteRefusal, MemberSpec};
pub use identity::{ClusterIdentity, ClusterMode, IdentityError};
pub use member::{ClusterMember, MemberError, MutationRefusal};
