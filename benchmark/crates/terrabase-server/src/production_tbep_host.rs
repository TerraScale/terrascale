//! Production TBEP host over the retained admitted database members.

use std::sync::Arc;
use std::sync::mpsc::Receiver;

use terrabase_tbep::TbepHost;
use terrabase_tbep::member_host::MemberHost;
use terrabase_tbep::registry;

use crate::admitted_databases::{AdmittedDatabase, AdmittedDatabaseRegistry};

/// Routes every scoped TBEP operation to the already admitted member that
/// owns the requested database.
pub struct ProductionTbepHost {
    registry: Arc<AdmittedDatabaseRegistry>,
}

impl ProductionTbepHost {
    /// Creates a host backed by the production admission registry.
    #[must_use]
    pub fn new(registry: Arc<AdmittedDatabaseRegistry>) -> Self {
        Self { registry }
    }

    fn member(
        &self,
        scope: &registry::BoundScope,
    ) -> Result<Arc<MemberHost>, terrabase_tbep::HostingError> {
        let database = self
            .registry
            .resolve_serving(&scope.database)
            .map_err(|error| terrabase_tbep::HostingError::Refused(error.clone()))?
            .ok_or_else(|| {
                terrabase_tbep::HostingError::Refused("database is not serving".into())
            })?;
        Self::verify_scope(scope, &database)?;
        Ok(Arc::clone(&database.host))
    }

    fn verify_scope(
        scope: &registry::BoundScope,
        database: &AdmittedDatabase,
    ) -> Result<(), terrabase_tbep::HostingError> {
        let record = &database.record;
        if scope.tenant != record.tenant_id
            || scope.project != record.project_id
            || scope.database != record.database_id
        {
            return Err(terrabase_tbep::HostingError::Refused(
                "the bound scope does not match the admitted database owner".into(),
            ));
        }
        Ok(())
    }

    fn all_members(&self) -> Result<Vec<Arc<AdmittedDatabase>>, terrabase_tbep::HostingError> {
        self.registry
            .retained_snapshot()
            .map(|entries| entries.into_iter().map(|entry| entry.database).collect())
            .map_err(|error| terrabase_tbep::HostingError::Refused(error.clone()))
    }

    fn combined(a: registry::HealthState, b: registry::HealthState) -> registry::HealthState {
        use registry::HealthState::{Draining, Serving, Starting, Stopped};
        match (a, b) {
            (Stopped, _) | (_, Stopped) => Stopped,
            (Draining, _) | (_, Draining) => Draining,
            (Starting, _) | (_, Starting) => Starting,
            (Serving, Serving) => Serving,
        }
    }
}

impl TbepHost for ProductionTbepHost {
    fn bind_channel(
        &self,
        request: &registry::ChannelBindRequest,
    ) -> Result<registry::ChannelBindResponse, terrabase_tbep::HostingError> {
        let member = self.member(&request.scope)?;
        member.bind_channel(request)
    }

    fn backbone_subscribe(
        &self,
        scope: &registry::BoundScope,
        request: &registry::BackboneSubscribeRequest,
    ) -> Result<
        (registry::BackboneSubscribeResponse, Receiver<terrabase_tbep::host::TbepNotification>),
        terrabase_tbep::HostingError,
    > {
        self.member(scope)?.backbone_subscribe(scope, request)
    }

    fn backbone_acknowledge(
        &self,
        scope: &registry::BoundScope,
        request: &registry::BackboneAcknowledgeRequest,
    ) -> Result<registry::BackboneAcknowledgeResponse, terrabase_tbep::HostingError> {
        self.member(scope)?.backbone_acknowledge(scope, request)
    }

    fn backbone_unsubscribe(
        &self,
        scope: &registry::BoundScope,
        request: &registry::BackboneUnsubscribeRequest,
    ) -> Result<registry::BackboneUnsubscribeResponse, terrabase_tbep::HostingError> {
        self.member(scope)?.backbone_unsubscribe(scope, request)
    }

    fn schema_declare(
        &self,
        scope: &registry::BoundScope,
        request: &registry::SchemaDeclareRequest,
        idempotency_key: &str,
    ) -> Result<registry::SchemaDeclareResponse, terrabase_tbep::HostingError> {
        self.member(scope)?.schema_declare(scope, request, idempotency_key)
    }

    fn schema_status(
        &self,
        scope: &registry::BoundScope,
        request: &registry::SchemaStatusRequest,
    ) -> Result<registry::SchemaStatusResponse, terrabase_tbep::HostingError> {
        self.member(scope)?.schema_status(scope, request)
    }

    fn schema_validate(
        &self,
        scope: &registry::BoundScope,
        request: &registry::SchemaValidateRequest,
    ) -> Result<registry::SchemaValidateResponse, terrabase_tbep::HostingError> {
        self.member(scope)?.schema_validate(scope, request)
    }

    fn topology_read(
        &self,
        scope: &registry::BoundScope,
    ) -> Result<registry::TopologyReadResponse, terrabase_tbep::HostingError> {
        self.member(scope)?.topology_read(scope)
    }

    fn usage_record(
        &self,
        scope: &registry::BoundScope,
        request: &registry::UsageRecordRequest,
        idempotency_key: &str,
    ) -> Result<registry::UsageRecordResponse, terrabase_tbep::HostingError> {
        self.member(scope)?.usage_record(scope, request, idempotency_key)
    }

    fn usage_export(
        &self,
        scope: &registry::BoundScope,
        request: &registry::UsageExportRequest,
    ) -> Result<registry::UsageExportResponse, terrabase_tbep::HostingError> {
        self.member(scope)?.usage_export(scope, request)
    }

    fn admin_status(&self) -> Result<registry::AdminStatusResponse, terrabase_tbep::HostingError> {
        let mut result = registry::AdminStatusResponse {
            registry_version: registry::REGISTRY_VERSION.into(),
            frames_served: 0,
            sessions: 0,
            subscriptions: 0,
            scopes: 0,
        };
        for database in self.all_members()? {
            let status = database.host.admin_status()?;
            result.frames_served = result.frames_served.saturating_add(status.frames_served);
            result.sessions = result.sessions.saturating_add(status.sessions);
            result.subscriptions = result.subscriptions.saturating_add(status.subscriptions);
            result.scopes = result.scopes.saturating_add(status.scopes);
        }
        Ok(result)
    }

    fn health(&self) -> Result<registry::SupervisionHealthResponse, terrabase_tbep::HostingError> {
        let mut result = registry::SupervisionHealthResponse {
            state: registry::HealthState::Starting,
            sessions: 0,
            subscriptions: 0,
            frames_served: 0,
        };
        for database in self.all_members()? {
            let health = database.host.health()?;
            result.state = Self::combined(result.state, health.state);
            result.sessions = result.sessions.saturating_add(health.sessions);
            result.subscriptions = result.subscriptions.saturating_add(health.subscriptions);
            result.frames_served = result.frames_served.saturating_add(health.frames_served);
        }
        Ok(result)
    }

    fn begin_shutdown(
        &self,
        _request: &registry::SupervisionShutdownRequest,
    ) -> Result<registry::SupervisionShutdownResponse, terrabase_tbep::HostingError> {
        self.registry
            .begin_drain()
            .map_err(|error| terrabase_tbep::HostingError::Refused(error.clone()))?;
        // The runtime owns reverse-order teardown of replication and native
        // listeners. Retained MemberHost instances must remain available for
        // those drains; fencing the registry is the only action owned here.
        let in_flight = self
            .all_members()?
            .into_iter()
            .map(|database| database.host.health().map(|health| health.subscriptions))
            .try_fold(0_u32, |total, subscriptions| {
                subscriptions.map(|count| total.saturating_add(count))
            })?;
        Ok(registry::SupervisionShutdownResponse {
            state: registry::HealthState::Draining,
            in_flight,
        })
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn scope(record: &crate::admission::AdmissionRecord) -> registry::BoundScope {
        registry::BoundScope {
            principal: "gateway".into(),
            tenant: record.tenant_id.clone(),
            project: record.project_id.clone(),
            database: record.database_id.clone(),
        }
    }

    #[test]
    fn routes_to_the_admitted_member() {
        let (registry, member, records, root) = crate::admitted_databases::tests::fixture();
        let host = ProductionTbepHost::new(Arc::new(registry));
        let response =
            host.bind_channel(&registry::ChannelBindRequest { scope: scope(&records[0]) });
        assert!(response.is_ok());
        drop(host);
        member.close(std::time::Duration::from_secs(1)).unwrap();
        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn refuses_wrong_scope_and_draining_registry() {
        let (registry, member, records, root) = crate::admitted_databases::tests::fixture();
        let host = ProductionTbepHost::new(Arc::new(registry));
        let mut wrong = scope(&records[0]);
        wrong.tenant = records[1].tenant_id.clone();
        assert!(matches!(
            host.bind_channel(&registry::ChannelBindRequest { scope: wrong }),
            Err(terrabase_tbep::HostingError::Refused(_))
        ));
        host.registry.begin_drain().unwrap();
        assert!(matches!(
            host.bind_channel(&registry::ChannelBindRequest { scope: scope(&records[0]) }),
            Err(terrabase_tbep::HostingError::Refused(_))
        ));
        drop(host);
        member.close(std::time::Duration::from_secs(1)).unwrap();
        std::fs::remove_dir_all(root).unwrap();
    }
}
