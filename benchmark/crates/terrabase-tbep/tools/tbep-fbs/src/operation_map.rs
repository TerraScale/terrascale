//! The operation-discriminant to body-union member map.
//!
//! The schema's `operation` field is the wire authority (explicit
//! registry frame codes) and the `body` union is structural (flatc
//! assigns its tags by declaration order); this table is the one place
//! that binds them, so the receiver can refuse an envelope whose body
//! shape does not match its discriminant. The table is total: every
//! registered operation code maps to exactly one union member, and an
//! unmapped code is refused as `UnknownOperation` before body access.

use crate::tbep_generated::terrabase::tbep::{TbepFrame, TbepOperation};

/// The union member one operation code must ride in, with its
/// declaration name for diagnostics. `None` marks an unknown code.
#[must_use]
pub fn body_kind_for(operation: TbepOperation) -> Option<TbepFrame> {
    let member = match operation {
        // Channel frames.
        TbepOperation::ERROR => TbepFrame::ErrorFrame,
        TbepOperation::NEGOTIATION_REQUEST => TbepFrame::NegotiationRequestFrame,
        TbepOperation::NEGOTIATION_RESPONSE => TbepFrame::NegotiationResponseFrame,
        // Supervision.
        TbepOperation::CHANNEL_BIND => TbepFrame::ChannelBindRequest,
        TbepOperation::CHANNEL_BIND_RESPONSE => TbepFrame::ChannelBindResponse,
        TbepOperation::SUPERVISION_HEALTH => TbepFrame::SupervisionHealthRequest,
        TbepOperation::SUPERVISION_HEALTH_RESPONSE => {
            TbepFrame::SupervisionHealthResponse
        }
        TbepOperation::SUPERVISION_SHUTDOWN => TbepFrame::SupervisionShutdownRequest,
        TbepOperation::SUPERVISION_SHUTDOWN_RESPONSE => {
            TbepFrame::SupervisionShutdownResponse
        }
        // Documents.
        TbepOperation::DOCUMENT_READ => TbepFrame::DocumentReadRequest,
        TbepOperation::DOCUMENT_READ_RESPONSE => TbepFrame::DocumentReadResponse,
        TbepOperation::DOCUMENT_WRITE => TbepFrame::DocumentWriteRequest,
        TbepOperation::DOCUMENT_WRITE_RESPONSE => TbepFrame::DocumentWriteResponse,
        TbepOperation::DOCUMENT_DELETE => TbepFrame::DocumentDeleteRequest,
        TbepOperation::DOCUMENT_DELETE_RESPONSE => TbepFrame::DocumentDeleteResponse,
        TbepOperation::DOCUMENT_BATCH => TbepFrame::DocumentBatchRequest,
        TbepOperation::DOCUMENT_BATCH_RESPONSE => TbepFrame::DocumentBatchResponse,
        // Query.
        TbepOperation::QUERY_EXECUTE => TbepFrame::QueryExecuteRequest,
        TbepOperation::QUERY_EXECUTE_RESPONSE => TbepFrame::QueryExecuteResponse,
        TbepOperation::QUERY_CANCEL => TbepFrame::QueryCancelRequest,
        TbepOperation::QUERY_CANCEL_RESPONSE => TbepFrame::QueryCancelResponse,
        // Typed families.
        TbepOperation::FAMILY_READ => TbepFrame::FamilyReadRequest,
        TbepOperation::FAMILY_READ_RESPONSE => TbepFrame::FamilyReadResponse,
        TbepOperation::FAMILY_WRITE => TbepFrame::FamilyWriteRequest,
        TbepOperation::FAMILY_WRITE_RESPONSE => TbepFrame::FamilyWriteResponse,
        // Backbone subscriptions.
        TbepOperation::BACKBONE_SUBSCRIBE => TbepFrame::BackboneSubscribeRequest,
        TbepOperation::BACKBONE_SUBSCRIBE_RESPONSE => {
            TbepFrame::BackboneSubscribeResponse
        }
        TbepOperation::BACKBONE_ACKNOWLEDGE => TbepFrame::BackboneAcknowledgeRequest,
        TbepOperation::BACKBONE_ACKNOWLEDGE_RESPONSE => {
            TbepFrame::BackboneAcknowledgeResponse
        }
        TbepOperation::BACKBONE_UNSUBSCRIBE => TbepFrame::BackboneUnsubscribeRequest,
        TbepOperation::BACKBONE_UNSUBSCRIBE_RESPONSE => {
            TbepFrame::BackboneUnsubscribeResponse
        }
        // Sync delivery.
        TbepOperation::SYNC_SESSION_SETUP => TbepFrame::SyncSessionSetupRequest,
        TbepOperation::SYNC_SESSION_SETUP_RESPONSE => {
            TbepFrame::SyncSessionSetupResponse
        }
        TbepOperation::SYNC_DELTA_PULL => TbepFrame::SyncDeltaPullRequest,
        TbepOperation::SYNC_DELTA_PULL_RESPONSE => TbepFrame::SyncDeltaPullResponse,
        TbepOperation::SYNC_SNAPSHOT_PULL => TbepFrame::SyncSnapshotPullRequest,
        TbepOperation::SYNC_SNAPSHOT_PULL_RESPONSE => {
            TbepFrame::SyncSnapshotPullResponse
        }
        TbepOperation::SYNC_CHECKPOINT_ACK => TbepFrame::SyncCheckpointAckRequest,
        TbepOperation::SYNC_CHECKPOINT_ACK_RESPONSE => {
            TbepFrame::SyncCheckpointAckResponse
        }
        TbepOperation::SYNC_MUTATION_SUBMIT => TbepFrame::SyncMutationSubmitRequest,
        TbepOperation::SYNC_MUTATION_SUBMIT_RESPONSE => {
            TbepFrame::SyncMutationSubmitResponse
        }
        TbepOperation::SYNC_SESSION_CLOSE => TbepFrame::SyncSessionCloseRequest,
        TbepOperation::SYNC_SESSION_CLOSE_RESPONSE => {
            TbepFrame::SyncSessionCloseResponse
        }
        // Topology and admin.
        TbepOperation::TOPOLOGY_READ => TbepFrame::TopologyReadRequest,
        TbepOperation::TOPOLOGY_READ_RESPONSE => TbepFrame::TopologyReadResponse,
        TbepOperation::ADMIN_STATUS => TbepFrame::AdminStatusRequest,
        TbepOperation::ADMIN_STATUS_RESPONSE => TbepFrame::AdminStatusResponse,
        // Replication observation.
        TbepOperation::REPLICATION_CONFLICTS => TbepFrame::ReplicationConflictsRequest,
        TbepOperation::REPLICATION_CONFLICTS_RESPONSE => {
            TbepFrame::ReplicationConflictsResponse
        }
        TbepOperation::REPLICATION_FENCED => TbepFrame::ReplicationFencedRequest,
        TbepOperation::REPLICATION_FENCED_RESPONSE => {
            TbepFrame::ReplicationFencedResponse
        }
        TbepOperation::REPLICATION_DEPOSITIONS => {
            TbepFrame::ReplicationDepositionsRequest
        }
        TbepOperation::REPLICATION_DEPOSITIONS_RESPONSE => {
            TbepFrame::ReplicationDepositionsResponse
        }
        TbepOperation::REPLICATION_CURSOR => TbepFrame::ReplicationCursorRequest,
        TbepOperation::REPLICATION_CURSOR_RESPONSE => {
            TbepFrame::ReplicationCursorResponse
        }
        TbepOperation::REPLICATION_HEALTH => TbepFrame::ReplicationHealthRequest,
        TbepOperation::REPLICATION_HEALTH_RESPONSE => {
            TbepFrame::ReplicationHealthResponse
        }
        TbepOperation::REPLICATION_DENIALS => TbepFrame::ReplicationDenialsRequest,
        TbepOperation::REPLICATION_DENIALS_RESPONSE => {
            TbepFrame::ReplicationDenialsResponse
        }
        // Schemas and migrations.
        TbepOperation::SCHEMA_DECLARE => TbepFrame::SchemaDeclareRequest,
        TbepOperation::SCHEMA_DECLARE_RESPONSE => TbepFrame::SchemaDeclareResponse,
        TbepOperation::SCHEMA_STATUS => TbepFrame::SchemaStatusRequest,
        TbepOperation::SCHEMA_STATUS_RESPONSE => TbepFrame::SchemaStatusResponse,
        TbepOperation::SCHEMA_VALIDATE => TbepFrame::SchemaValidateRequest,
        TbepOperation::SCHEMA_VALIDATE_RESPONSE => TbepFrame::SchemaValidateResponse,
        TbepOperation::MIGRATION_STATUS => TbepFrame::MigrationStatusRequest,
        TbepOperation::MIGRATION_STATUS_RESPONSE => {
            TbepFrame::MigrationStatusResponse
        }
        TbepOperation::MIGRATION_DRY_RUN => TbepFrame::MigrationDryRunRequest,
        TbepOperation::MIGRATION_DRY_RUN_RESPONSE => {
            TbepFrame::MigrationDryRunResponse
        }
        TbepOperation::MIGRATION_APPLY => TbepFrame::MigrationApplyRequest,
        TbepOperation::MIGRATION_APPLY_RESPONSE => TbepFrame::MigrationApplyResponse,
        // Storage tiers.
        TbepOperation::TIER_CONFIGURE => TbepFrame::TierConfigureRequest,
        TbepOperation::TIER_CONFIGURE_RESPONSE => TbepFrame::TierConfigureResponse,
        TbepOperation::TIER_INSPECT => TbepFrame::TierInspectRequest,
        TbepOperation::TIER_INSPECT_RESPONSE => TbepFrame::TierInspectResponse,
        TbepOperation::TIER_DRY_RUN => TbepFrame::TierDryRunRequest,
        TbepOperation::TIER_DRY_RUN_RESPONSE => TbepFrame::TierDryRunResponse,
        TbepOperation::TIER_EXPLAIN => TbepFrame::TierExplainRequest,
        TbepOperation::TIER_EXPLAIN_RESPONSE => TbepFrame::TierExplainResponse,
        TbepOperation::TIER_RUN => TbepFrame::TierRunRequest,
        TbepOperation::TIER_RUN_RESPONSE => TbepFrame::TierRunResponse,
        TbepOperation::TIER_PAUSE => TbepFrame::TierPauseRequest,
        TbepOperation::TIER_PAUSE_RESPONSE => TbepFrame::TierPauseResponse,
        TbepOperation::TIER_RESUME => TbepFrame::TierResumeRequest,
        TbepOperation::TIER_RESUME_RESPONSE => TbepFrame::TierResumeResponse,
        // Notifications.
        TbepOperation::SUBSCRIPTION_BATCH => TbepFrame::SubscriptionBatchFrame,
        TbepOperation::TRIGGER_NOTIFICATION => TbepFrame::TriggerNotificationFrame,
        TbepOperation::SUBSCRIPTION_CLOSED => TbepFrame::SubscriptionClosedFrame,
        // The discriminant is an open newtype over `u16`: any numeric
        // value outside the registered codes (including the absent
        // sentinel) is unknown and fails closed before body access.
        _ => return None,
    };
    Some(member)
}
