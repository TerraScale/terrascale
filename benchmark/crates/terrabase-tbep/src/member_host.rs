//! Opt-in composition of the durable engine and sync-delivery planes.
//!
//! `MemberHost` is the first staged host unification. It keeps
//! [`EngineHost`] as the production harness, but lets focused callers
//! construct one host whose engine mutations and backbone revisions share
//! the engine publication journal.

use std::path::Path;
use std::sync::{Mutex, mpsc::Receiver};

use terrabase::engine::Engine;

use crate::engine_host::EngineHost;
use crate::host::{HostingError, SyncDeliveryHost, TbepHost, TbepNotification};
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

/// One TBEP host over a single engine and its durable publication journal.
pub struct MemberHost {
    engine: EngineHost,
    delivery: SyncDeliveryHost,
    journal_sync: Mutex<()>,
}

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

    fn tier_configure(
        &self,
        scope: &registry::BoundScope,
        request: &registry::TierConfigureRequest,
        idempotency_key: &str,
    ) -> Result<registry::TierConfigureResponse, HostingError> {
        self.engine.tier_configure(scope, request, idempotency_key)
    }

    fn tier_inspect(
        &self,
        scope: &registry::BoundScope,
    ) -> Result<registry::TierInspectResponse, HostingError> {
        self.engine.tier_inspect(scope)
    }

    fn tier_explain(
        &self,
        scope: &registry::BoundScope,
    ) -> Result<registry::TierExplainResponse, HostingError> {
        self.engine.tier_explain(scope)
    }

    fn tier_dry_run(
        &self,
        scope: &registry::BoundScope,
    ) -> Result<registry::TierDryRunResponse, HostingError> {
        self.engine.tier_dry_run(scope)
    }

    fn tier_run(
        &self,
        scope: &registry::BoundScope,
        idempotency_key: &str,
    ) -> Result<registry::TierRunResponse, HostingError> {
        self.engine.tier_run(scope, idempotency_key)
    }

    fn tier_pause(
        &self,
        scope: &registry::BoundScope,
        idempotency_key: &str,
    ) -> Result<registry::TierPauseResponse, HostingError> {
        self.engine.tier_pause(scope, idempotency_key)
    }

    fn tier_resume(
        &self,
        scope: &registry::BoundScope,
        idempotency_key: &str,
    ) -> Result<registry::TierResumeResponse, HostingError> {
        self.engine.tier_resume(scope, idempotency_key)
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
