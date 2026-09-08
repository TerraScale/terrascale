//! Private multi-shard SQL serving cut and source-selection boundary.
//!
//! The member serving owner constructs [`RoutedReadCut`] only after the
//! shared route/read admission barrier closes. The query executor consumes the
//! borrowed cut to materialize authoritative route-owned relations into one
//! SQLite evaluator; it never executes a complete SQL statement independently
//! on each participant.

use std::sync::Arc;

use crate::{
    Engine,
    shard_split::{FileShardIdentity, ShardRoute},
    types::ReadSnapshot,
};

/// One admitted physical participant and the key ranges it owns at a route
/// generation.
pub(crate) struct RoutedReadParticipant {
    pub(crate) identity: FileShardIdentity,
    pub(crate) engine: Arc<Engine>,
    pub(crate) snapshot: ReadSnapshot,
    pub(crate) routes: Vec<ShardRoute>,
}

impl RoutedReadParticipant {
    pub(crate) fn new(
        identity: FileShardIdentity,
        engine: Arc<Engine>,
        snapshot: ReadSnapshot,
        routes: Vec<ShardRoute>,
    ) -> Result<Self, &'static str> {
        if identity.database_incarnation == 0
            || identity.origin_lane_incarnation == 0
            || routes.is_empty()
            || snapshot.engine_identity() != engine.identity()
        {
            return Err("invalid routed read participant");
        }
        if routes.iter().any(|route| route.shard_id() != identity.shard_id) {
            return Err("routed read range belongs to another shard");
        }
        Ok(Self { identity, engine, snapshot, routes })
    }
}

/// One immutable, coordinated multi-shard read cut for one database.
pub(crate) struct RoutedReadCut {
    pub(crate) database_incarnation: u128,
    pub(crate) route_generation: u64,
    pub(crate) participants: Vec<RoutedReadParticipant>,
}

impl RoutedReadCut {
    /// Constructs a cut after the serving owner has acquired the source
    /// barrier's exclusive read-admission guard and captured every snapshot.
    /// This constructor validates identity only; it does not claim to create
    /// causal consistency itself.
    pub(crate) fn new(
        database_incarnation: u128,
        route_generation: u64,
        participants: Vec<RoutedReadParticipant>,
    ) -> Result<Self, &'static str> {
        if database_incarnation == 0 || route_generation == 0 || participants.is_empty() {
            return Err("invalid routed read cut");
        }
        if participants
            .iter()
            .any(|participant| participant.identity.database_incarnation != database_incarnation)
        {
            return Err("routed read cut identity mismatch");
        }
        let mut seen = std::collections::BTreeSet::new();
        if participants.iter().any(|participant| !seen.insert(participant.identity.shard_id)) {
            return Err("duplicate routed read participant");
        }
        Ok(Self { database_incarnation, route_generation, participants })
    }
}
