//! Strict auth state and consensus storage, separate from causal documents.
//! Public admission must use the Raft leader and a quorum-confirmed read barrier.

use serde::{Deserialize, Serialize};
use std::collections::BTreeMap;
use std::io::Cursor;

mod authority;
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
