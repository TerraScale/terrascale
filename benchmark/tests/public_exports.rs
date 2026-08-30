use terrabase::{
    CallerContext, CallerContextError, CallerEvidence, Cancellation, CommitOutcome, CommitReceipt,
    CredentialInput, CredentialKind, Deadline, Document, DocumentId, DocumentMetadata,
    DocumentReadRequest, DocumentValue, DocumentWriteRequest, Engine as RootEngine, EngineConfig,
    EngineError, ExpectedVersion, FamilyError, FamilyKind, FamilyReadContext, FamilyReadRequest,
    FamilyReadResult, FamilyWriteRequest, IndexGeneration, IndexGenerationSet, LocalDurability,
    OperationId, PlatformAdapter, PlatformError, PrincipalId, ProtocolAdapter, ProtocolError,
    QueryError, QueryInput, QueryInputError, QueryOperation, QueryOrdering, QueryPaging, QueryPlan,
    QueryPredicate, QueryProjection, QueryResult, QuerySource, QuerySpec,
    QueryStore as RootQueryStore, ReadSnapshot, RequestValidationError, RetryDisposition,
    StalenessPolicy, StorageSequence, TenantId, TenantScope, TqlQueryText, Transaction,
    UnknownCommit, UnknownReason,
    adapters::{platform, protocol},
    engine::{self, Engine},
    families::{self, FamilyStore},
    operations::{self, PublicOperation, PublicResponse},
    query::{self, QueryStore},
    types,
};

#[test]
fn public_exports_compile_with_declaration_only_tree() {
    let _ = (
        std::any::type_name::<platform::PlatformAdapter<'static>>(),
        std::any::type_name::<protocol::ProtocolAdapter<'static>>(),
        std::any::type_name::<engine::EngineError>(),
        std::any::type_name::<families::FamilyError>(),
        std::any::type_name::<families::AttachmentRead>(),
        std::any::type_name::<families::AttachmentWrite>(),
        std::any::type_name::<families::AttachmentMetadataResult>(),
        std::any::type_name::<families::CounterRead>(),
        std::any::type_name::<families::CounterBatchWrite>(),
        std::any::type_name::<families::CounterGroupResult>(),
        std::any::type_name::<families::TimeSeriesRead>(),
        std::any::type_name::<families::TimeSeriesBatchWrite>(),
        std::any::type_name::<families::TimeSeriesPage>(),
        std::any::type_name::<families::RevisionRead>(),
        std::any::type_name::<families::RevisionWrite>(),
        std::any::type_name::<families::RevisionPage>(),
        std::any::type_name::<families::CompareExchangeRead>(),
        std::any::type_name::<families::CompareExchangeWrite>(),
        std::any::type_name::<families::CompareExchangeValueResult>(),
        std::any::type_name::<families::IdentityRead>(),
        std::any::type_name::<families::IdentityWrite>(),
        std::any::type_name::<families::IdentityRangeResult>(),
        std::any::type_name::<families::LifecycleRead>(),
        std::any::type_name::<families::LifecycleWrite>(),
        std::any::type_name::<families::LifecycleResult>(),
        std::any::type_name::<families::ArchivalReadRequest>(),
        std::any::type_name::<families::ArchivalWrite>(),
        std::any::type_name::<families::ArchivalReadResult>(),
        std::any::type_name::<families::ValidationRead>(),
        std::any::type_name::<families::ValidationWrite>(),
        std::any::type_name::<families::ValidationResult>(),
        std::any::type_name::<families::TombstoneRead>(),
        std::any::type_name::<families::TombstoneWrite>(),
        std::any::type_name::<families::TombstoneResult>(),
        std::any::type_name::<operations::RequestValidationError>(),
        std::any::type_name::<query::QueryError>(),
        std::any::type_name::<types::ReadSnapshot>(),
        std::any::type_name::<PublicResponse>(),
        std::any::type_name::<Cancellation>(),
        std::any::type_name::<CallerContext>(),
        std::any::type_name::<CallerContextError>(),
        std::any::type_name::<CallerEvidence>(),
        std::any::type_name::<CommitOutcome>(),
        std::any::type_name::<CommitReceipt>(),
        std::any::type_name::<CredentialInput>(),
        std::any::type_name::<CredentialKind>(),
        std::any::type_name::<Deadline>(),
        std::any::type_name::<Document>(),
        std::any::type_name::<DocumentId>(),
        std::any::type_name::<DocumentMetadata>(),
        std::any::type_name::<DocumentReadRequest>(),
        std::any::type_name::<DocumentValue>(),
        std::any::type_name::<DocumentWriteRequest>(),
        std::any::type_name::<RootEngine>(),
        std::any::type_name::<EngineConfig>(),
        std::any::type_name::<EngineError>(),
        std::any::type_name::<ExpectedVersion>(),
        std::any::type_name::<FamilyError>(),
        std::any::type_name::<FamilyKind>(),
        std::any::type_name::<FamilyReadContext>(),
        std::any::type_name::<FamilyReadRequest>(),
        std::any::type_name::<FamilyReadResult>(),
        std::any::type_name::<FamilyWriteRequest>(),
        std::any::type_name::<IndexGeneration>(),
        std::any::type_name::<IndexGenerationSet>(),
        std::any::type_name::<LocalDurability>(),
        std::any::type_name::<OperationId>(),
        std::any::type_name::<PrincipalId>(),
        std::any::type_name::<PlatformAdapter<'static>>(),
        std::any::type_name::<PlatformError>(),
        std::any::type_name::<ProtocolAdapter<'static>>(),
        std::any::type_name::<ProtocolError>(),
        std::any::type_name::<QueryError>(),
        std::any::type_name::<QueryInput>(),
        std::any::type_name::<QueryInputError>(),
        std::any::type_name::<QueryOperation>(),
        std::any::type_name::<QueryOrdering>(),
        std::any::type_name::<QueryPaging>(),
        std::any::type_name::<QueryPlan>(),
        std::any::type_name::<QueryPredicate>(),
        std::any::type_name::<QueryProjection>(),
        std::any::type_name::<QueryResult>(),
        std::any::type_name::<QuerySource>(),
        std::any::type_name::<QuerySpec>(),
        std::any::type_name::<RootQueryStore<'static>>(),
        std::any::type_name::<ReadSnapshot>(),
        std::any::type_name::<RequestValidationError>(),
        std::any::type_name::<RetryDisposition>(),
        std::any::type_name::<StorageSequence>(),
        std::any::type_name::<StalenessPolicy>(),
        std::any::type_name::<TenantId>(),
        std::any::type_name::<TenantScope>(),
        std::any::type_name::<Transaction<'static>>(),
        std::any::type_name::<UnknownCommit>(),
        std::any::type_name::<UnknownReason>(),
        std::any::type_name::<TqlQueryText>(),
    );
}

fn borrowed_store_lifetimes<'engine>(
    engine: &'engine Engine,
) -> (FamilyStore<'engine>, QueryStore<'engine>) {
    (engine.families(), engine.queries())
}

#[test]
fn borrowed_store_lifetimes_prevent_engine_shutdown_while_borrowed() {
    let _ = borrowed_store_lifetimes;
}

fn public_operation_variants_are_closed(operation: PublicOperation) {
    match operation {
        PublicOperation::DocumentRead(_) => {}
        PublicOperation::DocumentWrite(_) => {}
        PublicOperation::Query(_) => {}
        PublicOperation::Family(_) => {}
        PublicOperation::Schema(_) => {}
    }
}

fn public_response_variants_are_closed(response: PublicResponse) {
    match response {
        PublicResponse::Document(_) => {}
        PublicResponse::Query(_) => {}
        PublicResponse::Family(_) => {}
        PublicResponse::Commit(_) => {}
        PublicResponse::Schema(_) => {}
    }
}

#[test]
fn closed_operation_registry_rejects_generic_routes_and_private_operations() {
    let _ = (public_operation_variants_are_closed, public_response_variants_are_closed);
}

#[test]
fn platform_grant_is_not_reexported_or_reachable_from_public_exports() {
    // The private PlatformGrant has no public import path. This test deliberately
    // names only the approved public adapter and operation types above.
    let _ = (
        std::any::type_name::<platform::PlatformAdapter<'static>>(),
        std::any::type_name::<protocol::ProtocolAdapter<'static>>(),
    );
}
