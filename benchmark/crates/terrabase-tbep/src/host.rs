//! The TBEP hosting seam: what the engine process serves.
//!
//! [`TbepHost`] is the seam the engine process wires behind TBEP: one
//! method per typed operation, the subscription feeds of the backbone
//! streams, and the supervision contract. The server (`crate::server`)
//! serves any `TbepHost` over the UDS binding.
//!
//! Three hosts ship in this crate:
//!
//! - [`crate::member_host::MemberHost`] — the opt-in TB-1a composition of one
//!   engine with journal-derived sync and backbone delivery.
//! - [`SyncDeliveryHost`] — the real serving half over the adopted sync
//!   core (`terrabase-sync-core`, the SE-02 GO decision): sync sessions,
//!   byte-budgeted delta and snapshot delivery, checkpoint
//!   acknowledgements, server-ack mutations with idempotent retry
//!   identities, the journal-backed backbone streams with content-free
//!   trigger notifications (PD-2026-08-26-13 D3), and supervision. This
//!   is the delivery layer TBEP's sync-delivery frames exist to serve.
//! - [`ScriptedHost`] — the conformance fixture used by the golden
//!   vectors and the adversarial suite to drive the document/query/family
//!   operation frames through the wire with scripted, deterministic
//!   outcomes. It is a fixture, not an engine: it invents no database
//!   behavior.
//!
//! [`crate::engine_host::EngineHost`] remains the production harness during
//! this staged landing. `MemberHost` is available only through its explicit
//! construction option.

use std::collections::BTreeMap;
use std::collections::VecDeque;
use std::sync::Mutex;
use std::sync::MutexGuard;
use std::sync::mpsc::Receiver;
use std::sync::mpsc::SyncSender;
use std::sync::mpsc::sync_channel;

use terrabase_sync_core::JournalChange;
use terrabase_sync_core::JournalCommit;
use terrabase_sync_core::NATIVE_DELTA_MAGIC;
use terrabase_sync_core::NATIVE_SNAPSHOT_MAGIC;
use terrabase_sync_core::SyncBatch;
use terrabase_sync_core::SyncCheckpoint;
use terrabase_sync_core::SyncCore;
use terrabase_sync_core::SyncCoreConfig;
use terrabase_sync_core::SyncCoreError;
use terrabase_sync_core::SyncScope;
use terrabase_sync_core::SyncValue;
use terrabase_sync_core::protocol::PayloadCodec;

use terrabase::streaming::{ChangeCheckpoint, SyncJournalMutation, SyncJournalRecord};
use terrabase::types::DocumentValue;

use crate::registry::AdminStatusResponse;
use crate::registry::BackboneAcknowledgeRequest;
use crate::registry::BackboneAcknowledgeResponse;
use crate::registry::BackboneSubscribeRequest;
use crate::registry::BackboneSubscribeResponse;
use crate::registry::BackboneUnsubscribeRequest;
use crate::registry::BackboneUnsubscribeResponse;
use crate::registry::BoundScope;
use crate::registry::ChangeKind;
use crate::registry::ChannelBindRequest;
use crate::registry::ChannelBindResponse;
use crate::registry::CommitReceipt;
use crate::registry::DocumentDeleteRequest;
use crate::registry::DocumentDeleteResponse;
use crate::registry::DocumentReadRequest;
use crate::registry::DocumentReadResponse;
use crate::registry::DocumentWriteRequest;
use crate::registry::DocumentWriteResponse;
use crate::registry::ErrorCode;
use crate::registry::FamilyReadRequest;
use crate::registry::FamilyReadResponse;
use crate::registry::FamilyWriteRequest;
use crate::registry::FamilyWriteResponse;
use crate::registry::HealthState;
use crate::registry::MigrationApplyRequest;
use crate::registry::MigrationApplyResponse;
use crate::registry::MigrationDryRunRequest;
use crate::registry::MigrationDryRunResponse;
use crate::registry::MigrationStatusRequest;
use crate::registry::MigrationStatusResponse;
use crate::registry::MutationOutcome;
use crate::registry::MutationReason;
use crate::registry::QueryCancelRequest;
use crate::registry::QueryCancelResponse;
use crate::registry::QueryExecuteRequest;
use crate::registry::QueryExecuteResponse;
use crate::registry::REGISTRY_VERSION;
use crate::registry::SchemaDeclareRequest;
use crate::registry::SchemaDeclareResponse;
use crate::registry::SchemaStatusRequest;
use crate::registry::SchemaStatusResponse;
use crate::registry::SchemaValidateRequest;
use crate::registry::SchemaValidateResponse;
use crate::registry::SubscriptionBatch;
use crate::registry::SubscriptionCloseReason;
use crate::registry::SubscriptionClosed;
use crate::registry::SubscriptionStart;
use crate::registry::SupervisionHealthResponse;
use crate::registry::SupervisionShutdownRequest;
use crate::registry::SupervisionShutdownResponse;
use crate::registry::SyncCheckpointAckRequest;
use crate::registry::SyncCheckpointAckResponse;
use crate::registry::SyncDeltaPullRequest;
use crate::registry::SyncDeltaPullResponse;
use crate::registry::SyncMutationSubmitRequest;
use crate::registry::SyncMutationSubmitResponse;
use crate::registry::SyncSessionCloseRequest;
use crate::registry::SyncSessionCloseResponse;
use crate::registry::SyncSessionSetupRequest;
use crate::registry::SyncSessionSetupResponse;
use crate::registry::SyncSnapshotPullRequest;
use crate::registry::SyncSnapshotPullResponse;
use crate::registry::TopologyReadResponse;
use crate::value::TbepValue;

/// The batch byte budget the serving core configures (the SE-04 corpus
/// default); a single oversize commit rides alone, flagged.
pub const BATCH_BYTE_BUDGET: usize = 8192;

/// The checkpoint window the serving core retains.
pub const CHECKPOINT_WINDOW: usize = 128;

/// The idempotency retry horizon: how many of each scope's most recent
/// applied mutation keys keep their receipts for at-least-once retry
/// deduplication. The registry is bounded drop-oldest at this size —
/// sized against the checkpoint window — so serving memory stays
/// bounded; a consumer that retries a key older than the horizon (a
/// retry behind more than a full retained window of mutations) may have
/// its mutation re-applied: the documented at-least-once caveat.
pub const IDEMPOTENCY_RETRY_HORIZON: usize = CHECKPOINT_WINDOW;

/// The journal bound per scope (bounded memory: the retained window).
const JOURNAL_BOUND: usize = CHECKPOINT_WINDOW;

/// Bounds serving memory and the amount of work one mutation performs for
/// backbone consumers. These are deliberately conservative host-wide caps;
/// callers must refuse new resources once a cap is reached.
const SUBSCRIPTION_QUEUE_BOUND: usize = 64;
const MAX_SCOPES: usize = 4096;
/// Engine-owned family records participate in the durable backbone, but they
/// are not a customer sync-session collection.
const RESERVED_FAMILY_COLLECTION: &str = "terrabase.families";
const MAX_SESSIONS: usize = 4096;
const MAX_SUBSCRIPTIONS: usize = 1024;
const MAX_SESSIONS_PER_TENANT: usize = 256;
const MAX_SUBSCRIPTIONS_PER_TENANT: usize = 128;

/// The maximum encoded native checkpoint accepted by this host. The core's
/// decoder enforces the same entry bound; checking the byte bound first keeps
/// malformed TBEP bodies from reaching even bounded decode allocation.
const MAX_SYNC_CHECKPOINT_BYTES: usize = 1 + 8 + 4 + 64 * 16;

/// Native payloads are bounded by the generated TBEP frame limit and by the
/// sync core's native decoder.
const MAX_SYNC_PAYLOAD_BYTES: usize = 16 * 1024 * 1024;

/// The per-host failure vocabulary; every row maps onto a closed TBEP
/// error code.
#[derive(Clone, Debug, PartialEq)]
pub enum HostingError {
    /// The host refuses the operation (error code `operation_refused`).
    Refused(String),
    /// The request is invalid for this host (error code `invalid_request`).
    Invalid(String),
    /// The session is unknown or refused (error code `session_refused`).
    SessionRefused(String),
    /// The subscription is unknown or refused (error code
    /// `subscription_refused`).
    SubscriptionRefused(String),
    /// The checkpoint is unknown, ahead, or behind the retained floor
    /// (error code `checkpoint_refused`).
    CheckpointRefused(String),
    /// The engine-side execution failed (error code `engine_failure`).
    EngineFailure(String),
    /// A client mutation met a backup cluster's permanent refusal
    /// (error code `backup_cluster_read_only`, P5): the detail carries
    /// the replication position.
    ClusterReadOnly(String),
    /// A cluster mode change attempt met the immutable identity
    /// (error code `backup_cluster_immutable`, P5).
    ClusterImmutable(String),
    /// Authoritative usage is unavailable on this host or backend.
    UsageUnavailable(String),
    /// Persisted usage failed canonical or checksum verification.
    UsageCorrupt(String),
}

impl HostingError {
    /// The closed error code this failure maps onto.
    #[must_use]
    pub fn error_code(&self) -> ErrorCode {
        match self {
            Self::Refused(_) => ErrorCode::OperationRefused,
            Self::Invalid(_) => ErrorCode::InvalidRequest,
            Self::SessionRefused(_) => ErrorCode::SessionRefused,
            Self::SubscriptionRefused(_) => ErrorCode::SubscriptionRefused,
            Self::CheckpointRefused(_) => ErrorCode::CheckpointRefused,
            Self::EngineFailure(_) => ErrorCode::EngineFailure,
            Self::ClusterReadOnly(_) => ErrorCode::BackupClusterReadOnly,
            Self::ClusterImmutable(_) => ErrorCode::BackupClusterImmutable,
            Self::UsageUnavailable(_) => ErrorCode::UsageUnavailable,
            Self::UsageCorrupt(_) => ErrorCode::UsageCorrupt,
        }
    }

    /// The bounded machine-readable detail.
    #[must_use]
    pub fn detail(&self) -> String {
        match self {
            Self::Refused(detail)
            | Self::Invalid(detail)
            | Self::SessionRefused(detail)
            | Self::ClusterReadOnly(detail)
            | Self::ClusterImmutable(detail)
            | Self::UsageUnavailable(detail)
            | Self::UsageCorrupt(detail)
            | Self::SubscriptionRefused(detail)
            | Self::CheckpointRefused(detail)
            | Self::EngineFailure(detail) => detail.clone(),
        }
    }
}

/// One outbound notification of a backbone subscription stream.
#[derive(Clone, Debug, PartialEq)]
pub enum TbepNotification {
    /// An ordered, bounded batch of change events.
    Batch(SubscriptionBatch),
    /// The content-free trigger notification (PD-2026-08-26-13 D3).
    Trigger(crate::registry::TriggerNotification),
    /// The subscription closed.
    Closed(SubscriptionClosed),
}

/// The hosting seam the engine process wires behind TBEP.
///
/// One method per typed operation; the server enforces the channel
/// binding before any database-scoped method runs, so every operation receives
/// the same validated gateway, tenant, Project, and Database identity.
pub trait TbepHost: Send + Sync {
    /// Binds one channel to the backend-supplied database scope.
    ///
    /// # Errors
    ///
    /// Returns [`HostingError::Refused`] when the host refuses the
    /// binding.
    fn bind_channel(
        &self,
        request: &ChannelBindRequest,
    ) -> Result<ChannelBindResponse, HostingError>;

    /// Serves one typed document read.
    ///
    /// # Errors
    ///
    /// Returns the host's typed refusal.
    fn document_read(
        &self,
        scope: &BoundScope,
        request: &DocumentReadRequest,
    ) -> Result<DocumentReadResponse, HostingError>;

    /// Serves one typed document write (mutation; the idempotency key is
    /// the envelope's).
    ///
    /// # Errors
    ///
    /// Returns the host's typed refusal.
    fn document_write(
        &self,
        scope: &BoundScope,
        request: &DocumentWriteRequest,
        idempotency_key: &str,
    ) -> Result<DocumentWriteResponse, HostingError>;

    /// Serves one typed document delete (mutation).
    ///
    /// # Errors
    ///
    /// Returns the host's typed refusal.
    fn document_delete(
        &self,
        scope: &BoundScope,
        request: &DocumentDeleteRequest,
        idempotency_key: &str,
    ) -> Result<DocumentDeleteResponse, HostingError>;

    /// Serves one typed query execution.
    ///
    /// # Errors
    ///
    /// Returns the host's typed refusal.
    fn query_execute(
        &self,
        scope: &BoundScope,
        request: &QueryExecuteRequest,
    ) -> Result<QueryExecuteResponse, HostingError>;

    /// Serves one query-cancellation live signal (GAP-Q12: the wire-side
    /// cancellation lifecycle handoff).
    ///
    /// # Errors
    ///
    /// Returns the host's typed refusal.
    fn query_cancel(
        &self,
        scope: &BoundScope,
        request: &QueryCancelRequest,
    ) -> Result<QueryCancelResponse, HostingError>;

    /// Serves one typed family read.
    ///
    /// # Errors
    ///
    /// Returns the host's typed refusal.
    fn family_read(
        &self,
        scope: &BoundScope,
        request: &FamilyReadRequest,
    ) -> Result<FamilyReadResponse, HostingError>;

    /// Serves one typed family write (mutation).
    ///
    /// # Errors
    ///
    /// Returns the host's typed refusal.
    fn family_write(
        &self,
        scope: &BoundScope,
        request: &FamilyWriteRequest,
        idempotency_key: &str,
    ) -> Result<FamilyWriteResponse, HostingError>;

    /// Serves one atomic document batch (P4, mutation): one frame, one
    /// engine transaction — every item commits together or none does.
    ///
    /// # Errors
    ///
    /// Returns [`HostingError::Refused`] unless a host serves batches.
    fn document_batch(
        &self,
        scope: &BoundScope,
        request: &crate::registry::DocumentBatchRequest,
        idempotency_key: &str,
    ) -> Result<crate::registry::DocumentBatchResponse, HostingError> {
        let _ = (scope, request, idempotency_key);
        Err(HostingError::Refused(
            "document.batch is served by the engine-backed host's transaction path".into(),
        ))
    }

    /// Serves one typed schema declaration (SM-04, mutation).
    ///
    /// The default implementation refuses typed: the shipped hosts do not
    /// serve the schema lifecycle, and serving it from a live engine
    /// process is the engine-backed host integrator action the SM-04
    /// record names.
    ///
    /// # Errors
    ///
    /// Returns [`HostingError::Refused`] unless a host serves the seam.
    fn schema_declare(
        &self,
        _scope: &BoundScope,
        _request: &SchemaDeclareRequest,
        _idempotency_key: &str,
    ) -> Result<SchemaDeclareResponse, HostingError> {
        Err(HostingError::Refused("schema declarations are not served by this host".into()))
    }

    /// Serves one typed schema status read (SM-04).
    ///
    /// # Errors
    ///
    /// Returns [`HostingError::Refused`] unless a host serves the seam.
    fn schema_status(
        &self,
        _scope: &BoundScope,
        _request: &SchemaStatusRequest,
    ) -> Result<SchemaStatusResponse, HostingError> {
        Err(HostingError::Refused("schema status is not served by this host".into()))
    }

    /// Serves one typed on-demand document validation (SM-04).
    ///
    /// # Errors
    ///
    /// Returns [`HostingError::Refused`] unless a host serves the seam.
    fn schema_validate(
        &self,
        _scope: &BoundScope,
        _request: &SchemaValidateRequest,
    ) -> Result<SchemaValidateResponse, HostingError> {
        Err(HostingError::Refused("schema validation is not served by this host".into()))
    }

    /// Serves one typed Schema Migration status read (SM-04).
    ///
    /// # Errors
    ///
    /// Returns [`HostingError::Refused`] unless a host serves the seam.
    fn migration_status(
        &self,
        _scope: &BoundScope,
        _request: &MigrationStatusRequest,
    ) -> Result<MigrationStatusResponse, HostingError> {
        Err(HostingError::Refused("migration status is not served by this host".into()))
    }

    /// Serves one typed migration dry run (SM-04; mutating nothing).
    ///
    /// # Errors
    ///
    /// Returns [`HostingError::Refused`] unless a host serves the seam.
    fn migration_dry_run(
        &self,
        _scope: &BoundScope,
        _request: &MigrationDryRunRequest,
    ) -> Result<MigrationDryRunResponse, HostingError> {
        Err(HostingError::Refused("migration dry runs are not served by this host".into()))
    }

    /// Serves one typed migration apply (SM-04, mutation).
    ///
    /// # Errors
    ///
    /// Returns [`HostingError::Refused`] unless a host serves the seam.
    fn migration_apply(
        &self,
        _scope: &BoundScope,
        _request: &MigrationApplyRequest,
        _idempotency_key: &str,
    ) -> Result<MigrationApplyResponse, HostingError> {
        Err(HostingError::Refused("migration applies are not served by this host".into()))
    }

    /// Subscribes one backbone stream; the returned feed carries the
    /// ordered batches, the trigger notifications, and the close frame.
    ///
    /// # Errors
    ///
    /// Returns the host's typed refusal.
    fn backbone_subscribe(
        &self,
        scope: &BoundScope,
        request: &BackboneSubscribeRequest,
    ) -> Result<(BackboneSubscribeResponse, Receiver<TbepNotification>), HostingError>;

    /// Records one subscription's checkpoint acknowledgement.
    ///
    /// # Errors
    ///
    /// Returns the host's typed refusal.
    fn backbone_acknowledge(
        &self,
        scope: &BoundScope,
        request: &BackboneAcknowledgeRequest,
    ) -> Result<BackboneAcknowledgeResponse, HostingError>;

    /// Unsubscribes one backbone stream.
    ///
    /// # Errors
    ///
    /// Returns the host's typed refusal.
    fn backbone_unsubscribe(
        &self,
        scope: &BoundScope,
        request: &BackboneUnsubscribeRequest,
    ) -> Result<BackboneUnsubscribeResponse, HostingError>;

    /// Opens (or resumes) one sync session.
    ///
    /// # Errors
    ///
    /// Returns the host's typed refusal.
    fn sync_session_setup(
        &self,
        scope: &BoundScope,
        request: &SyncSessionSetupRequest,
    ) -> Result<SyncSessionSetupResponse, HostingError>;

    /// Serves one byte-budgeted delta batch from a checkpoint.
    ///
    /// # Errors
    ///
    /// Returns the host's typed refusal; a checkpoint behind the
    /// retained floor is [`HostingError::CheckpointRefused`] with the
    /// detail `behind-retained-floor` (the consumer resyncs by pulling a
    /// snapshot).
    fn sync_delta_pull(
        &self,
        scope: &BoundScope,
        request: &SyncDeltaPullRequest,
    ) -> Result<SyncDeltaPullResponse, HostingError>;

    /// Serves one full-state resync snapshot.
    ///
    /// # Errors
    ///
    /// Returns the host's typed refusal.
    fn sync_snapshot_pull(
        &self,
        scope: &BoundScope,
        request: &SyncSnapshotPullRequest,
    ) -> Result<SyncSnapshotPullResponse, HostingError>;

    /// Records one session's durable checkpoint acknowledgement.
    ///
    /// # Errors
    ///
    /// Returns the host's typed refusal.
    fn sync_checkpoint_ack(
        &self,
        scope: &BoundScope,
        request: &SyncCheckpointAckRequest,
    ) -> Result<SyncCheckpointAckResponse, HostingError>;

    /// Submits one server-ack mutation (PD-2026-08-26-12 D2: no
    /// optimistic patches; the idempotency key is the retry identity).
    ///
    /// # Errors
    ///
    /// Returns the host's typed refusal.
    fn sync_mutation_submit(
        &self,
        scope: &BoundScope,
        request: &SyncMutationSubmitRequest,
        idempotency_key: &str,
    ) -> Result<SyncMutationSubmitResponse, HostingError>;

    /// Closes one sync session.
    ///
    /// # Errors
    ///
    /// Returns the host's typed refusal.
    fn sync_session_close(
        &self,
        scope: &BoundScope,
        request: &SyncSessionCloseRequest,
    ) -> Result<SyncSessionCloseResponse, HostingError>;

    /// Serves the topology read.
    ///
    /// # Errors
    ///
    /// Returns the host's typed refusal.
    fn topology_read(&self, scope: &BoundScope) -> Result<TopologyReadResponse, HostingError>;

    /// Records one trusted, tenant-bound usage fact idempotently.
    ///
    /// # Errors
    ///
    /// Returns [`HostingError::UsageUnavailable`] until the host owns an
    /// authoritative durable usage port.
    fn usage_record(
        &self,
        scope: &BoundScope,
        request: &crate::registry::UsageRecordRequest,
        idempotency_key: &str,
    ) -> Result<crate::registry::UsageRecordResponse, HostingError> {
        let _ = (scope, request, idempotency_key);
        Err(HostingError::UsageUnavailable(
            "authoritative usage recording is unavailable on this host".into(),
        ))
    }

    /// Exports one stable page of authoritative, tenant-bound usage facts.
    ///
    /// # Errors
    ///
    /// Returns [`HostingError::UsageUnavailable`] until the host owns an
    /// authoritative durable usage port.
    fn usage_export(
        &self,
        scope: &BoundScope,
        request: &crate::registry::UsageExportRequest,
    ) -> Result<crate::registry::UsageExportResponse, HostingError> {
        let _ = (scope, request);
        Err(HostingError::UsageUnavailable(
            "authoritative usage export is unavailable on this host".into(),
        ))
    }

    /// Serves one replication conflict observation.
    ///
    /// # Errors
    ///
    /// Returns [`HostingError::Refused`] unless a host serves
    /// replication observation.
    fn replication_conflicts(
        &self,
        _scope: &BoundScope,
    ) -> Result<crate::registry::ReplicationConflictsResponse, HostingError> {
        Err(HostingError::Refused("replication.conflicts is not served by this host".into()))
    }

    /// Serves one fenced-range observation.
    ///
    /// # Errors
    ///
    /// Returns [`HostingError::Refused`] unless a host serves
    /// replication observation.
    fn replication_fenced(
        &self,
        _scope: &BoundScope,
    ) -> Result<crate::registry::ReplicationFencedResponse, HostingError> {
        Err(HostingError::Refused("replication.fenced is not served by this host".into()))
    }

    /// Serves one leadership-deposition observation.
    ///
    /// # Errors
    ///
    /// Returns [`HostingError::Refused`] unless a host serves
    /// replication observation.
    fn replication_depositions(
        &self,
        _scope: &BoundScope,
    ) -> Result<crate::registry::ReplicationDepositionsResponse, HostingError> {
        Err(HostingError::Refused("replication.depositions is not served by this host".into()))
    }

    /// Serves the replication cursor observation.
    ///
    /// # Errors
    ///
    /// Returns [`HostingError::Refused`] unless a host serves
    /// replication observation.
    fn replication_cursor(
        &self,
        _scope: &BoundScope,
    ) -> Result<crate::registry::ReplicationCursorResponse, HostingError> {
        Err(HostingError::Refused("replication.cursor is not served by this host".into()))
    }

    /// Serves the replication health observation.
    ///
    /// # Errors
    ///
    /// Returns [`HostingError::Refused`] unless a host serves
    /// replication observation.
    fn replication_health(
        &self,
        _scope: &BoundScope,
    ) -> Result<crate::registry::ReplicationHealthResponse, HostingError> {
        Err(HostingError::Refused("replication.health is not served by this host".into()))
    }

    /// Serves the cross-tenant denial observation.
    ///
    /// # Errors
    ///
    /// Returns [`HostingError::Refused`] unless a host serves
    /// replication observation.
    fn replication_denials(
        &self,
        _scope: &BoundScope,
    ) -> Result<crate::registry::ReplicationDenialsResponse, HostingError> {
        Err(HostingError::Refused("replication.denials is not served by this host".into()))
    }

    /// Configures the Storage Tier Mover's policy (P4, mutation).
    ///
    /// # Errors
    ///
    /// Returns [`HostingError::Refused`] unless a host serves tiers.
    fn tier_configure(
        &self,
        scope: &BoundScope,
        request: &crate::registry::TierConfigureRequest,
        idempotency_key: &str,
    ) -> Result<crate::registry::TierConfigureResponse, HostingError> {
        let _ = (scope, request, idempotency_key);
        Err(HostingError::Refused(
            "tier.configure is served by the engine-backed host's tier administration".into(),
        ))
    }

    /// Inspects the tier state (P4).
    ///
    /// # Errors
    ///
    /// Returns [`HostingError::Refused`] unless a host serves tiers.
    fn tier_inspect(
        &self,
        scope: &BoundScope,
    ) -> Result<crate::registry::TierInspectResponse, HostingError> {
        let _ = scope;
        Err(HostingError::Refused(
            "tier.inspect is served by the engine-backed host's tier administration".into(),
        ))
    }

    /// Explains the next mover pass as a dry receipt (P4).
    ///
    /// # Errors
    ///
    /// Returns [`HostingError::Refused`] unless a host serves tiers.
    fn tier_explain(
        &self,
        scope: &BoundScope,
    ) -> Result<crate::registry::TierExplainResponse, HostingError> {
        let _ = scope;
        Err(HostingError::Refused(
            "tier.explain is served by the engine-backed host's tier administration".into(),
        ))
    }

    /// Runs one dry mover pass (P4).
    ///
    /// # Errors
    ///
    /// Returns [`HostingError::Refused`] unless a host serves tiers.
    fn tier_dry_run(
        &self,
        scope: &BoundScope,
    ) -> Result<crate::registry::TierDryRunResponse, HostingError> {
        let _ = scope;
        Err(HostingError::Refused(
            "tier.dry_run is served by the engine-backed host's tier administration".into(),
        ))
    }

    /// Runs one bounded mover pass (P4, mutation).
    ///
    /// # Errors
    ///
    /// Returns [`HostingError::Refused`] unless a host serves tiers.
    fn tier_run(
        &self,
        scope: &BoundScope,
        idempotency_key: &str,
    ) -> Result<crate::registry::TierRunResponse, HostingError> {
        let _ = (scope, idempotency_key);
        Err(HostingError::Refused(
            "tier.run is served by the engine-backed host's tier administration".into(),
        ))
    }

    /// Pauses mover runs (P4, mutation).
    ///
    /// # Errors
    ///
    /// Returns [`HostingError::Refused`] unless a host serves tiers.
    fn tier_pause(
        &self,
        scope: &BoundScope,
        idempotency_key: &str,
    ) -> Result<crate::registry::TierPauseResponse, HostingError> {
        let _ = (scope, idempotency_key);
        Err(HostingError::Refused(
            "tier.pause is served by the engine-backed host's tier administration".into(),
        ))
    }

    /// Resumes mover runs (P4, mutation).
    ///
    /// # Errors
    ///
    /// Returns [`HostingError::Refused`] unless a host serves tiers.
    fn tier_resume(
        &self,
        scope: &BoundScope,
        idempotency_key: &str,
    ) -> Result<crate::registry::TierResumeResponse, HostingError> {
        let _ = (scope, idempotency_key);
        Err(HostingError::Refused(
            "tier.resume is served by the engine-backed host's tier administration".into(),
        ))
    }

    /// Serves the admin status read.
    ///
    /// # Errors
    ///
    /// Returns the host's typed refusal.
    fn admin_status(&self) -> Result<AdminStatusResponse, HostingError>;

    /// Serves the supervision health report.
    ///
    /// # Errors
    ///
    /// Returns the host's typed refusal.
    fn health(&self) -> Result<SupervisionHealthResponse, HostingError>;

    /// Begins a graceful shutdown; the answer carries the draining state
    /// and the in-flight count.
    ///
    /// # Errors
    ///
    /// Returns the host's typed refusal.
    fn begin_shutdown(
        &self,
        request: &SupervisionShutdownRequest,
    ) -> Result<SupervisionShutdownResponse, HostingError>;
}

/// One journal record of a scope: the backbone's change-event facts.
#[derive(Clone, Debug, PartialEq)]
struct JournalRecord {
    sequence: u64,
    write_index: u32,
    operation_id: String,
    kind: ChangeKind,
    collection: String,
    document: String,
}

/// The database identity that owns materialized delivery state. The
/// authenticated principal owns sessions and subscriptions, but two admitted
/// channels for the same database must observe the same journal-derived view.
#[derive(Clone, Debug, Eq, Ord, PartialEq, PartialOrd)]
struct DeliveryScope {
    tenant: String,
    project: String,
    database: String,
}

impl DeliveryScope {
    fn of(scope: &BoundScope) -> Self {
        Self {
            tenant: scope.tenant.clone(),
            project: scope.project.clone(),
            database: scope.database.clone(),
        }
    }
}

/// The serving state of one tenant collection scope.
struct ScopeState {
    core: SyncCore,
    journal: VecDeque<JournalRecord>,
    next_sequence: u64,
    applied_keys: BTreeMap<String, CommitReceipt>,
    /// The drop-oldest insertion order of `applied_keys` — the ring that
    /// bounds the idempotency registry at
    /// [`IDEMPOTENCY_RETRY_HORIZON`] receipts per scope.
    applied_order: VecDeque<String>,
}

impl ScopeState {
    fn record(&mut self, event: JournalRecord) {
        self.journal.push_back(event);
        while self.journal.len() > JOURNAL_BOUND {
            self.journal.pop_front();
        }
    }

    /// Records one applied key's receipt under the bounded retry
    /// horizon: the newest [`IDEMPOTENCY_RETRY_HORIZON`] receipts stay
    /// answerable for at-least-once retries; the oldest receipt beyond
    /// the horizon is evicted, so a retry of an evicted key re-applies
    /// (the documented caveat — a retry behind more than a full retained
    /// window of mutations).
    fn record_applied(&mut self, key: String, receipt: CommitReceipt) {
        self.applied_keys.insert(key.clone(), receipt);
        self.applied_order.push_back(key);
        while self.applied_order.len() > IDEMPOTENCY_RETRY_HORIZON {
            let evicted = self
                .applied_order
                .pop_front()
                .expect("the order ring tracks the registry's inserts");
            self.applied_keys.remove(&evicted);
        }
    }

    /// Serves up to `limit` events at or after `from`; the events must
    /// exist in the retained window.
    fn events_from(&self, from: u64, limit: usize) -> Vec<crate::registry::ChangeEvent> {
        self.journal
            .iter()
            .filter(|record| record.sequence >= from)
            .take(limit)
            .map(|record| crate::registry::ChangeEvent {
                sequence: record.sequence,
                operation_id: record.operation_id.clone(),
                kind: record.kind,
                collection: record.collection.clone(),
                document: record.document.clone(),
            })
            .collect()
    }
}

/// One sync session served by the host.
struct SessionState {
    collection: String,
    /// The complete channel scope that opened the session.
    scope: BoundScope,
    /// The latest checkpoint issued to this session. This is delivery state,
    /// not a replication frontier or an authorization grant.
    checkpoint: SyncCheckpoint,
    /// The greatest checkpoint acknowledged by this session.
    acknowledged: SyncCheckpoint,
    /// Checkpoints issued to this session and therefore eligible for ack or
    /// replay. The ring is bounded by the delivery window.
    issued_checkpoints: VecDeque<SyncCheckpoint>,
}

/// One backbone subscription served by the host.
struct SubscriptionState {
    collections: Option<Vec<String>>,
    next_sequence: u64,
    feed: SyncSender<TbepNotification>,
    /// The complete channel scope that subscribed.
    scope: BoundScope,
}

struct HostState {
    scopes: BTreeMap<(DeliveryScope, String), ScopeState>,
    durable_cursors: BTreeMap<DeliveryScope, ChangeCheckpoint>,
    sessions: BTreeMap<u64, SessionState>,
    subscriptions: BTreeMap<u64, SubscriptionState>,
    next_session: u64,
    frames_served: u64,
    health: HealthState,
}

/// The real sync-delivery host over the adopted sync core.
///
/// Serves the sync-delivery frame family end-to-end over
/// `terrabase-sync-core` (native deltas and snapshots), the
/// journal-backed backbone streams with content-free trigger
/// notifications, topology/admin reads, and the supervision contract.
/// The document/query/family operations answer
/// [`HostingError::Refused`] with a named reason: they belong to the
/// engine-backed host (the integrator wiring named in the SE-14 record).
pub struct SyncDeliveryHost {
    state: Mutex<HostState>,
    instance: u64,
}

impl SyncDeliveryHost {
    /// Creates a serving host with the given instance identity.
    #[must_use]
    pub fn new(instance: u64) -> Self {
        Self {
            state: Mutex::new(HostState {
                scopes: BTreeMap::new(),
                durable_cursors: BTreeMap::new(),
                sessions: BTreeMap::new(),
                subscriptions: BTreeMap::new(),
                next_session: 0,
                frames_served: 0,
                health: HealthState::Starting,
            }),
            instance,
        }
    }

    fn lock(&self) -> MutexGuard<'_, HostState> {
        self.state.lock().expect("the host mutex is not poisoned")
    }

    fn ensure_admission(state: &HostState) -> Result<(), HostingError> {
        match state.health {
            HealthState::Draining => Err(HostingError::Refused("the host is draining".into())),
            HealthState::Stopped => Err(HostingError::Refused("the host is stopped".into())),
            HealthState::Starting | HealthState::Serving => Ok(()),
        }
    }

    fn can_add_scope(
        state: &HostState,
        scope: &BoundScope,
        collection: &str,
    ) -> Result<(), HostingError> {
        if state.scopes.contains_key(&(DeliveryScope::of(scope), collection.to_owned())) {
            return Ok(());
        }
        if state.scopes.len() >= MAX_SCOPES {
            return Err(HostingError::Refused("the host scope limit is exhausted".into()));
        }
        Ok(())
    }

    fn can_add_session(state: &HostState, scope: &BoundScope) -> Result<(), HostingError> {
        if state.sessions.len() >= MAX_SESSIONS {
            return Err(HostingError::SessionRefused("the host session limit is exhausted".into()));
        }
        let count = state.sessions.values().filter(|session| &session.scope == scope).count();
        if count >= MAX_SESSIONS_PER_TENANT {
            return Err(HostingError::SessionRefused(
                "the tenant session limit is exhausted".into(),
            ));
        }
        Ok(())
    }

    fn can_add_subscription(state: &HostState, scope: &BoundScope) -> Result<(), HostingError> {
        if state.subscriptions.len() >= MAX_SUBSCRIPTIONS {
            return Err(HostingError::SubscriptionRefused(
                "the host subscription limit is exhausted".into(),
            ));
        }
        let count = state
            .subscriptions
            .values()
            .filter(|subscription| &subscription.scope == scope)
            .count();
        if count >= MAX_SUBSCRIPTIONS_PER_TENANT {
            return Err(HostingError::SubscriptionRefused(
                "the tenant subscription limit is exhausted".into(),
            ));
        }
        Ok(())
    }

    fn scope<'a>(
        scopes: &'a mut BTreeMap<(DeliveryScope, String), ScopeState>,
        bound_scope: &BoundScope,
        collection: &str,
    ) -> &'a mut ScopeState {
        scopes.entry((DeliveryScope::of(bound_scope), collection.to_string())).or_insert_with(
            || ScopeState {
                core: SyncCore::new(
                    SyncScope::new(&bound_scope.tenant, collection),
                    SyncCoreConfig {
                        batch_byte_budget: BATCH_BYTE_BUDGET,
                        checkpoint_window: CHECKPOINT_WINDOW,
                    },
                )
                .expect("the sync core constructs for valid scopes"),
                journal: VecDeque::new(),
                next_sequence: 1,
                applied_keys: BTreeMap::new(),
                applied_order: VecDeque::new(),
            },
        )
    }

    /// Looks up one session for its owning tenant: an unknown session
    /// and a session another tenant's channel opened are both refused
    /// with the typed session refusal (PD-2026-08-26-11 D1 — the engine
    /// enforces isolation between bound channels).
    fn session_of<'a>(
        state: &'a HostState,
        scope: &BoundScope,
        session_id: u64,
    ) -> Result<&'a SessionState, HostingError> {
        match state.sessions.get(&session_id) {
            Some(session) if &session.scope == scope => Ok(session),
            Some(_) => Err(HostingError::SessionRefused("session bound to another channel".into())),
            None => Err(HostingError::SessionRefused("unknown session".into())),
        }
    }

    /// Looks up one subscription for its owning tenant, the same
    /// isolation rule as [`Self::session_of`].
    fn subscription_of<'a>(
        state: &'a HostState,
        scope: &BoundScope,
        subscription_id: u64,
    ) -> Result<&'a SubscriptionState, HostingError> {
        match state.subscriptions.get(&subscription_id) {
            Some(subscription) if &subscription.scope == scope => Ok(subscription),
            Some(_) => Err(HostingError::SubscriptionRefused(
                "subscription bound to another channel".into(),
            )),
            None => Err(HostingError::SubscriptionRefused("unknown subscription".into())),
        }
    }

    fn session_collection(
        state: &HostState,
        scope: &BoundScope,
        session_id: u64,
    ) -> Result<String, HostingError> {
        Ok(Self::session_of(state, scope, session_id)?.collection.clone())
    }

    fn decode_checkpoint(bytes: &[u8]) -> Result<SyncCheckpoint, HostingError> {
        if bytes.len() > MAX_SYNC_CHECKPOINT_BYTES {
            return Err(HostingError::CheckpointRefused(
                "checkpoint exceeds the native decode bound".into(),
            ));
        }
        SyncCheckpoint::from_bytes(bytes)
            .map_err(|_| HostingError::CheckpointRefused("the checkpoint does not decode".into()))
    }

    fn checkpoint_is_issued(session: &SessionState, checkpoint: &SyncCheckpoint) -> bool {
        session.issued_checkpoints.iter().any(|issued| issued == checkpoint)
    }

    fn remember_checkpoint(session: &mut SessionState, checkpoint: SyncCheckpoint) {
        if session.issued_checkpoints.back() != Some(&checkpoint) {
            session.issued_checkpoints.push_back(checkpoint.clone());
        }
        while session.issued_checkpoints.len() > CHECKPOINT_WINDOW {
            session.issued_checkpoints.pop_front();
        }
        session.checkpoint = checkpoint;
    }

    fn checkpoint_is_monotone(previous: &SyncCheckpoint, candidate: &SyncCheckpoint) -> bool {
        candidate.storage_sequence >= previous.storage_sequence
            && candidate.end_for(terrabase_sync_core::SERVER_PEER_ID).unwrap_or(0)
                >= previous.end_for(terrabase_sync_core::SERVER_PEER_ID).unwrap_or(0)
    }

    fn durable_position_is_after(
        candidate: ChangeCheckpoint,
        current: Option<ChangeCheckpoint>,
    ) -> bool {
        current.is_none_or(|current| {
            (candidate.sequence(), candidate.write_index())
                > (current.sequence(), current.write_index())
        })
    }

    fn ordered_events(
        state: &HostState,
        scope: &DeliveryScope,
        collections: Option<&[String]>,
        from: u64,
    ) -> Vec<crate::registry::ChangeEvent> {
        let mut records: Vec<&JournalRecord> = state
            .scopes
            .iter()
            .filter(|((record_scope, collection), _)| {
                record_scope == scope
                    && collections.is_none_or(|collections| collections.contains(collection))
            })
            .flat_map(|(_, scope)| scope.journal.iter())
            .filter(|record| record.sequence >= from)
            .collect();
        records.sort_by_key(|record| (record.sequence, record.write_index));
        records.truncate(crate::registry::MAX_LIST_ITEMS);
        records
            .into_iter()
            .map(|record| crate::registry::ChangeEvent {
                sequence: record.sequence,
                operation_id: record.operation_id.clone(),
                kind: record.kind,
                collection: record.collection.clone(),
                document: record.document.clone(),
            })
            .collect()
    }

    /// Returns the last engine-journal position applied to this database's
    /// derived delivery state.
    pub(crate) fn durable_cursor(&self, scope: &BoundScope) -> Option<ChangeCheckpoint> {
        self.lock().durable_cursors.get(&DeliveryScope::of(scope)).copied()
    }

    /// Advances past journal writes that intentionally produced no sync
    /// record, such as a provable no-op delete.
    pub(crate) fn advance_durable_cursor(&self, scope: &BoundScope, checkpoint: ChangeCheckpoint) {
        let mut state = self.lock();
        let key = DeliveryScope::of(scope);
        if Self::durable_position_is_after(checkpoint, state.durable_cursors.get(&key).copied()) {
            state.durable_cursors.insert(key, checkpoint);
        }
    }

    /// Returns the collection owned by one admitted session.
    pub(crate) fn collection_for_session(
        &self,
        scope: &BoundScope,
        session_id: u64,
    ) -> Result<String, HostingError> {
        let state = self.lock();
        Self::session_collection(&state, scope, session_id)
    }

    /// Applies records pulled from the engine's durable publication journal.
    ///
    /// This is the only composition path that advances a `MemberHost` sync
    /// revision. The standalone host's mutation path remains available to its
    /// existing conformance fixtures.
    pub(crate) fn apply_durable_records(
        &self,
        bound_scope: &BoundScope,
        records: &[SyncJournalRecord],
    ) -> Result<(), HostingError> {
        let mut state = self.lock();
        Self::ensure_admission(&state)?;
        let delivery_scope = DeliveryScope::of(bound_scope);
        let mut applied = Vec::new();
        for record in records {
            if !Self::durable_position_is_after(
                record.position(),
                state.durable_cursors.get(&delivery_scope).copied(),
            ) {
                continue;
            }
            Self::can_add_scope(&state, bound_scope, record.collection().as_str())?;
            {
                let scope =
                    Self::scope(&mut state.scopes, bound_scope, record.collection().as_str());
                if record.collection().as_str() != RESERVED_FAMILY_COLLECTION {
                    let change = match record.mutation() {
                        SyncJournalMutation::Put(value) => {
                            JournalChange::Put { body: sync_body(value)? }
                        }
                        SyncJournalMutation::Delete => JournalChange::Delete,
                    };
                    let sequence = scope.next_sequence;
                    scope
                        .core
                        .apply(&JournalCommit {
                            storage_sequence: sequence,
                            operation_id: record.operation_id().as_str().to_owned(),
                            document_id: record.document().as_str().to_owned(),
                            change,
                        })
                        .map_err(|error| HostingError::EngineFailure(error.to_string()))?;
                    scope.next_sequence = scope.next_sequence.saturating_add(1);
                }
                scope.record(JournalRecord {
                    sequence: record.storage_sequence(),
                    write_index: record.position().write_index(),
                    operation_id: record.operation_id().as_str().to_owned(),
                    kind: match record.mutation() {
                        SyncJournalMutation::Put(_) => ChangeKind::Put,
                        SyncJournalMutation::Delete => ChangeKind::Delete,
                    },
                    collection: record.collection().as_str().to_owned(),
                    document: record.document().as_str().to_owned(),
                });
            }
            state.durable_cursors.insert(delivery_scope.clone(), record.position());
            applied.push(record.clone());
        }
        if !applied.is_empty() {
            Self::publish_durable_records(&mut state, bound_scope, &applied);
        }
        Ok(())
    }

    fn publish_durable_records(
        state: &mut HostState,
        bound_scope: &BoundScope,
        records: &[SyncJournalRecord],
    ) {
        let ids: Vec<u64> = state.subscriptions.keys().copied().collect();
        let mut failed = Vec::new();
        for id in ids {
            let Some(subscription) = state.subscriptions.get(&id) else {
                continue;
            };
            if DeliveryScope::of(&subscription.scope) != DeliveryScope::of(bound_scope) {
                continue;
            }
            let from = subscription.next_sequence;
            let events = Self::ordered_events(
                state,
                &DeliveryScope::of(bound_scope),
                subscription.collections.as_deref(),
                from,
            );
            let Some(subscription) = state.subscriptions.get_mut(&id) else {
                continue;
            };
            if let Some(last) = events.last() {
                let next_sequence = last.sequence.saturating_add(1);
                if subscription
                    .feed
                    .try_send(TbepNotification::Batch(SubscriptionBatch {
                        subscription_id: id,
                        from_sequence: from,
                        events,
                        next_sequence,
                    }))
                    .is_err()
                {
                    failed.push(id);
                    continue;
                }
                subscription.next_sequence = next_sequence;
            }
            for record in records {
                if let Some(collections) = &subscription.collections
                    && !collections
                        .iter()
                        .any(|collection| collection == record.collection().as_str())
                {
                    continue;
                }
                if subscription
                    .feed
                    .try_send(TbepNotification::Trigger(crate::registry::TriggerNotification {
                        subscription_id: id,
                        collection: record.collection().as_str().to_owned(),
                        sequence: record.storage_sequence(),
                        operation_id: record.operation_id().as_str().to_owned(),
                    }))
                    .is_err()
                {
                    failed.push(id);
                    break;
                }
            }
        }
        for id in failed {
            state.subscriptions.remove(&id);
        }
    }
}

fn sync_body(value: &DocumentValue) -> Result<BTreeMap<String, SyncValue>, HostingError> {
    let DocumentValue::Object(fields) = value else {
        return Err(HostingError::EngineFailure(
            "durable sync requires an object document body".into(),
        ));
    };
    fields.iter().map(|(name, value)| Ok((name.clone(), sync_value(value)?))).collect()
}

fn sync_value(value: &DocumentValue) -> Result<SyncValue, HostingError> {
    match value {
        DocumentValue::Null => Ok(SyncValue::Null),
        DocumentValue::Boolean(value) => Ok(SyncValue::Bool(*value)),
        DocumentValue::Integer(value) => Ok(SyncValue::Int(*value)),
        DocumentValue::Number(value) => Ok(SyncValue::Float(*value)),
        DocumentValue::String(value) => Ok(SyncValue::Str(value.clone())),
        DocumentValue::Array(_) | DocumentValue::Object(_) => Err(HostingError::EngineFailure(
            "durable sync cannot materialize a composite document field".into(),
        )),
    }
}

impl Default for SyncDeliveryHost {
    fn default() -> Self {
        Self::new(1)
    }
}

fn engine_ops_refusal(operation: &str) -> HostingError {
    HostingError::Refused(format!(
        "{operation} is served by the engine-backed host; the engine-side wiring is the named integrator action"
    ))
}

impl TbepHost for SyncDeliveryHost {
    fn bind_channel(
        &self,
        request: &ChannelBindRequest,
    ) -> Result<ChannelBindResponse, HostingError> {
        let mut state = self.lock();
        state.frames_served += 1;
        if state.health != HealthState::Serving && state.health != HealthState::Starting {
            return Err(HostingError::Refused("the host is draining".into()));
        }
        state.health = HealthState::Serving;
        Ok(ChannelBindResponse { scope: request.scope.clone(), serving_instance: self.instance })
    }

    fn document_read(
        &self,
        _scope: &BoundScope,
        _request: &DocumentReadRequest,
    ) -> Result<DocumentReadResponse, HostingError> {
        Err(engine_ops_refusal("document.read"))
    }

    fn document_write(
        &self,
        _scope: &BoundScope,
        _request: &DocumentWriteRequest,
        _idempotency_key: &str,
    ) -> Result<DocumentWriteResponse, HostingError> {
        Err(engine_ops_refusal("document.write"))
    }

    fn document_delete(
        &self,
        _scope: &BoundScope,
        _request: &DocumentDeleteRequest,
        _idempotency_key: &str,
    ) -> Result<DocumentDeleteResponse, HostingError> {
        Err(engine_ops_refusal("document.delete"))
    }

    fn query_execute(
        &self,
        _scope: &BoundScope,
        _request: &QueryExecuteRequest,
    ) -> Result<QueryExecuteResponse, HostingError> {
        Err(engine_ops_refusal("query.execute"))
    }

    fn query_cancel(
        &self,
        _scope: &BoundScope,
        _request: &QueryCancelRequest,
    ) -> Result<QueryCancelResponse, HostingError> {
        Err(engine_ops_refusal("query.cancel"))
    }

    fn family_read(
        &self,
        _scope: &BoundScope,
        _request: &FamilyReadRequest,
    ) -> Result<FamilyReadResponse, HostingError> {
        Err(engine_ops_refusal("family.read"))
    }

    fn family_write(
        &self,
        _scope: &BoundScope,
        _request: &FamilyWriteRequest,
        _idempotency_key: &str,
    ) -> Result<FamilyWriteResponse, HostingError> {
        Err(engine_ops_refusal("family.write"))
    }

    fn backbone_subscribe(
        &self,
        bound_scope: &BoundScope,
        request: &BackboneSubscribeRequest,
    ) -> Result<(BackboneSubscribeResponse, Receiver<TbepNotification>), HostingError> {
        let mut state = self.lock();
        state.frames_served += 1;
        Self::ensure_admission(&state)?;
        if let Some(existing) = state.subscriptions.get(&request.subscription_id) {
            if &existing.scope != bound_scope {
                return Err(HostingError::SubscriptionRefused(
                    "subscription id belongs to another channel".into(),
                ));
            }
        } else {
            Self::can_add_subscription(&state, bound_scope)?;
        }
        let (sender, receiver) = sync_channel(SUBSCRIPTION_QUEUE_BOUND);
        let from = match &request.from {
            SubscriptionStart::Live => 0,
            SubscriptionStart::Sequence(sequence) => *sequence,
        };
        // Replay the retained windows in durable write order. The write index
        // breaks ties for atomic commits that published several documents.
        let events = Self::ordered_events(
            &state,
            &DeliveryScope::of(bound_scope),
            request.collections.as_deref(),
            from,
        );
        let next_sequence = events.last().map_or(from, |event| event.sequence + 1);
        if !events.is_empty() {
            sender
                .try_send(TbepNotification::Batch(SubscriptionBatch {
                    subscription_id: request.subscription_id,
                    from_sequence: from,
                    events,
                    next_sequence,
                }))
                .map_err(|_| {
                    HostingError::SubscriptionRefused(
                        "subscription replay queue is unavailable".into(),
                    )
                })?;
        }
        if let Some(existing) = state.subscriptions.remove(&request.subscription_id) {
            let _ = existing.feed.try_send(TbepNotification::Closed(SubscriptionClosed {
                subscription_id: request.subscription_id,
                reason: SubscriptionCloseReason::Replaced,
            }));
        }
        state.subscriptions.insert(
            request.subscription_id,
            SubscriptionState {
                collections: request.collections.clone(),
                next_sequence,
                feed: sender,
                scope: bound_scope.clone(),
            },
        );
        Ok((
            BackboneSubscribeResponse {
                subscription_id: request.subscription_id,
                start_sequence: next_sequence,
            },
            receiver,
        ))
    }

    fn backbone_acknowledge(
        &self,
        bound_scope: &BoundScope,
        request: &BackboneAcknowledgeRequest,
    ) -> Result<BackboneAcknowledgeResponse, HostingError> {
        let mut state = self.lock();
        state.frames_served += 1;
        Self::ensure_admission(&state)?;
        match state.subscriptions.get_mut(&request.subscription_id) {
            Some(subscription) if &subscription.scope == bound_scope => {
                if request.through_sequence > subscription.next_sequence {
                    return Err(HostingError::SubscriptionRefused(
                        "acknowledges a sequence never served".into(),
                    ));
                }
                Ok(BackboneAcknowledgeResponse {
                    subscription_id: request.subscription_id,
                    acknowledged_through: request.through_sequence,
                })
            }
            Some(_) => Err(HostingError::SubscriptionRefused(
                "subscription bound to another channel".into(),
            )),
            None => Err(HostingError::SubscriptionRefused("unknown subscription".into())),
        }
    }

    fn backbone_unsubscribe(
        &self,
        bound_scope: &BoundScope,
        request: &BackboneUnsubscribeRequest,
    ) -> Result<BackboneUnsubscribeResponse, HostingError> {
        let mut state = self.lock();
        state.frames_served += 1;
        Self::ensure_admission(&state)?;
        // The unsubscribe removes only the owning tenant's subscription:
        // another channel's unsubscribe is refused and changes nothing.
        Self::subscription_of(&state, bound_scope, request.subscription_id)?;
        match state.subscriptions.remove(&request.subscription_id) {
            Some(subscription) => {
                let _ = subscription.feed.try_send(TbepNotification::Closed(SubscriptionClosed {
                    subscription_id: request.subscription_id,
                    reason: SubscriptionCloseReason::Replaced,
                }));
                Ok(BackboneUnsubscribeResponse { subscription_id: request.subscription_id })
            }
            None => Err(HostingError::SubscriptionRefused("unknown subscription".into())),
        }
    }

    fn sync_session_setup(
        &self,
        bound_scope: &BoundScope,
        request: &SyncSessionSetupRequest,
    ) -> Result<SyncSessionSetupResponse, HostingError> {
        let mut state = self.lock();
        state.frames_served += 1;
        Self::ensure_admission(&state)?;
        if request.collection.len() > 64 {
            return Err(HostingError::Invalid("collection exceeds the sync bound".into()));
        }
        if request.collection == RESERVED_FAMILY_COLLECTION {
            return Err(HostingError::SessionRefused(
                "the engine family collection is not a sync-session collection".into(),
            ));
        }
        Self::can_add_scope(&state, bound_scope, &request.collection)?;
        Self::can_add_session(&state, bound_scope)?;
        let resume = request.resume.as_deref().map(Self::decode_checkpoint).transpose()?;
        let (checkpoint, retained_floor) = {
            let scope = Self::scope(&mut state.scopes, bound_scope, &request.collection);
            // Validate the native peer and server position before accepting
            // a resume. The generated schema still carries a causal
            // checkpoint rather than the opaque, scope-bound view token
            // required by ADR 0024; collection binding is therefore a
            // schema/API gap reported with this integration.
            if let Some(resume) = &resume {
                match scope.core.delta_since(resume) {
                    Ok(_) => {}
                    Err(error) => {
                        return Err(HostingError::CheckpointRefused(error.to_string()));
                    }
                }
            }
            (scope.core.checkpoint().to_bytes(), scope.core.retained_floor().to_bytes())
        };
        let checkpoint = Self::decode_checkpoint(&checkpoint)?;
        state.next_session += 1;
        let session_id = state.next_session;
        let mut issued_checkpoints = VecDeque::new();
        if let Some(resume) = resume {
            issued_checkpoints.push_back(resume);
        }
        issued_checkpoints.push_back(checkpoint.clone());
        state.sessions.insert(
            session_id,
            SessionState {
                collection: request.collection.clone(),
                scope: bound_scope.clone(),
                checkpoint: checkpoint.clone(),
                acknowledged: SyncCheckpoint::initial(),
                issued_checkpoints,
            },
        );
        Ok(SyncSessionSetupResponse {
            session_id,
            checkpoint: checkpoint.to_bytes(),
            retained_floor,
        })
    }

    fn sync_delta_pull(
        &self,
        bound_scope: &BoundScope,
        request: &SyncDeltaPullRequest,
    ) -> Result<SyncDeltaPullResponse, HostingError> {
        let mut state = self.lock();
        state.frames_served += 1;
        Self::ensure_admission(&state)?;
        let (collection, from) = {
            let session = Self::session_of(&state, bound_scope, request.session_id)?;
            let from = Self::decode_checkpoint(&request.from)?;
            if !Self::checkpoint_is_issued(session, &from) {
                return Err(HostingError::CheckpointRefused(
                    "checkpoint was not issued to this session".into(),
                ));
            }
            (session.collection.clone(), from)
        };
        let response = {
            let scope = Self::scope(&mut state.scopes, bound_scope, &collection);
            match scope.core.delta_since(&from) {
                Ok(SyncBatch::UpToDate) => {
                    let next = scope.core.checkpoint();
                    Ok(SyncDeltaPullResponse {
                        session_id: request.session_id,
                        payload_codec: PayloadCodec::NativeDelta1.name().into(),
                        payload: Vec::new(),
                        next: next.to_bytes(),
                        commits: 0,
                        single_commit_oversize: false,
                    })
                }
                Ok(SyncBatch::Delta {
                    bytes,
                    next,
                    within_budget,
                    commits,
                    single_commit_oversize,
                }) => {
                    if !within_budget && !single_commit_oversize {
                        return Err(HostingError::EngineFailure(
                            "native delta exceeded its batch bound".into(),
                        ));
                    }
                    if bytes.len() > MAX_SYNC_PAYLOAD_BYTES
                        || (!bytes.is_empty() && !bytes.starts_with(NATIVE_DELTA_MAGIC))
                    {
                        return Err(HostingError::EngineFailure(
                            "native delta failed its payload bound".into(),
                        ));
                    }
                    Ok(SyncDeltaPullResponse {
                        session_id: request.session_id,
                        payload_codec: PayloadCodec::NativeDelta1.name().into(),
                        payload: bytes,
                        next: next.to_bytes(),
                        commits: u32::try_from(commits).unwrap_or(u32::MAX),
                        single_commit_oversize,
                    })
                }
                Ok(SyncBatch::BehindRetainedFloor { .. }) => Err(HostingError::CheckpointRefused(
                    "resync-required:behind-retained-floor".into(),
                )),
                Err(error) => Err(HostingError::EngineFailure(error.to_string())),
            }
        }?;
        let next = Self::decode_checkpoint(&response.next)?;
        let session =
            state.sessions.get_mut(&request.session_id).expect("session was validated above");
        Self::remember_checkpoint(session, next);
        Ok(response)
    }

    fn sync_snapshot_pull(
        &self,
        bound_scope: &BoundScope,
        request: &SyncSnapshotPullRequest,
    ) -> Result<SyncSnapshotPullResponse, HostingError> {
        let mut state = self.lock();
        state.frames_served += 1;
        Self::ensure_admission(&state)?;
        let collection = Self::session_collection(&state, bound_scope, request.session_id)?;
        let scope = Self::scope(&mut state.scopes, bound_scope, &collection);
        match scope.core.resync_snapshot() {
            Ok(snapshot) => {
                if snapshot.bytes.len() > MAX_SYNC_PAYLOAD_BYTES
                    || !snapshot.bytes.starts_with(NATIVE_SNAPSHOT_MAGIC)
                {
                    return Err(HostingError::EngineFailure(
                        "native snapshot failed its payload bound".into(),
                    ));
                }
                let checkpoint = Self::decode_checkpoint(&snapshot.checkpoint.to_bytes())?;
                let response = SyncSnapshotPullResponse {
                    session_id: request.session_id,
                    payload_codec: PayloadCodec::NativeSnapshot1.name().into(),
                    payload: snapshot.bytes,
                    checkpoint: checkpoint.to_bytes(),
                };
                let session = state
                    .sessions
                    .get_mut(&request.session_id)
                    .expect("session was validated above");
                Self::remember_checkpoint(session, checkpoint);
                Ok(response)
            }
            Err(error) => Err(HostingError::EngineFailure(error.to_string())),
        }
    }

    fn sync_checkpoint_ack(
        &self,
        bound_scope: &BoundScope,
        request: &SyncCheckpointAckRequest,
    ) -> Result<SyncCheckpointAckResponse, HostingError> {
        let mut state = self.lock();
        state.frames_served += 1;
        Self::ensure_admission(&state)?;
        // The session must belong to this tenant's channel before the
        // checkpoint is even parsed: another tenant's ack is refused
        // with the typed session refusal and changes nothing.
        let (collection, checkpoint) = {
            let session = Self::session_of(&state, bound_scope, request.session_id)?;
            let checkpoint = Self::decode_checkpoint(&request.checkpoint)?;
            if !Self::checkpoint_is_issued(session, &checkpoint) {
                return Err(HostingError::CheckpointRefused(
                    "checkpoint was not issued to this session".into(),
                ));
            }
            if !Self::checkpoint_is_monotone(&session.acknowledged, &checkpoint) {
                return Err(HostingError::CheckpointRefused(
                    "checkpoint acknowledgement moved backwards".into(),
                ));
            }
            (session.collection.clone(), checkpoint)
        };
        let scope = Self::scope(&mut state.scopes, bound_scope, &collection);
        if let Err(error) = scope.core.delta_since(&checkpoint) {
            return Err(HostingError::CheckpointRefused(error.to_string()));
        }
        let session =
            state.sessions.get_mut(&request.session_id).expect("session was validated above");
        session.acknowledged = checkpoint;
        Ok(SyncCheckpointAckResponse { session_id: request.session_id })
    }

    #[allow(
        clippy::too_many_lines,
        reason = "mutation validation, deduplication, application, and receipt recording are one atomic host path"
    )]
    fn sync_mutation_submit(
        &self,
        bound_scope: &BoundScope,
        request: &SyncMutationSubmitRequest,
        idempotency_key: &str,
    ) -> Result<SyncMutationSubmitResponse, HostingError> {
        let mut guard = self.lock();
        guard.frames_served += 1;
        Self::ensure_admission(&guard)?;
        let collection = Self::session_collection(&guard, bound_scope, request.session_id)?;
        let state: &mut HostState = &mut guard;
        let scope = Self::scope(&mut state.scopes, bound_scope, &collection);
        // At-least-once: a retried key answers the recorded receipt.
        if let Some(receipt) = scope.applied_keys.get(idempotency_key) {
            let receipt = receipt.clone();
            return Ok(mutation_applied(request.session_id, receipt));
        }
        let change = match &request.change {
            crate::registry::SyncChange::Put(fields) => {
                let mut body: BTreeMap<String, SyncValue> = BTreeMap::new();
                for field in fields {
                    // The serving core's value domain is scalar
                    // (`SyncValue` has no composite row): a composite
                    // field value is rejected with the typed
                    // `invalid_body` outcome — never silently coerced to
                    // null on an accepted write. The full recursive
                    // value type stays the wire schema's; composites
                    // belong to the document bodies of the engine-backed
                    // host.
                    let value = match &field.value {
                        TbepValue::Null => SyncValue::Null,
                        TbepValue::Boolean(v) => SyncValue::Bool(*v),
                        TbepValue::Integer(v) => SyncValue::Int(*v),
                        TbepValue::Number(v) => SyncValue::Float(*v),
                        TbepValue::String(v) => SyncValue::Str(v.clone()),
                        TbepValue::Array(_) | TbepValue::Object(_) => {
                            return Ok(SyncMutationSubmitResponse {
                                session_id: request.session_id,
                                outcome: MutationOutcome::Rejected(MutationReason::InvalidBody),
                            });
                        }
                    };
                    body.insert(field.name.clone(), value);
                }
                JournalChange::Put { body }
            }
            crate::registry::SyncChange::Delete => JournalChange::Delete,
        };
        let sequence = scope.next_sequence;
        let commit = JournalCommit {
            storage_sequence: sequence,
            operation_id: idempotency_key.to_string(),
            document_id: request.document.clone(),
            change,
        };
        if let Err(error) = scope.core.apply(&commit) {
            return Ok(mutation_rejected(request.session_id, &error));
        }
        scope.next_sequence += 1;
        let receipt = CommitReceipt {
            sequence,
            version: sequence,
            operation_id: idempotency_key.to_string(),
            // The journal's single-member commit is its regional quorum.
            acknowledgement: Some(crate::registry::Acknowledgement::RegionalQuorum),
            backup: None,
        };
        scope.record_applied(idempotency_key.to_string(), receipt.clone());
        let record = JournalRecord {
            sequence,
            write_index: 0,
            operation_id: idempotency_key.to_string(),
            kind: match &request.change {
                crate::registry::SyncChange::Put(_) => ChangeKind::Put,
                crate::registry::SyncChange::Delete => ChangeKind::Delete,
            },
            collection: collection.clone(),
            document: request.document.clone(),
        };
        let trigger = crate::registry::TriggerNotification {
            subscription_id: 0, // filled per subscription below
            collection: record.collection.clone(),
            sequence: record.sequence,
            operation_id: record.operation_id.clone(),
        };
        scope.record(record);
        // Push the hint (trigger) and the delta (batch) to every matching
        // subscription: push the hint, pull the delta (PD-2026-08-26-13).
        let subscriptions: Vec<u64> = state.subscriptions.keys().copied().collect();
        let mut failed_subscriptions = Vec::new();
        for id in subscriptions {
            let Some(subscription) = state.subscriptions.get_mut(&id) else {
                continue;
            };
            if &subscription.scope != bound_scope {
                continue;
            }
            if let Some(collections) = &subscription.collections
                && !collections.contains(&collection)
            {
                continue;
            }
            let from = subscription.next_sequence;
            let events = scope.events_from(from, crate::registry::MAX_LIST_ITEMS);
            if let Some(last) = events.last() {
                let next_sequence = last.sequence + 1;
                let batch = TbepNotification::Batch(SubscriptionBatch {
                    subscription_id: id,
                    from_sequence: from,
                    events,
                    next_sequence,
                });
                if subscription.feed.try_send(batch).is_err() {
                    failed_subscriptions.push(id);
                    continue;
                }
                subscription.next_sequence = next_sequence;
            }
            if subscription
                .feed
                .try_send(TbepNotification::Trigger(crate::registry::TriggerNotification {
                    subscription_id: id,
                    ..trigger.clone()
                }))
                .is_err()
            {
                failed_subscriptions.push(id);
            }
        }
        for id in failed_subscriptions {
            state.subscriptions.remove(&id);
        }
        Ok(mutation_applied(request.session_id, receipt))
    }

    fn sync_session_close(
        &self,
        bound_scope: &BoundScope,
        request: &SyncSessionCloseRequest,
    ) -> Result<SyncSessionCloseResponse, HostingError> {
        let mut state = self.lock();
        state.frames_served += 1;
        Self::ensure_admission(&state)?;
        // The close removes only the owning tenant's session: another
        // channel's close is refused and changes nothing.
        Self::session_of(&state, bound_scope, request.session_id)?;
        match state.sessions.remove(&request.session_id) {
            Some(_) => Ok(SyncSessionCloseResponse { session_id: request.session_id }),
            None => Err(HostingError::SessionRefused("unknown session".into())),
        }
    }

    fn topology_read(&self, _scope: &BoundScope) -> Result<TopologyReadResponse, HostingError> {
        Ok(TopologyReadResponse {
            binding: crate::registry::BindingKind::LocalUds,
            subsystems: vec!["sync-delivery".into(), "backbone".into(), "supervision".into()],
            engine_topology_available: false,
            cluster_mode: Some(crate::registry::ClusterMode::Standalone),
        })
    }

    fn admin_status(&self) -> Result<AdminStatusResponse, HostingError> {
        let state = self.lock();
        Ok(AdminStatusResponse {
            registry_version: REGISTRY_VERSION.into(),
            frames_served: state.frames_served,
            sessions: u32::try_from(state.sessions.len()).unwrap_or(u32::MAX),
            subscriptions: u32::try_from(state.subscriptions.len()).unwrap_or(u32::MAX),
            scopes: u32::try_from(state.scopes.len()).unwrap_or(u32::MAX),
        })
    }

    fn health(&self) -> Result<SupervisionHealthResponse, HostingError> {
        let state = self.lock();
        Ok(SupervisionHealthResponse {
            state: state.health,
            sessions: u32::try_from(state.sessions.len()).unwrap_or(u32::MAX),
            subscriptions: u32::try_from(state.subscriptions.len()).unwrap_or(u32::MAX),
            frames_served: state.frames_served,
        })
    }

    fn begin_shutdown(
        &self,
        _request: &SupervisionShutdownRequest,
    ) -> Result<SupervisionShutdownResponse, HostingError> {
        let mut state = self.lock();
        let in_flight =
            u32::try_from(state.sessions.len() + state.subscriptions.len()).unwrap_or(u32::MAX);
        state.health = HealthState::Draining;
        let subscriptions: Vec<u64> = state.subscriptions.keys().copied().collect();
        for id in subscriptions {
            if let Some(subscription) = state.subscriptions.remove(&id) {
                let _ = subscription.feed.try_send(TbepNotification::Closed(SubscriptionClosed {
                    subscription_id: id,
                    reason: SubscriptionCloseReason::ShuttingDown,
                }));
            }
        }
        state.sessions.clear();
        Ok(SupervisionShutdownResponse { state: state.health, in_flight })
    }
}

fn mutation_applied(session_id: u64, receipt: CommitReceipt) -> SyncMutationSubmitResponse {
    SyncMutationSubmitResponse { session_id, outcome: MutationOutcome::Applied(receipt) }
}

fn mutation_rejected(session_id: u64, error: &SyncCoreError) -> SyncMutationSubmitResponse {
    let reason = match error {
        SyncCoreError::SequenceOutOfOrder { .. } => MutationReason::SequenceOutOfOrder,
        SyncCoreError::InvalidFrame(_) => MutationReason::InvalidBody,
        _ => MutationReason::ScopeRefused,
    };
    SyncMutationSubmitResponse { session_id, outcome: MutationOutcome::Rejected(reason) }
}

// ---------------------------------------------------------------------------
// The conformance fixture host
// ---------------------------------------------------------------------------

/// One scripted outcome of the engine-side operation families.
#[derive(Clone, Debug)]
pub enum ScriptedOutcome {
    /// Answer with a document read response.
    DocumentRead(DocumentReadResponse),
    /// Answer with a document write response.
    DocumentWrite(DocumentWriteResponse),
    /// Answer with a query response.
    Query(QueryExecuteResponse),
    /// Answer with a query-cancel response.
    QueryCancel(QueryCancelResponse),
    /// Answer with a family response.
    Family(FamilyReadResponse),
    /// Refuse with the typed error.
    Refuse(HostingError),
}

/// The conformance fixture: a scripted host over the real
/// [`SyncDeliveryHost`].
///
/// Sync-delivery, backbone, topology, admin, and supervision requests
/// serve the real host; the document/query/family operations answer the
/// queued script (deterministic outcomes for the golden vectors and the
/// adversarial suite) or a typed refusal when the queue is empty. This
/// is a test fixture, not an engine implementation: it stores nothing
/// and invents no database behavior.
pub struct ScriptedHost {
    real: SyncDeliveryHost,
    script: Mutex<VecDeque<ScriptedOutcome>>,
}

impl ScriptedHost {
    /// Creates the fixture over one real host with a script queue.
    #[must_use]
    pub fn new(real: SyncDeliveryHost, script: Vec<ScriptedOutcome>) -> Self {
        Self { real, script: Mutex::new(script.into()) }
    }

    fn next(&self) -> ScriptedOutcome {
        self.script.lock().expect("the script mutex is not poisoned").pop_front().unwrap_or(
            ScriptedOutcome::Refuse(HostingError::Refused(
                "the conformance script is exhausted".into(),
            )),
        )
    }
}

impl TbepHost for ScriptedHost {
    fn bind_channel(
        &self,
        request: &ChannelBindRequest,
    ) -> Result<ChannelBindResponse, HostingError> {
        self.real.bind_channel(request)
    }

    fn document_read(
        &self,
        _scope: &BoundScope,
        _request: &DocumentReadRequest,
    ) -> Result<DocumentReadResponse, HostingError> {
        match self.next() {
            ScriptedOutcome::DocumentRead(response) => Ok(response),
            ScriptedOutcome::Refuse(error) => Err(error),
            _ => Err(HostingError::Refused("script mismatch: document.read".into())),
        }
    }

    fn document_write(
        &self,
        _scope: &BoundScope,
        _request: &DocumentWriteRequest,
        _idempotency_key: &str,
    ) -> Result<DocumentWriteResponse, HostingError> {
        match self.next() {
            ScriptedOutcome::DocumentWrite(response) => Ok(response),
            ScriptedOutcome::Refuse(error) => Err(error),
            _ => Err(HostingError::Refused("script mismatch: document.write".into())),
        }
    }

    fn document_delete(
        &self,
        _scope: &BoundScope,
        _request: &DocumentDeleteRequest,
        _idempotency_key: &str,
    ) -> Result<DocumentDeleteResponse, HostingError> {
        match self.next() {
            ScriptedOutcome::DocumentWrite(response) => {
                Ok(DocumentDeleteResponse { outcome: response.outcome })
            }
            ScriptedOutcome::Refuse(error) => Err(error),
            _ => Err(HostingError::Refused("script mismatch: document.delete".into())),
        }
    }

    fn query_execute(
        &self,
        _scope: &BoundScope,
        _request: &QueryExecuteRequest,
    ) -> Result<QueryExecuteResponse, HostingError> {
        match self.next() {
            ScriptedOutcome::Query(response) => Ok(response),
            ScriptedOutcome::Refuse(error) => Err(error),
            _ => Err(HostingError::Refused("script mismatch: query.execute".into())),
        }
    }

    fn query_cancel(
        &self,
        _scope: &BoundScope,
        _request: &QueryCancelRequest,
    ) -> Result<QueryCancelResponse, HostingError> {
        match self.next() {
            ScriptedOutcome::QueryCancel(response) => Ok(response),
            ScriptedOutcome::Refuse(error) => Err(error),
            _ => Err(HostingError::Refused("script mismatch: query.cancel".into())),
        }
    }

    fn family_read(
        &self,
        _scope: &BoundScope,
        _request: &FamilyReadRequest,
    ) -> Result<FamilyReadResponse, HostingError> {
        match self.next() {
            ScriptedOutcome::Family(response) => Ok(response),
            ScriptedOutcome::Refuse(error) => Err(error),
            _ => Err(HostingError::Refused("script mismatch: family.read".into())),
        }
    }

    fn family_write(
        &self,
        _scope: &BoundScope,
        _request: &FamilyWriteRequest,
        _idempotency_key: &str,
    ) -> Result<FamilyWriteResponse, HostingError> {
        match self.next() {
            ScriptedOutcome::Family(response) => Ok(FamilyWriteResponse {
                response_codec: response.response_codec,
                response: response.response,
            }),
            ScriptedOutcome::Refuse(error) => Err(error),
            _ => Err(HostingError::Refused("script mismatch: family.write".into())),
        }
    }

    fn backbone_subscribe(
        &self,
        scope: &BoundScope,
        request: &BackboneSubscribeRequest,
    ) -> Result<(BackboneSubscribeResponse, Receiver<TbepNotification>), HostingError> {
        self.real.backbone_subscribe(scope, request)
    }

    fn backbone_acknowledge(
        &self,
        scope: &BoundScope,
        request: &BackboneAcknowledgeRequest,
    ) -> Result<BackboneAcknowledgeResponse, HostingError> {
        self.real.backbone_acknowledge(scope, request)
    }

    fn backbone_unsubscribe(
        &self,
        scope: &BoundScope,
        request: &BackboneUnsubscribeRequest,
    ) -> Result<BackboneUnsubscribeResponse, HostingError> {
        self.real.backbone_unsubscribe(scope, request)
    }

    fn sync_session_setup(
        &self,
        scope: &BoundScope,
        request: &SyncSessionSetupRequest,
    ) -> Result<SyncSessionSetupResponse, HostingError> {
        self.real.sync_session_setup(scope, request)
    }

    fn sync_delta_pull(
        &self,
        scope: &BoundScope,
        request: &SyncDeltaPullRequest,
    ) -> Result<SyncDeltaPullResponse, HostingError> {
        self.real.sync_delta_pull(scope, request)
    }

    fn sync_snapshot_pull(
        &self,
        scope: &BoundScope,
        request: &SyncSnapshotPullRequest,
    ) -> Result<SyncSnapshotPullResponse, HostingError> {
        self.real.sync_snapshot_pull(scope, request)
    }

    fn sync_checkpoint_ack(
        &self,
        scope: &BoundScope,
        request: &SyncCheckpointAckRequest,
    ) -> Result<SyncCheckpointAckResponse, HostingError> {
        self.real.sync_checkpoint_ack(scope, request)
    }

    fn sync_mutation_submit(
        &self,
        scope: &BoundScope,
        request: &SyncMutationSubmitRequest,
        idempotency_key: &str,
    ) -> Result<SyncMutationSubmitResponse, HostingError> {
        self.real.sync_mutation_submit(scope, request, idempotency_key)
    }

    fn sync_session_close(
        &self,
        scope: &BoundScope,
        request: &SyncSessionCloseRequest,
    ) -> Result<SyncSessionCloseResponse, HostingError> {
        self.real.sync_session_close(scope, request)
    }

    fn topology_read(&self, scope: &BoundScope) -> Result<TopologyReadResponse, HostingError> {
        self.real.topology_read(scope)
    }

    fn admin_status(&self) -> Result<AdminStatusResponse, HostingError> {
        self.real.admin_status()
    }

    fn health(&self) -> Result<SupervisionHealthResponse, HostingError> {
        self.real.health()
    }

    fn begin_shutdown(
        &self,
        request: &SupervisionShutdownRequest,
    ) -> Result<SupervisionShutdownResponse, HostingError> {
        self.real.begin_shutdown(request)
    }
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

#[cfg(test)]
mod tests {
    use super::*;
    use crate::registry::SyncChange;

    fn scope(tenant: &str) -> BoundScope {
        scope_with_database(tenant, "database-a")
    }

    fn scope_with_database(tenant: &str, database: &str) -> BoundScope {
        BoundScope {
            principal: "operator".into(),
            tenant: tenant.into(),
            project: "project-a".into(),
            database: database.into(),
        }
    }

    fn submit(
        host: &SyncDeliveryHost,
        scope: &BoundScope,
        session: u64,
        document: &str,
        key: &str,
    ) -> CommitReceipt {
        let response = host
            .sync_mutation_submit(
                scope,
                &SyncMutationSubmitRequest {
                    session_id: session,
                    document: document.into(),
                    change: SyncChange::Put(vec![crate::registry::SyncField {
                        name: "f".into(),
                        value: TbepValue::Integer(1),
                    }]),
                    usage: None,
                },
                key,
            )
            .expect("submits");
        match response.outcome {
            MutationOutcome::Applied(receipt) => receipt,
            MutationOutcome::Rejected(reason) => panic!("rejected: {reason:?}"),
        }
    }

    #[test]
    fn sync_delivery_serves_deltas_snapshots_and_idempotent_mutations() {
        let host = SyncDeliveryHost::new(7);
        let scope = scope("tenant-a");
        let setup = host
            .sync_session_setup(
                &scope,
                &SyncSessionSetupRequest { collection: "orders".into(), resume: None },
            )
            .expect("setup");
        assert_eq!(setup.session_id, 1);

        let first = submit(&host, &scope, 1, "d-01", "op-01");
        assert_eq!(first.sequence, 1);
        // A retry with the same key answers the recorded receipt.
        let retry = submit(&host, &scope, 1, "d-01", "op-01");
        assert_eq!(retry, first);
        let second = submit(&host, &scope, 1, "d-02", "op-02");
        assert_eq!(second.sequence, 2);

        let initial: SyncCheckpoint =
            SyncCheckpoint::from_bytes(&setup.checkpoint).expect("decodes");
        let delta = host
            .sync_delta_pull(
                &scope,
                &SyncDeltaPullRequest { session_id: 1, from: initial.to_bytes() },
            )
            .expect("delta");
        assert_eq!(delta.payload_codec, "terrabase-native/1");
        assert!(!delta.payload.is_empty());
        assert_eq!(delta.commits, 2);

        let again = host
            .sync_delta_pull(
                &scope,
                &SyncDeltaPullRequest { session_id: 1, from: delta.next.clone() },
            )
            .expect("up to date");
        assert!(again.payload.is_empty());
        assert_eq!(again.commits, 0);

        let snapshot = host
            .sync_snapshot_pull(&scope, &SyncSnapshotPullRequest { session_id: 1 })
            .expect("snapshot");
        assert_eq!(snapshot.payload_codec, "terrabase-snapshot/1");
        assert!(!snapshot.payload.is_empty());
    }

    #[test]
    fn subscriptions_receive_batches_and_content_free_triggers() {
        let host = SyncDeliveryHost::new(1);
        let scope_a = scope("tenant-a");
        let scope_b = scope("tenant-b");
        let setup = host
            .sync_session_setup(
                &scope_a,
                &SyncSessionSetupRequest { collection: "orders".into(), resume: None },
            )
            .expect("setup");
        let (subscribe, feed) = host
            .backbone_subscribe(
                &scope_a,
                &BackboneSubscribeRequest {
                    subscription_id: 42,
                    collections: Some(vec!["orders".into()]),
                    from: SubscriptionStart::Live,
                },
            )
            .expect("subscribes");
        assert_eq!(subscribe.subscription_id, 42);
        let (_, tenant_b_feed) = host
            .backbone_subscribe(
                &scope_b,
                &BackboneSubscribeRequest {
                    subscription_id: 43,
                    collections: Some(vec!["orders".into()]),
                    from: SubscriptionStart::Live,
                },
            )
            .expect("tenant-b subscribes");

        let receipt = submit(&host, &scope_a, setup.session_id, "d-01", "op-01");
        let batch = feed.recv_timeout(std::time::Duration::from_secs(5)).expect("batch");
        match &batch {
            TbepNotification::Batch(batch) => {
                assert_eq!(batch.subscription_id, 42);
                assert_eq!(batch.events.len(), 1);
                assert_eq!(batch.events[0].sequence, receipt.sequence);
            }
            other => panic!("expected a batch, got {other:?}"),
        }
        let trigger = feed.recv_timeout(std::time::Duration::from_secs(5)).expect("trigger");
        match &trigger {
            TbepNotification::Trigger(trigger) => {
                assert_eq!(trigger.collection, "orders");
                assert_eq!(trigger.sequence, receipt.sequence);
                assert_eq!(trigger.operation_id, "op-01");
            }
            other => panic!("expected a trigger, got {other:?}"),
        }
        assert!(tenant_b_feed.recv_timeout(std::time::Duration::from_millis(20)).is_err());
    }

    #[test]
    fn engine_operations_refuse_with_a_named_reason() {
        let host = SyncDeliveryHost::default();
        let scope = scope("tenant-a");
        let error = host
            .document_read(
                &scope,
                &DocumentReadRequest {
                    collection: "orders".into(),
                    document: "d".into(),
                    level: None,
                },
            )
            .unwrap_err();
        assert!(
            matches!(error, HostingError::Refused(ref detail) if detail.contains("engine-backed host"))
        );
    }

    #[test]
    fn supervision_health_and_graceful_shutdown() {
        let host = SyncDeliveryHost::default();
        let health = host.health().expect("health");
        assert_eq!(health.state, HealthState::Starting);
        host.bind_channel(&ChannelBindRequest { scope: scope("tenant-a") }).expect("binds");
        let health = host.health().expect("health");
        assert_eq!(health.state, HealthState::Serving);
        let shutdown = host
            .begin_shutdown(&SupervisionShutdownRequest {
                reason: crate::registry::ShutdownReason::Operator,
            })
            .expect("shutdown");
        assert_eq!(shutdown.state, HealthState::Draining);
        assert_eq!(shutdown.in_flight, 0);
    }

    fn outcome_of(
        host: &SyncDeliveryHost,
        scope: &BoundScope,
        session: u64,
        key: &str,
        fields: Vec<crate::registry::SyncField>,
    ) -> MutationOutcome {
        host.sync_mutation_submit(
            scope,
            &SyncMutationSubmitRequest {
                session_id: session,
                document: "d-01".into(),
                change: SyncChange::Put(fields),
                usage: None,
            },
            key,
        )
        .expect("submits")
        .outcome
    }

    #[test]
    fn composite_field_values_are_rejected_not_coerced() {
        // The serving core's value domain is scalar: a composite field
        // value (array or object) must be rejected with the typed
        // invalid_body outcome, never silently coerced to null on an
        // accepted write.
        let host = SyncDeliveryHost::new(1);
        let scope = scope("tenant-a");
        let setup = host
            .sync_session_setup(
                &scope,
                &SyncSessionSetupRequest { collection: "orders".into(), resume: None },
            )
            .expect("setup");
        let session = setup.session_id;

        let array = outcome_of(
            &host,
            &scope,
            session,
            "op-array",
            vec![crate::registry::SyncField {
                name: "tags".into(),
                value: TbepValue::Array(vec![TbepValue::String("x".into())]),
            }],
        );
        assert_eq!(array, MutationOutcome::Rejected(MutationReason::InvalidBody));

        let object = outcome_of(
            &host,
            &scope,
            session,
            "op-object",
            vec![crate::registry::SyncField {
                name: "nested".into(),
                value: TbepValue::Object(vec![("a".into(), TbepValue::Integer(1))]),
            }],
        );
        assert_eq!(object, MutationOutcome::Rejected(MutationReason::InvalidBody));

        // A rejection is not an application: the same idempotency key
        // stays retryable and applies once the body is scalar.
        let applied = outcome_of(
            &host,
            &scope,
            session,
            "op-array",
            vec![crate::registry::SyncField {
                name: "tags".into(),
                value: TbepValue::String("x".into()),
            }],
        );
        match applied {
            MutationOutcome::Applied(_) => {}
            MutationOutcome::Rejected(reason) => panic!("the scalar retry applied: {reason:?}"),
        }
    }

    #[test]
    fn the_idempotency_registry_is_bounded_with_a_drop_oldest_horizon() {
        // The applied-key registry is bounded (serving memory): it
        // retains the most recent IDEMPOTENCY_RETRY_HORIZON receipts of
        // each scope, drop-oldest. Inside the horizon a retried key
        // answers its recorded receipt; beyond it the documented caveat
        // applies — the retry re-applies.
        let host = SyncDeliveryHost::new(1);
        let scope = scope("tenant-a");
        let setup = host
            .sync_session_setup(
                &scope,
                &SyncSessionSetupRequest { collection: "orders".into(), resume: None },
            )
            .expect("setup");
        let session = setup.session_id;

        let first = match outcome_of(
            &host,
            &scope,
            session,
            "op-00",
            vec![crate::registry::SyncField { name: "f".into(), value: TbepValue::Integer(0) }],
        ) {
            MutationOutcome::Applied(receipt) => receipt,
            MutationOutcome::Rejected(reason) => panic!("applies: {reason:?}"),
        };
        for index in 1..IDEMPOTENCY_RETRY_HORIZON {
            match outcome_of(
                &host,
                &scope,
                session,
                &format!("op-{index:02}"),
                vec![crate::registry::SyncField {
                    name: "f".into(),
                    value: TbepValue::Integer(i64::try_from(index).expect("fits")),
                }],
            ) {
                MutationOutcome::Applied(_) => {}
                MutationOutcome::Rejected(reason) => panic!("applies: {reason:?}"),
            }
        }

        // The newest key is inside the horizon: its retry answers the
        // recorded receipt.
        let newest = format!("op-{:02}", IDEMPOTENCY_RETRY_HORIZON - 1);
        match outcome_of(
            &host,
            &scope,
            session,
            &newest,
            vec![crate::registry::SyncField { name: "f".into(), value: TbepValue::Integer(1) }],
        ) {
            MutationOutcome::Applied(receipt) => assert_eq!(receipt.operation_id, newest),
            MutationOutcome::Rejected(reason) => {
                panic!("the in-horizon retry answered: {reason:?}")
            }
        }

        // One more application evicts the oldest key: the op-00 retry
        // re-applies with a fresh sequence (the documented at-least-once
        // caveat beyond the horizon).
        match outcome_of(
            &host,
            &scope,
            session,
            "op-evict",
            vec![crate::registry::SyncField { name: "f".into(), value: TbepValue::Integer(1) }],
        ) {
            MutationOutcome::Applied(_) => {}
            MutationOutcome::Rejected(reason) => panic!("applies: {reason:?}"),
        }
        match outcome_of(
            &host,
            &scope,
            session,
            "op-00",
            vec![crate::registry::SyncField { name: "f".into(), value: TbepValue::Integer(0) }],
        ) {
            MutationOutcome::Applied(receipt) => assert!(
                receipt.sequence > first.sequence,
                "the beyond-horizon retry re-applied, not answered from the registry"
            ),
            MutationOutcome::Rejected(reason) => panic!("re-applies: {reason:?}"),
        }
    }

    #[test]
    fn sessions_and_subscriptions_remember_their_complete_bound_scope() {
        // The complete database scope owns each session and subscription.
        // Sharing a tenant is not enough to cross that boundary.
        let host = SyncDeliveryHost::new(1);
        let owner = scope("tenant-a");
        let other_tenant = scope("tenant-b");
        let other_database = scope_with_database("tenant-a", "database-b");
        let setup = host
            .sync_session_setup(
                &owner,
                &SyncSessionSetupRequest { collection: "orders".into(), resume: None },
            )
            .expect("setup");
        let session = setup.session_id;
        let checkpoint = SyncCheckpoint::from_bytes(&setup.checkpoint).expect("decodes");
        host.backbone_subscribe(
            &owner,
            &BackboneSubscribeRequest {
                subscription_id: 42,
                collections: Some(vec!["orders".into()]),
                from: SubscriptionStart::Live,
            },
        )
        .expect("subscribes");

        // A channel for the same tenant but another Database cannot use the
        // owner's lifecycle resources.
        let error = host
            .sync_checkpoint_ack(
                &other_database,
                &SyncCheckpointAckRequest {
                    session_id: session,
                    checkpoint: checkpoint.to_bytes(),
                },
            )
            .unwrap_err();
        assert!(matches!(error, HostingError::SessionRefused(_)), "{error:?}");
        let error = host
            .sync_session_close(&other_database, &SyncSessionCloseRequest { session_id: session })
            .unwrap_err();
        assert!(matches!(error, HostingError::SessionRefused(_)), "{error:?}");
        let error = host
            .backbone_acknowledge(
                &other_database,
                &BackboneAcknowledgeRequest { subscription_id: 42, through_sequence: 0 },
            )
            .unwrap_err();
        assert!(matches!(error, HostingError::SubscriptionRefused(_)), "{error:?}");
        let error = host
            .backbone_unsubscribe(
                &other_database,
                &BackboneUnsubscribeRequest { subscription_id: 42 },
            )
            .unwrap_err();
        assert!(matches!(error, HostingError::SubscriptionRefused(_)), "{error:?}");

        let error = host
            .sync_session_close(&other_tenant, &SyncSessionCloseRequest { session_id: session })
            .unwrap_err();
        assert!(matches!(error, HostingError::SessionRefused(_)), "{error:?}");

        // The owner's channel still owns the lifecycle end to end.
        host.sync_checkpoint_ack(
            &owner,
            &SyncCheckpointAckRequest { session_id: session, checkpoint: checkpoint.to_bytes() },
        )
        .expect("acks");
        host.backbone_unsubscribe(&owner, &BackboneUnsubscribeRequest { subscription_id: 42 })
            .expect("unsubscribes");
        host.sync_session_close(&owner, &SyncSessionCloseRequest { session_id: session })
            .expect("closes");
    }

    #[test]
    fn duplicate_subscription_ids_are_scope_owned_and_same_scope_replaces() {
        let host = SyncDeliveryHost::new(1);
        let owner = scope("tenant-a");
        let other_database = scope_with_database("tenant-a", "database-b");
        let request = BackboneSubscribeRequest {
            subscription_id: 9,
            collections: None,
            from: SubscriptionStart::Live,
        };
        let (_, old_feed) = host.backbone_subscribe(&owner, &request).expect("subscribes");

        let error = host.backbone_subscribe(&other_database, &request).unwrap_err();
        assert!(matches!(error, HostingError::SubscriptionRefused(_)), "{error:?}");
        assert_eq!(host.admin_status().expect("status").subscriptions, 1);

        let (_, new_feed) = host.backbone_subscribe(&owner, &request).expect("replaces");
        assert_eq!(
            old_feed.recv().expect("replacement notice"),
            TbepNotification::Closed(SubscriptionClosed {
                subscription_id: 9,
                reason: SubscriptionCloseReason::Replaced,
            })
        );
        assert_eq!(host.admin_status().expect("status").subscriptions, 1);
        drop(new_feed);
    }

    #[test]
    fn dropped_or_slow_subscription_is_removed_without_blocking_mutations() {
        let host = SyncDeliveryHost::new(1);
        let scope = scope("tenant-a");
        let setup = host
            .sync_session_setup(
                &scope,
                &SyncSessionSetupRequest { collection: "orders".into(), resume: None },
            )
            .expect("setup");
        let (_, feed) = host
            .backbone_subscribe(
                &scope,
                &BackboneSubscribeRequest {
                    subscription_id: 1,
                    collections: Some(vec!["orders".into()]),
                    from: SubscriptionStart::Live,
                },
            )
            .expect("subscribes");
        drop(feed);
        submit(&host, &scope, setup.session_id, "dropped", "dropped");
        assert_eq!(host.admin_status().expect("status").subscriptions, 0);

        let (_, _slow_feed) = host
            .backbone_subscribe(
                &scope,
                &BackboneSubscribeRequest {
                    subscription_id: 2,
                    collections: Some(vec!["orders".into()]),
                    from: SubscriptionStart::Live,
                },
            )
            .expect("subscribes");
        for index in 0..(SUBSCRIPTION_QUEUE_BOUND / 2 + 2) {
            submit(
                &host,
                &scope,
                setup.session_id,
                &format!("slow-{index}"),
                &format!("slow-{index}"),
            );
        }
        assert_eq!(host.admin_status().expect("status").subscriptions, 0);
    }

    #[test]
    fn shutdown_drains_resources_and_rejects_data_operations() {
        let host = SyncDeliveryHost::new(1);
        let scope = scope("tenant-a");
        let setup = host
            .sync_session_setup(
                &scope,
                &SyncSessionSetupRequest { collection: "orders".into(), resume: None },
            )
            .expect("setup");
        let _ = host
            .backbone_subscribe(
                &scope,
                &BackboneSubscribeRequest {
                    subscription_id: 3,
                    collections: None,
                    from: SubscriptionStart::Live,
                },
            )
            .expect("subscribes");
        let shutdown = host
            .begin_shutdown(&SupervisionShutdownRequest {
                reason: crate::registry::ShutdownReason::Operator,
            })
            .expect("shutdown");
        assert_eq!(shutdown.state, HealthState::Draining);
        assert_eq!(host.health().expect("health").state, HealthState::Draining);
        assert_eq!(host.admin_status().expect("status").sessions, 0);
        assert_eq!(host.admin_status().expect("status").subscriptions, 0);
        let error = host
            .sync_mutation_submit(
                &scope,
                &SyncMutationSubmitRequest {
                    session_id: setup.session_id,
                    document: "d".into(),
                    change: SyncChange::Delete,
                    usage: None,
                },
                "after-shutdown",
            )
            .unwrap_err();
        assert!(matches!(error, HostingError::Refused(_)), "{error:?}");
    }
}
