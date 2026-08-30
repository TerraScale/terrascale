//! Opt-in composition of the durable engine and sync-delivery planes.
//!
//! `MemberHost` is the first staged host unification. It keeps
//! [`EngineHost`](crate::engine_host::EngineHost) as the production harness,
//! but lets focused callers construct one host whose document mutations and
//! sync/backbone revisions share the engine publication journal.

use std::path::Path;
use std::sync::{Mutex, mpsc::Receiver};

use crate::engine_host::EngineHost;
use crate::host::{HostingError, SyncDeliveryHost, TbepHost, TbepNotification};
use crate::registry;
use crate::value::TbepValue;

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

    fn validate_sync_body(body: &TbepValue) -> Result<(), HostingError> {
        let TbepValue::Object(fields) = body else {
            return Err(HostingError::Invalid(
                "MemberHost sync requires an object document body".into(),
            ));
        };
        if fields
            .iter()
            .any(|(_, value)| matches!(value, TbepValue::Array(_) | TbepValue::Object(_)))
        {
            return Err(HostingError::Invalid(
                "MemberHost sync does not accept composite document fields".into(),
            ));
        }
        Ok(())
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

    fn sync_mutation_request(
        &self,
        scope: &registry::BoundScope,
        request: &registry::SyncMutationSubmitRequest,
        idempotency_key: &str,
    ) -> Result<registry::WriteOutcome, HostingError> {
        let collection = self.delivery.collection_for_session(scope, request.session_id)?;
        match &request.change {
            registry::SyncChange::Put(fields) => {
                let body = TbepValue::Object(
                    fields.iter().map(|field| (field.name.clone(), field.value.clone())).collect(),
                );
                Self::validate_sync_body(&body)?;
                self.engine
                    .document_write(
                        scope,
                        &registry::DocumentWriteRequest {
                            document: registry::DocumentPut {
                                id: request.document.clone(),
                                collection,
                                body,
                            },
                            expected: registry::ExpectedVersion::Any,
                            operation_id: idempotency_key.to_owned(),
                            ack: Some(registry::Acknowledgement::LocalSynced),
                            usage: request.usage.clone(),
                        },
                        idempotency_key,
                    )
                    .map(|response| response.outcome)
            }
            registry::SyncChange::Delete => self
                .engine
                .document_delete(
                    scope,
                    &registry::DocumentDeleteRequest {
                        collection,
                        document: request.document.clone(),
                        expected: registry::ExpectedVersion::Any,
                        operation_id: idempotency_key.to_owned(),
                        ack: Some(registry::Acknowledgement::LocalSynced),
                        usage: request.usage.clone(),
                    },
                    idempotency_key,
                )
                .map(|response| response.outcome),
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

    fn document_read(
        &self,
        scope: &registry::BoundScope,
        request: &registry::DocumentReadRequest,
    ) -> Result<registry::DocumentReadResponse, HostingError> {
        self.engine.document_read(scope, request)
    }

    fn document_write(
        &self,
        scope: &registry::BoundScope,
        request: &registry::DocumentWriteRequest,
        idempotency_key: &str,
    ) -> Result<registry::DocumentWriteResponse, HostingError> {
        Self::validate_sync_body(&request.document.body)?;
        let response = self.engine.document_write(scope, request, idempotency_key)?;
        if matches!(response.outcome, registry::WriteOutcome::Committed(_)) {
            self.synchronize(scope)?;
        }
        Ok(response)
    }

    fn document_delete(
        &self,
        scope: &registry::BoundScope,
        request: &registry::DocumentDeleteRequest,
        idempotency_key: &str,
    ) -> Result<registry::DocumentDeleteResponse, HostingError> {
        let response = self.engine.document_delete(scope, request, idempotency_key)?;
        if matches!(response.outcome, registry::WriteOutcome::Committed(_)) {
            self.synchronize(scope)?;
        }
        Ok(response)
    }

    fn document_batch(
        &self,
        scope: &registry::BoundScope,
        request: &registry::DocumentBatchRequest,
        idempotency_key: &str,
    ) -> Result<registry::DocumentBatchResponse, HostingError> {
        for write in &request.items {
            Self::validate_sync_body(&write.document.body)?;
        }
        let response = self.engine.document_batch(scope, request, idempotency_key)?;
        self.synchronize(scope)?;
        Ok(response)
    }

    fn query_execute(
        &self,
        scope: &registry::BoundScope,
        request: &registry::QueryExecuteRequest,
    ) -> Result<registry::QueryExecuteResponse, HostingError> {
        self.engine.query_execute(scope, request)
    }

    fn query_cancel(
        &self,
        scope: &registry::BoundScope,
        request: &registry::QueryCancelRequest,
    ) -> Result<registry::QueryCancelResponse, HostingError> {
        self.engine.query_cancel(scope, request)
    }

    fn family_read(
        &self,
        scope: &registry::BoundScope,
        request: &registry::FamilyReadRequest,
    ) -> Result<registry::FamilyReadResponse, HostingError> {
        self.engine.family_read(scope, request)
    }

    fn family_write(
        &self,
        scope: &registry::BoundScope,
        request: &registry::FamilyWriteRequest,
        idempotency_key: &str,
    ) -> Result<registry::FamilyWriteResponse, HostingError> {
        let response = self.engine.family_write(scope, request, idempotency_key)?;
        self.synchronize(scope)?;
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

    fn sync_session_setup(
        &self,
        scope: &registry::BoundScope,
        request: &registry::SyncSessionSetupRequest,
    ) -> Result<registry::SyncSessionSetupResponse, HostingError> {
        self.synchronize(scope)?;
        self.delivery.sync_session_setup(scope, request)
    }

    fn sync_delta_pull(
        &self,
        scope: &registry::BoundScope,
        request: &registry::SyncDeltaPullRequest,
    ) -> Result<registry::SyncDeltaPullResponse, HostingError> {
        self.synchronize(scope)?;
        self.delivery.sync_delta_pull(scope, request)
    }

    fn sync_snapshot_pull(
        &self,
        scope: &registry::BoundScope,
        request: &registry::SyncSnapshotPullRequest,
    ) -> Result<registry::SyncSnapshotPullResponse, HostingError> {
        self.synchronize(scope)?;
        self.delivery.sync_snapshot_pull(scope, request)
    }

    fn sync_checkpoint_ack(
        &self,
        scope: &registry::BoundScope,
        request: &registry::SyncCheckpointAckRequest,
    ) -> Result<registry::SyncCheckpointAckResponse, HostingError> {
        self.delivery.sync_checkpoint_ack(scope, request)
    }

    fn sync_mutation_submit(
        &self,
        scope: &registry::BoundScope,
        request: &registry::SyncMutationSubmitRequest,
        idempotency_key: &str,
    ) -> Result<registry::SyncMutationSubmitResponse, HostingError> {
        self.synchronize(scope)?;
        let outcome = self.sync_mutation_request(scope, request, idempotency_key)?;
        self.synchronize(scope)?;
        let outcome = match outcome {
            registry::WriteOutcome::Committed(receipt) => {
                registry::MutationOutcome::Applied(receipt)
            }
            registry::WriteOutcome::Conflict => {
                registry::MutationOutcome::Rejected(registry::MutationReason::SequenceOutOfOrder)
            }
            registry::WriteOutcome::Rejected(_) => {
                registry::MutationOutcome::Rejected(registry::MutationReason::InvalidBody)
            }
        };
        Ok(registry::SyncMutationSubmitResponse { session_id: request.session_id, outcome })
    }

    fn sync_session_close(
        &self,
        scope: &registry::BoundScope,
        request: &registry::SyncSessionCloseRequest,
    ) -> Result<registry::SyncSessionCloseResponse, HostingError> {
        self.delivery.sync_session_close(scope, request)
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
            sessions: delivery.sessions,
            subscriptions: delivery.subscriptions,
            scopes: delivery.scopes,
        })
    }

    fn health(&self) -> Result<registry::SupervisionHealthResponse, HostingError> {
        let engine = self.engine.health()?;
        let delivery = self.delivery.health()?;
        Ok(registry::SupervisionHealthResponse {
            state: Self::combined_health(engine.state, delivery.state),
            sessions: engine.sessions.saturating_add(delivery.sessions),
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
