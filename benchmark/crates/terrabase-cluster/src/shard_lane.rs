//! Immutable file identity used by physical replication and internal dispatch.

use crate::production::DatabaseRouteScope;

/// One persisted SQLite file belonging to an immutable database incarnation.
///
/// The originating causal actor is a separate identity: physical followers
/// replicate this same file without acquiring their own originating stream.
#[derive(Clone, Copy, Debug, Eq, Ord, PartialEq, PartialOrd, Hash)]
pub struct ShardLaneIdentity {
    scope: DatabaseRouteScope,
    database_incarnation: u128,
    shard_id: u64,
}

/// A file identity is incomplete or disagrees with its persisted incarnation.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct InvalidShardLaneIdentity;

impl ShardLaneIdentity {
    /// Constructs an exact lane from accepted and persisted identity facts.
    ///
    /// # Errors
    /// Rejects zero incarnation and shard identifiers, as the engine route
    /// admission codec does. Callers must additionally authenticate authority.
    pub fn new(
        scope: DatabaseRouteScope,
        database_incarnation: u128,
        shard_id: u64,
    ) -> Result<Self, InvalidShardLaneIdentity> {
        if database_incarnation == 0 || shard_id == 0 {
            return Err(InvalidShardLaneIdentity);
        }
        Ok(Self { scope, database_incarnation, shard_id })
    }

    /// Logical database scope; it does not distinguish sibling files.
    #[must_use]
    pub const fn scope(self) -> DatabaseRouteScope {
        self.scope
    }

    /// Database incarnation persisted by the owning engine.
    #[must_use]
    pub const fn database_incarnation(self) -> u128 {
        self.database_incarnation
    }

    /// Shard identifier persisted by the owning engine.
    #[must_use]
    pub const fn shard_id(self) -> u64 {
        self.shard_id
    }
}
