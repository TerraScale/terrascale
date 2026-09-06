//! Strict auth state and consensus storage, separate from causal documents.
//! Public admission must use the Raft leader and a quorum-confirmed read barrier.

use serde::{Deserialize, Serialize};
use std::collections::BTreeMap;
use std::io::Cursor;

mod authority;
mod query;
pub use query::{Connector, Mode, Operator, Predicate};
mod network;
mod store;
pub use authority::{AuthAuthority, AuthorityError};
pub use network::AuthNetwork;

#[cfg(test)]
mod tests;
pub use store::SqliteStore;

openraft::declare_raft_types!(
    /// Types for one database's strict auth consensus group.
    pub TypeConfig:
        D = Command,
        R = Outcome,
);

/// A JSON auth record with deterministic field ordering.
pub type Record = BTreeMap<String, serde_json::Value>;

/// One retriable operation; identity is persisted with its result.
#[derive(Clone, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Command {
    /// Opaque stable key scoped by this consensus group's database identity.
    pub idempotency_key: String,
    /// Server-observed time, persisted through consensus; excluded from retry digest.
    #[serde(default)]
    pub observed_at_ms: u64,
    /// Named conditional operation.
    pub action: Action,
}

/// Operations admitted by the strict state machine.
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(tag = "operation", rename_all = "camelCase", deny_unknown_fields)]
pub enum Action {
    /// Register a mandatory unique index for subsequent inserts and updates.
    CreateUniqueIndex { model: String, fields: Vec<String> },
    /// Insert a new record, respecting all registered unique indexes.
    InsertUnique { model: String, record: Record },
    /// Update one identified record if all supplied predicates still match.
    CompareAndSwap { model: String, id: String, expected: Record, update: Record },
    /// Delete and return one identified record if all predicates still match.
    ConsumeOne { model: String, id: String, expected: Record },
    /// Update the lowest-id matching record.
    UpdateOne { model: String, predicates: Vec<Predicate>, update: Record },
    /// Update all matching records atomically within the mutation bound.
    UpdateMany { model: String, predicates: Vec<Predicate>, update: Record },
    /// Delete the lowest-id matching record.
    DeleteOne { model: String, predicates: Vec<Predicate> },
    /// Delete all matching records atomically within the mutation bound.
    DeleteMany { model: String, predicates: Vec<Predicate> },
    /// Increment one or more numeric fields on the lowest-id matching record.
    IncrementOne {
        model: String,
        predicates: Vec<Predicate>,
        increment: BTreeMap<String, serde_json::Number>,
        set: Record,
    },
    /// Delete and return the lowest-id matching record.
    ConsumeMatching { model: String, predicates: Vec<Predicate> },
    /// Count all matching records exactly.
    Count { model: String, predicates: Vec<Predicate> },
    /// Read a bounded model page using exact field predicates.
    Find { model: String, predicates: Vec<Predicate>, offset: u32, limit: u32 },
    /// Start a bounded optimistic transaction using a client label of at most 100 bytes.
    /// Subsequent operations must use the returned generation-qualified identity.
    BeginTransaction { transaction_id: String },
    /// Apply one named command against the private transaction workspace.
    TransactionOperation { transaction_id: String, action: Box<Action> },
    /// Commit if no ordinary authority mutation changed the captured revision.
    CommitTransaction { transaction_id: String },
    /// Discard an unfinished workspace without reverting external callback effects.
    RollbackTransaction { transaction_id: String },
}

/// Deterministic results, including conflicts, are persisted for replay.
#[derive(Clone, Debug, Default, PartialEq, Serialize, Deserialize)]
#[serde(tag = "status", rename_all = "camelCase")]
pub enum Outcome {
    /// Successful operation with an optional returned record.
    #[default]
    Applied,
    /// Returned created, updated, or consumed record.
    Record { record: Record },
    /// The conditional predicate did not match an existing record.
    NotMatched,
    /// Bounded query records.
    Records { records: Vec<Record> },
    /// Number of records affected or counted.
    Count { count: u64 },
    /// An optimistic transaction started at a fixed revision and expiry.
    /// `transaction_id` is the exact server-issued identity for all subsequent commands.
    Transaction { transaction_id: String, expires_at_ms: u64 },
    /// A transaction conflicted, expired, or previously failed an inner operation.
    TransactionConflict,
    /// A persisted uniqueness constraint was violated.
    UniqueConflict,
    /// A key already belongs to a different canonical command.
    IdempotencyConflict,
    /// The command was rejected before mutation.
    Invalid { reason: String },
}

impl std::fmt::Debug for Command {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        // Raft tracing must never print credentials or recovery tokens in commands.
        formatter.write_str("AuthCommand([redacted])")
    }
}

/// Bounded consensus transport and snapshot settings for one admitted auth group.
pub fn consensus_config(group: &str) -> openraft::Config {
    openraft::Config {
        cluster_name: group.into(),
        max_payload_entries: 1,
        snapshot_max_chunk_size: 64 * 1024,
        ..Default::default()
    }
}
