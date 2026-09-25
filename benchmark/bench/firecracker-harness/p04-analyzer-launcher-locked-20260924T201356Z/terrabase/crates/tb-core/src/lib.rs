//! Shared, stable domain contracts for `TerraBase` engine crates.

pub mod memory;

mod error;
mod types;

pub use error::CoreError;
pub use memory::{
    AccountScope, AdmissionError, Attribution, BudgetConfig, BudgetSnapshot, ConfigError,
    DEFAULT_MAX_CATEGORY_ITEMS, DEFAULT_MAX_PROCESS_ITEMS, DEFAULT_MAX_TENANT_ITEMS,
    DEFAULT_MAX_WORKER_ITEMS, ItemLimitScope, ItemReservation, ItemSplitError, ItemTransferError,
    LimitScope, MemoryBudget, MemoryCategory, ProcessBudgetAlreadyInitialized, Reservation,
    SplitError, TransferError, WorkReservation, WorkerId, initialize_process_memory_budget,
    process_memory_budget,
};
pub use types::{
    AckProfile, AckProfileKind, CollectionId, CommitFrontiers, CommitReceipt, CommitSeq,
    DatabaseId, DocumentId, FailureDomainId, Key, KeyBound, KeyRange, MAX_EVENT_METADATA_BYTES,
    MAX_KEY_BYTES, MAX_REPLICATION_BATCH_BYTES, MAX_VALUE_BYTES, MAX_WAL_GROUP_BYTES,
    MAX_WAL_OPERATIONS, MembershipEpoch, OriginId, ReadSnapshot, ReplicaId, ReplicaMember,
    ReplicatedDurability, ShardId, TenantId, TransactionId,
};
