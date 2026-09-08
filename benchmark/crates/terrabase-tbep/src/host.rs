//! The TBEP hosting seam: what the engine process serves.
//!
//! [`TbepHost`] is the seam the engine process wires behind TBEP: one
//! method per typed operation, the subscription feeds of the backbone
//! streams, and the supervision contract. The server (`crate::server`)
//! serves any `TbepHost` over the UDS binding.
//!
//! Two hosts ship in this crate:
//!
//! - [`crate::member_host::MemberHost`] — the opt-in TB-1a composition of one
//!   engine with journal-derived sync and backbone delivery.
//! - [`SyncDeliveryHost`] — the real serving half: the journal-backed
//!   backbone streams with content-free trigger notifications
//!   (PD-2026-08-26-13 D3), durable-journal materialization, and
//!   supervision. The document/query/sync-session data-plane RPCs were
//!   deleted with the S-03 TBEP reduction: the public API owns the data
//!   plane now, and the per-field CRDT materialization was deleted with
//!   the S-04 reduction (D-38): delivery serves the journal's change
//!   events, and `terrabase-sync-core` owns the result views.
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

use terrabase::streaming::{
    ChangeCheckpoint, SyncJournalCursor, SyncJournalLaneRecord, SyncJournalMutation,
    SyncJournalRecord,
};

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
use crate::registry::ErrorCode;
use crate::registry::HealthState;
use crate::registry::MigrationApplyRequest;
use crate::registry::MigrationApplyResponse;
use crate::registry::MigrationDryRunRequest;
use crate::registry::MigrationDryRunResponse;
use crate::registry::MigrationStatusRequest;
use crate::registry::MigrationStatusResponse;
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
use crate::registry::TopologyReadResponse;

/// The checkpoint window the serving core retains.
pub const CHECKPOINT_WINDOW: usize = 128;

/// The journal bound per scope (bounded memory: the retained window).
const JOURNAL_BOUND: usize = CHECKPOINT_WINDOW;

/// Bounds serving memory and the amount of work one mutation performs for
/// backbone consumers. These are deliberately conservative host-wide caps;
/// callers must refuse new resources once a cap is reached.
const SUBSCRIPTION_QUEUE_BOUND: usize = 64;
const MAX_SCOPES: usize = 4096;
const MAX_SUBSCRIPTIONS: usize = 1024;
const MAX_SUBSCRIPTIONS_PER_TENANT: usize = 128;

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
    journal: VecDeque<JournalRecord>,
}

impl ScopeState {
    fn record(&mut self, event: JournalRecord) {
        self.journal.push_back(event);
        while self.journal.len() > JOURNAL_BOUND {
            self.journal.pop_front();
        }
    }
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
    durable_lane_cursors: BTreeMap<DeliveryScope, SyncJournalCursor>,
    subscriptions: BTreeMap<u64, SubscriptionState>,
    reactive: crate::reactive::ReactiveState,
    frames_served: u64,
    health: HealthState,
}

/// The real sync-delivery host.
///
/// Serves the journal-backed backbone streams with content-free trigger
/// notifications, durable-journal materialization, topology/admin reads,
/// and the supervision contract. The sync-session data plane was deleted
/// with the S-03 TBEP reduction and the per-field CRDT materialization
/// with the S-04 reduction (D-38); delivery state now advances only
/// through [`SyncDeliveryHost::apply_durable_records`], which records the
/// journal's change events directly.
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
                durable_lane_cursors: BTreeMap::new(),
                subscriptions: BTreeMap::new(),
                reactive: crate::reactive::ReactiveState::default(),
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
        scopes
            .entry((DeliveryScope::of(bound_scope), collection.to_string()))
            .or_insert_with(|| ScopeState { journal: VecDeque::new() })
    }

    /// Looks up one subscription for its owning tenant.
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

    /// Applies a bounded merged page from independent physical lanes. Each
    /// lane is gated by its own cursor; a target lane's local sequence can
    /// therefore never be mistaken for an older source-lane record.
    pub(crate) fn apply_durable_lane_records(
        &self,
        bound_scope: &BoundScope,
        cursor: &SyncJournalCursor,
        records: &[SyncJournalLaneRecord],
    ) -> Result<(), HostingError> {
        let mut state = self.lock();
        Self::ensure_admission(&state)?;
        let delivery_scope = DeliveryScope::of(bound_scope);
        if let Some(current) = state.durable_lane_cursors.get(&delivery_scope)
            && current.binding() != cursor.binding()
        {
            return Err(HostingError::Refused(
                "the durable sync route binding changed; resnapshot is required".into(),
            ));
        }
        if let Some(current) = state.durable_lane_cursors.get(&delivery_scope)
            && current.lanes().iter().any(|(lane, previous)| {
                cursor.lanes().get(lane).is_none_or(|next| {
                    (next.sequence(), next.write_index()) < (previous.sequence(), previous.write_index())
                })
            })
        {
            return Err(HostingError::Refused(
                "the durable sync cursor regressed; resnapshot is required".into(),
            ));
        }
        let mut applied = Vec::new();
        for entry in records {
            let current = state
                .durable_lane_cursors
                .get(&delivery_scope)
                .and_then(|value| value.lanes().get(entry.lane()))
                .copied();
            if current.is_some_and(|checkpoint| {
                (entry.record().position().sequence(), entry.record().position().write_index())
                    <= (checkpoint.sequence(), checkpoint.write_index())
            }) {
                continue;
            }
            Self::can_add_scope(&state, bound_scope, entry.record().collection().as_str())?;
            state.reactive.apply_record(entry.record());
            let record = entry.record();
            Self::scope(&mut state.scopes, bound_scope, record.collection().as_str()).record(
                JournalRecord {
                    sequence: record.storage_sequence(),
                    write_index: record.position().write_index(),
                    operation_id: record.operation_id().as_str().to_owned(),
                    kind: match record.mutation() {
                        SyncJournalMutation::Put(_) => ChangeKind::Put,
                        SyncJournalMutation::Delete => ChangeKind::Delete,
                    },
                    collection: record.collection().as_str().to_owned(),
                    document: record.document().as_str().to_owned(),
                },
            );
            applied.push(record.clone());
        }
        // Commit the proposed per-lane cursor only after every record applied.
        state.durable_lane_cursors.insert(delivery_scope.clone(), cursor.clone());
        if !applied.is_empty() {
            Self::publish_durable_records(&mut state, bound_scope, &applied);
        }
        Ok(())
    }

    /// Attaches a reader to a shared view.  `authorization_scope` is the
    /// canonical rule/principal scope, rather than an opaque reader id, so
    /// equivalent authorizations collapse onto one materializer while
    /// distinct scopes remain isolated.
    pub(crate) fn reactive_subscribe_authorized(
        &self,
        scope: &BoundScope,
        authorization_scope: &str,
        spec: terrabase_sync_core::server::SubscriptionSpec,
    ) -> Result<(u64, Receiver<crate::reactive::ReactiveFrame>), HostingError> {
        let mut state = self.lock();
        Self::ensure_admission(&state)?;
        if state.reactive.len() >= MAX_SUBSCRIPTIONS {
            return Err(HostingError::SubscriptionRefused(
                "the reactive subscription limit is exhausted".into(),
            ));
        }
        let view_scope = format!(
            "{}\u{1f}{}\u{1f}{}\u{1f}{}",
            scope.tenant, scope.project, scope.database, authorization_scope
        );
        Ok(state.reactive.subscribe_scoped(view_scope, spec))
    }

    /// Seeds one reactive subscription's result and delivers the Seed
    /// frame (clears any reset-pending flag).
    pub(crate) fn reactive_seed(&self, id: u64, members: crate::reactive::ProjectedMembers) {
        self.lock().reactive.seed(id, members);
    }

    /// Seeds one view with projected fields and the raw canonical bodies
    /// produced by the query-plan adapter.
    pub(crate) fn reactive_seed_with_raw(
        &self,
        id: u64,
        members: crate::reactive::ProjectedMembers,
        raw: Vec<(String, Vec<u8>)>,
    ) {
        self.lock().reactive.seed_with_raw(id, members, raw);
    }

    /// Retracts one reactive subscription.
    pub(crate) fn reactive_unsubscribe(&self, id: u64) {
        self.lock().reactive.unsubscribe(id);
    }

    /// Scoped resume adapter for the public member host.
    pub(crate) fn reactive_resume_authorized(
        &self,
        scope: &BoundScope,
        authorization_scope: &str,
        previous: u64,
        revision: u64,
        spec: &terrabase_sync_core::server::SubscriptionSpec,
    ) -> Option<(
        u64,
        Receiver<crate::reactive::ReactiveFrame>,
        Vec<terrabase_sync_core::server::Delta>,
    )> {
        let view_scope = format!(
            "{}\u{1f}{}\u{1f}{}\u{1f}{}",
            scope.tenant, scope.project, scope.database, authorization_scope
        );
        self.lock().reactive.resume_scoped(&view_scope, previous, revision, spec)
    }

    /// Whether the reader is the first attachment of its shared view.
    pub(crate) fn reactive_needs_seed(&self, id: u64) -> bool {
        self.lock().reactive.needs_seed(id)
    }

    /// Returns a view row's canonical body bytes without decoding it.
    #[must_use]
    pub(crate) fn reactive_raw_body(&self, id: u64, document: &str) -> Option<Vec<u8>> {
        self.lock().reactive.raw_body(id, document).map(ToOwned::to_owned)
    }

    /// Marks one reactive subscription's transport closed while
    /// retaining its state for resume.
    pub(crate) fn reactive_detach(&self, id: u64) {
        self.lock().reactive.detach(id);
    }

    /// Applies records pulled from the engine's durable publication journal.
    ///
    /// This is the only composition path that advances a `MemberHost` sync
    /// revision.
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
            state.reactive.apply_record(record);
            {
                let scope =
                    Self::scope(&mut state.scopes, bound_scope, record.collection().as_str());
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

impl Default for SyncDeliveryHost {
    fn default() -> Self {
        Self::new(1)
    }
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
            sessions: 0,
            subscriptions: u32::try_from(state.subscriptions.len()).unwrap_or(u32::MAX),
            scopes: u32::try_from(state.scopes.len()).unwrap_or(u32::MAX),
        })
    }

    fn health(&self) -> Result<SupervisionHealthResponse, HostingError> {
        let state = self.lock();
        Ok(SupervisionHealthResponse {
            state: state.health,
            sessions: 0,
            subscriptions: u32::try_from(state.subscriptions.len()).unwrap_or(u32::MAX),
            frames_served: state.frames_served,
        })
    }

    fn begin_shutdown(
        &self,
        _request: &SupervisionShutdownRequest,
    ) -> Result<SupervisionShutdownResponse, HostingError> {
        let mut state = self.lock();
        let in_flight = u32::try_from(state.subscriptions.len()).unwrap_or(u32::MAX);
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
        Ok(SupervisionShutdownResponse { state: state.health, in_flight })
    }
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

#[cfg(test)]
mod tests {
    use super::*;

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

    #[test]
    fn subscriptions_and_subscribers_remember_their_complete_bound_scope() {
        // The complete database scope owns each subscription. Sharing a
        // tenant is not enough to cross that boundary.
        let host = SyncDeliveryHost::new(1);
        let owner = scope("tenant-a");
        let other_tenant = scope("tenant-b");
        let other_database = scope_with_database("tenant-a", "database-b");
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
            .backbone_acknowledge(
                &other_tenant,
                &BackboneAcknowledgeRequest { subscription_id: 42, through_sequence: 0 },
            )
            .unwrap_err();
        assert!(matches!(error, HostingError::SubscriptionRefused(_)), "{error:?}");

        // The owner's channel still owns the lifecycle end to end.
        host.backbone_unsubscribe(&owner, &BackboneUnsubscribeRequest { subscription_id: 42 })
            .expect("unsubscribes");
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
}
