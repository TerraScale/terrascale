//! `TerraBaseV2` engine foundation.
//!
//! This crate intentionally contains no database behavior yet. The public
//! seams, parity obligations, and evidence rules live in the workspace charter
//! and parity inventory. Feature work must add behavior behind those seams and
//! prove it at the corresponding interface.

#![deny(unsafe_code)]
#![deny(missing_docs)]
#![warn(clippy::all, clippy::pedantic)]

/// The crate version selected by Cargo for evidence and artifact identity.
pub const VERSION: &str = env!("CARGO_PKG_VERSION");

/// The current repository status. This is metadata, not a database feature.
pub const STATUS: &str = "foundation-only";

pub mod adapters;
pub mod engine;
pub mod families;
pub mod index;
pub mod operations;
pub mod query;
pub mod storage;
pub mod types;

pub use adapters::platform::{PlatformAdapter, PlatformError};
pub use adapters::protocol::{
    CallerContext, CallerContextError, CallerEvidence, CredentialInput, CredentialKind, PrincipalId,
};
pub use adapters::protocol::{ProtocolAdapter, ProtocolError};
pub use engine::{
    CommitError, CommitOutcome, CommitReceipt, DurableResolution, Engine, EngineClosed,
    EngineConfig, EngineError, EngineOpenError, EngineShutdownError, Transaction, UnknownCommit,
    UnknownReason,
};
pub use families::{
    FamilyError, FamilyKind, FamilyReadContext, FamilyReadRequest, FamilyReadResult, FamilyStore,
    FamilyWriteRequest,
};
pub use operations::{
    DocumentReadRequest, DocumentWriteRequest, FamilyOperation, PublicOperation, PublicResponse,
    QueryInput, QueryInputError, QueryOperation, RequestValidationError, V1QueryText,
};
pub use query::{
    QueryError, QueryOrdering, QueryPaging, QueryPlan, QueryPredicate, QueryProjection,
    QueryResult, QuerySource, QuerySpec, QueryStore, StalenessPolicy,
};
pub use types::{
    Cancellation, Deadline, Document, DocumentId, DocumentMetadata, DocumentValue, ExpectedVersion,
    IndexGeneration, IndexGenerationSet, LocalDurability, OperationId, ReadSnapshot,
    RetryDisposition, StorageSequence, TenantId, TenantScope,
};

#[cfg(test)]
mod tests {
    use super::{STATUS, VERSION};

    #[test]
    fn foundation_metadata_is_present() {
        assert_eq!(STATUS, "foundation-only");
        assert!(!VERSION.is_empty());
    }
}
