//! Opt-in composition of the durable engine and sync-delivery planes.
//!
//! `MemberHost` is the first staged host unification. It keeps
//! [`EngineHost`] as the production harness, but lets focused callers
//! construct one host whose engine mutations and backbone revisions share
//! the engine publication journal.

use std::path::Path;
use std::sync::Mutex;
use std::sync::mpsc::Receiver;

use terrabase::engine::Engine;
use terrabase::query::QueryError;

use crate::engine_host::EngineHost;
use crate::host::{HostingError, SyncDeliveryHost, TbepHost, TbepNotification};
use crate::reactive::{ReactivePlanAdapter, canonical_json_bytes, extract_path};
use crate::registry;

/// Explicit construction options for the staged `MemberHost`.
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct MemberHostOptions {
    durable_sync_backbone: bool,
}

impl MemberHostOptions {
    /// Enables the TB-1a engine plus durable sync/backbone composition.
    #[must_use]
    pub const fn tb1a() -> Self {
        Self { durable_sync_backbone: true }
    }
}

/// One opened reactive subscription: its identity, its delivery feed,
/// and — on a checkpoint resume that the retained revisions covered —
/// the exact missed revisions (a fresh subscription carries `None` and
/// a Seed frame instead).
pub type ReactiveOpen = (
    u64,
    Receiver<crate::reactive::ReactiveFrame>,
    Option<Vec<terrabase_sync_core::server::Delta>>,
);

/// One TBEP host over a single engine and its durable publication journal.
pub struct MemberHost {
    engine: EngineHost,
    delivery: SyncDeliveryHost,
    journal_sync: Mutex<()>,
}

type SubscriptionSeed = (crate::reactive::ProjectedMembers, Vec<(String, Vec<u8>)>);

impl MemberHost {
    /// Opens the staged host when the explicit TB-1a construction flag is on.
    ///
    /// # Errors
    ///
    /// Returns a typed refusal when the flag is off, or the engine's typed
    /// open failure when the durable location cannot be served.
    pub fn open(
        location: &Path,
        instance: u64,
        tenant: &str,
        project: &str,
        database: &str,
        options: MemberHostOptions,
    ) -> Result<Self, HostingError> {
        if !options.durable_sync_backbone {
            return Err(HostingError::Refused(
                "MemberHost construction requires the TB-1a durable sync/backbone flag".into(),
            ));
        }
        Ok(Self {
            engine: EngineHost::open(location, instance, tenant, project, database)?,
            delivery: SyncDeliveryHost::new(instance),
            journal_sync: Mutex::new(()),
        })
    }

    /// Composes public and sync serving over the same admitted Member lease.
    #[must_use]
    pub fn from_admitted(
        lease: terrabase::member::DatabaseLease,
        location: std::path::PathBuf,
        instance: u64,
    ) -> Self {
        Self {
            engine: EngineHost::from_admitted(lease, location, instance),
            delivery: SyncDeliveryHost::new(instance),
            journal_sync: Mutex::new(()),
        }
    }

    /// Logical serving tenant; production uses the immutable admitted owner.
    #[must_use]
    pub fn tenant(&self) -> &str {
        self.engine.tenant()
    }

    /// Returns the single durable location shared by both serving planes.
    #[must_use]
    pub fn location(&self) -> &Path {
        self.engine.location()
    }

    /// Returns the underlying engine, so the public API can serve the same
    /// durable storage this host's backbone delivery derives from.
    #[must_use]
    pub fn engine(&self) -> &Engine {
        self.engine.engine()
    }

    /// Returns the engine host this member composes: the plane the
    /// cross-region replication tailer pulls and applies through.
    #[must_use]
    pub fn engine_host(&self) -> &EngineHost {
        &self.engine
    }

    /// Subscribes one reactive result view (`S-04`): brings the journal
    /// pump current, registers the subscription, then seeds it from a
    /// consistent scan of the collection so the Seed frame and every
    /// later delta describe one continuous revision history.
    ///
    /// # Errors
    ///
    /// Returns the pump's engine failure or the delivery host's typed
    /// admission refusal.
    pub fn reactive_subscribe(
        &self,
        scope: &registry::BoundScope,
        spec: &terrabase_sync_core::server::SubscriptionSpec,
        resume: Option<(u64, u64)>,
    ) -> Result<ReactiveOpen, HostingError> {
        self.reactive_subscribe_authorized(scope, scope.principal.as_str(), spec, resume)
    }

    /// Attaches an authorization-equivalent reader to the shared SQL view.
    /// The authorization scope is part of the view identity; callers must
    /// pass the canonical rule/entitlement binding, never a per-request id.
    ///
    /// # Errors
    ///
    /// Returns a hosting error for invalid scope or query, unavailable
    /// snapshot data, or a subscription that cannot be admitted.
    pub fn reactive_subscribe_authorized(
        &self,
        scope: &registry::BoundScope,
        authorization_scope: &str,
        spec: &terrabase_sync_core::server::SubscriptionSpec,
        resume: Option<(u64, u64)>,
    ) -> Result<ReactiveOpen, HostingError> {
        self.synchronize(scope)?;
        if let Some((previous, revision)) = resume
            && let Some((id, receiver, missed)) = self.delivery.reactive_resume_authorized(
                scope,
                authorization_scope,
                previous,
                revision,
                spec,
            )
        {
            return Ok((id, receiver, Some(missed)));
        }
        let (id, receiver) = self.delivery.reactive_subscribe_authorized(
            scope,
            authorization_scope,
            spec.clone(),
        )?;
        // A shared view is scanned exactly once.  Every later reader gets
        // the already materialized snapshot from `ReactiveState`.
        if self.delivery.reactive_needs_seed(id) {
            let (members, raw) = self.scan_members(scope, spec)?;
            self.delivery.reactive_seed_with_raw(id, members, raw);
        } else {
            self.delivery.reactive_seed(id, Vec::new());
        }
        Ok((id, receiver, None))
    }

    /// Opens a reactive view from the query planner's compiled SQL plan.
    /// Snapshot seeding executes that plan once; committed rows then flow
    /// through the dependency-indexed shared materializer.
    ///
    /// # Errors
    ///
    /// Returns a hosting error for invalid scope or query, unavailable
    /// snapshot data, or a subscription that cannot be admitted.
    pub fn reactive_subscribe_plan(
        &self,
        scope: &registry::BoundScope,
        plan: &ReactivePlanAdapter,
        authorization_scope: &str,
        resume: Option<(u64, u64)>,
    ) -> Result<ReactiveOpen, HostingError> {
        self.synchronize(scope)?;
        let spec = plan.subscription_spec();
        if let Some(refusal) = plan.query_plan().sql_execute_only_refusal() {
            return Err(HostingError::Invalid(format!(
                "the reactive query is execute-only: {refusal:?}"
            )));
        }
        if plan
            .query_plan()
            .source()
            .collection()
            .is_none_or(|collection| collection.as_str() != spec.collection.as_str())
        {
            return Err(HostingError::Invalid(
                "the reactive SQL plan and dependency collection differ".into(),
            ));
        }
        if let Some((previous, revision)) = resume
            && let Some(opened) = self.delivery.reactive_resume_authorized(
                scope,
                authorization_scope,
                previous,
                revision,
                spec,
            )
        {
            return Ok((opened.0, opened.1, Some(opened.2)));
        }
        let (id, receiver) = self.delivery.reactive_subscribe_authorized(
            scope,
            authorization_scope,
            spec.clone(),
        )?;
        if self.delivery.reactive_needs_seed(id) {
            let engine_scope = self.engine.engine_scope_of(scope)?;
            let snapshot = self.engine.read_snapshot(&engine_scope).map_err(|error| {
                HostingError::EngineFailure(format!("the reactive plan snapshot failed: {error:?}"))
            })?;
            let result =
                self.engine.engine().queries().execute(plan.query_plan(), &snapshot).map_err(
                    |error: QueryError| {
                        HostingError::Invalid(format!(
                            "the reactive SQL plan is not servable: {error:?}"
                        ))
                    },
                )?;
            let mut members = Vec::new();
            let mut raw = Vec::new();
            for row in result.rows() {
                let fields = row
                    .projected()
                    .iter()
                    .filter_map(|(field, value)| {
                        field_value(value).map(|value| (field.clone(), value))
                    })
                    .collect();
                let document = row.document();
                members.push((document.id().as_str().to_owned(), fields));
                raw.push((
                    document.id().as_str().to_owned(),
                    canonical_json_bytes(document.value()),
                ));
            }
            self.delivery.reactive_seed_with_raw(id, members, raw);
        } else {
            self.delivery.reactive_seed(id, Vec::new());
        }
        Ok((id, receiver, None))
    }

    /// Pumps the journal: new commits flow to every delivery plane,
    /// reactive subscriptions included. The transport calls this per
    /// poll/upgrade tick.
    ///
    /// # Errors
    ///
    /// Returns the pump's engine failure when the journal cannot be
    /// replayed.
    pub fn reactive_pump(&self, scope: &registry::BoundScope) -> Result<(), HostingError> {
        self.synchronize(scope)
    }

    /// Retracts one reactive subscription.
    pub fn reactive_unsubscribe(&self, id: u64) {
        self.delivery.reactive_unsubscribe(id);
    }

    /// Marks one reactive subscription's transport closed while
    /// retaining its state for checkpoint resume (the retention window
    /// ends when the abandoned delivery channel fills).
    pub fn reactive_detach(&self, id: u64) {
        self.delivery.reactive_detach(id);
    }

    /// Returns the exact canonical JSON bytes retained for a reader's view
    /// row.  The returned copy is suitable for splicing into a response or
    /// handing to a replication adapter without parsing it.
    #[must_use]
    pub fn reactive_raw_body(&self, id: u64, document: &str) -> Option<Vec<u8>> {
        self.delivery.reactive_raw_body(id, document)
    }

    /// One subscription's seed members from a consistent engine scan:
    /// the documents whose extracted values satisfy the spec's predicate,
    /// projected per the spec, in document order.
    fn scan_members(
        &self,
        scope: &registry::BoundScope,
        spec: &terrabase_sync_core::server::SubscriptionSpec,
    ) -> Result<SubscriptionSeed, HostingError> {
        use terrabase_sync_core::server::Condition;

        let engine = self.engine.engine();
        let engine_scope = self.engine.engine_scope_of(scope)?;
        let collection = terrabase::types::CollectionName::try_new(&spec.collection)
            .map_err(|_| HostingError::Invalid("the collection name is not valid".into()))?;
        let snapshot = engine.read_snapshot(&engine_scope).map_err(|error| {
            HostingError::EngineFailure(format!("the scan snapshot failed: {error:?}"))
        })?;
        let documents = engine
            .snapshot_documents_in_collection(&engine_scope, &snapshot, &collection)
            .map_err(|error| {
                HostingError::EngineFailure(format!("the collection scan failed: {error:?}"))
            })?;
        let mut fields: std::collections::BTreeSet<String> =
            spec.conditions.iter().map(|condition| condition.field.clone()).collect();
        for field in &spec.projection {
            fields.insert(field.clone());
        }
        let mut members = Vec::new();
        let mut raw = Vec::new();
        for document in documents {
            let values: Vec<(String, Option<terrabase_sync_core::subscription::FieldValue>)> =
                fields
                    .iter()
                    .map(|field| (field.clone(), extract_path(document.value(), field)))
                    .collect();
            let member = spec.conditions.iter().all(|condition: &Condition| {
                values.iter().any(|(field, value)| {
                    field == &condition.field
                        && value.as_ref().is_some_and(|v| condition.matches(v))
                })
            });
            if !member {
                continue;
            }
            let projected = spec
                .projection
                .iter()
                .filter_map(|field| {
                    values
                        .iter()
                        .find(|(name, _)| name == field)
                        .and_then(|(_, value)| value.clone().map(|v| (field.clone(), v)))
                })
                .collect();
            members.push((document.id().as_str().to_owned(), projected));
            raw.push((document.id().as_str().to_owned(), canonical_json_bytes(document.value())));
        }
        Ok((members, raw))
    }

    fn synchronize(&self, scope: &registry::BoundScope) -> Result<(), HostingError> {
        let _serial = self.journal_sync.lock().expect("the journal sync lock is not poisoned");
        loop {
            let before = self.delivery.durable_cursor(scope);
            let batch = self.engine.replay_sync_journal(scope, before)?;
            self.delivery.apply_durable_records(scope, batch.records())?;
            if let Some(next) = batch.next_checkpoint() {
                self.delivery.advance_durable_cursor(scope, next);
            }
            if batch.drained() {
                return Ok(());
            }
            let after = self.delivery.durable_cursor(scope);
            if after == before {
                return Err(HostingError::EngineFailure(
                    "the durable sync journal did not advance".into(),
                ));
            }
        }
    }

    fn combined_health(
        engine: registry::HealthState,
        delivery: registry::HealthState,
    ) -> registry::HealthState {
        use registry::HealthState::{Draining, Serving, Starting, Stopped};
        match (engine, delivery) {
            (Stopped, _) | (_, Stopped) => Stopped,
            (Draining, _) | (_, Draining) => Draining,
            (Starting, _) | (_, Starting) => Starting,
            (Serving, Serving) => Serving,
        }
    }
}

fn field_value(
    value: &terrabase::types::DocumentValue,
) -> Option<terrabase_sync_core::subscription::FieldValue> {
    match value {
        terrabase::types::DocumentValue::String(value) => {
            Some(terrabase_sync_core::subscription::FieldValue::Text(value.clone()))
        }
        terrabase::types::DocumentValue::Integer(value) =>
        {
            #[allow(clippy::cast_precision_loss, reason = "sync dependency values use f64")]
            Some(terrabase_sync_core::subscription::FieldValue::Number(*value as f64))
        }
        terrabase::types::DocumentValue::Number(value) => {
            Some(terrabase_sync_core::subscription::FieldValue::Number(*value))
        }
        _ => None,
    }
}

impl TbepHost for MemberHost {
    fn bind_channel(
        &self,
        request: &registry::ChannelBindRequest,
    ) -> Result<registry::ChannelBindResponse, HostingError> {
        let response = self.engine.bind_channel(request)?;
        self.delivery.bind_channel(request)?;
        Ok(response)
    }

    fn backbone_subscribe(
        &self,
        scope: &registry::BoundScope,
        request: &registry::BackboneSubscribeRequest,
    ) -> Result<(registry::BackboneSubscribeResponse, Receiver<TbepNotification>), HostingError>
    {
        self.synchronize(scope)?;
        self.delivery.backbone_subscribe(scope, request)
    }

    fn backbone_acknowledge(
        &self,
        scope: &registry::BoundScope,
        request: &registry::BackboneAcknowledgeRequest,
    ) -> Result<registry::BackboneAcknowledgeResponse, HostingError> {
        self.synchronize(scope)?;
        self.delivery.backbone_acknowledge(scope, request)
    }

    fn backbone_unsubscribe(
        &self,
        scope: &registry::BoundScope,
        request: &registry::BackboneUnsubscribeRequest,
    ) -> Result<registry::BackboneUnsubscribeResponse, HostingError> {
        self.delivery.backbone_unsubscribe(scope, request)
    }

    fn schema_declare(
        &self,
        scope: &registry::BoundScope,
        request: &registry::SchemaDeclareRequest,
        idempotency_key: &str,
    ) -> Result<registry::SchemaDeclareResponse, HostingError> {
        self.engine.schema_declare(scope, request, idempotency_key)
    }

    fn schema_status(
        &self,
        scope: &registry::BoundScope,
        request: &registry::SchemaStatusRequest,
    ) -> Result<registry::SchemaStatusResponse, HostingError> {
        self.engine.schema_status(scope, request)
    }

    fn schema_validate(
        &self,
        scope: &registry::BoundScope,
        request: &registry::SchemaValidateRequest,
    ) -> Result<registry::SchemaValidateResponse, HostingError> {
        self.engine.schema_validate(scope, request)
    }

    fn topology_read(
        &self,
        scope: &registry::BoundScope,
    ) -> Result<registry::TopologyReadResponse, HostingError> {
        let mut response = self.engine.topology_read(scope)?;
        response.subsystems.push("sync-delivery".into());
        response.subsystems.push("backbone".into());
        Ok(response)
    }

    fn usage_record(
        &self,
        scope: &registry::BoundScope,
        request: &registry::UsageRecordRequest,
        idempotency_key: &str,
    ) -> Result<registry::UsageRecordResponse, HostingError> {
        self.engine.usage_record(scope, request, idempotency_key)
    }

    fn usage_export(
        &self,
        scope: &registry::BoundScope,
        request: &registry::UsageExportRequest,
    ) -> Result<registry::UsageExportResponse, HostingError> {
        self.engine.usage_export(scope, request)
    }

    fn admin_status(&self) -> Result<registry::AdminStatusResponse, HostingError> {
        let engine = self.engine.admin_status()?;
        let delivery = self.delivery.admin_status()?;
        Ok(registry::AdminStatusResponse {
            registry_version: engine.registry_version,
            frames_served: engine.frames_served.saturating_add(delivery.frames_served),
            sessions: 0,
            subscriptions: delivery.subscriptions,
            scopes: delivery.scopes,
        })
    }

    fn health(&self) -> Result<registry::SupervisionHealthResponse, HostingError> {
        let engine = self.engine.health()?;
        let delivery = self.delivery.health()?;
        Ok(registry::SupervisionHealthResponse {
            state: Self::combined_health(engine.state, delivery.state),
            sessions: 0,
            subscriptions: delivery.subscriptions,
            frames_served: engine.frames_served.saturating_add(delivery.frames_served),
        })
    }

    fn begin_shutdown(
        &self,
        request: &registry::SupervisionShutdownRequest,
    ) -> Result<registry::SupervisionShutdownResponse, HostingError> {
        let delivery = self.delivery.begin_shutdown(request)?;
        let engine = self.engine.begin_shutdown(request)?;
        Ok(registry::SupervisionShutdownResponse {
            state: Self::combined_health(engine.state, delivery.state),
            in_flight: engine.in_flight.saturating_add(delivery.in_flight),
        })
    }
}
