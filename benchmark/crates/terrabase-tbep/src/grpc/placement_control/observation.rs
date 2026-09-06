//! Read-only observation of the pending host's actual control lifecycle.

use super::*;
use pb::maintenance_service_server::MaintenanceService;

impl PlacementControlServer {
    fn authenticate_observer<T>(&self, request: &Request<T>) -> Result<(), Status> {
        // Existing observers carry a complete bound identity. Validate it without
        // interpreting that scope as an admitted database or placement authority.
        let identity = super::super::Identity::of(request)?;
        if !self.observer_principals.contains(&identity.principal) {
            return Err(control_status(
                Code::PermissionDenied,
                "principal is not an admitted observer",
            ));
        }
        let certificates = request.peer_certs().ok_or_else(|| {
            control_status(Code::Unauthenticated, "host observation requires mTLS")
        })?;
        let leaf = certificates.first().ok_or_else(|| {
            control_status(Code::Unauthenticated, "host observation requires mTLS")
        })?;
        authenticate_gateway_certificate(&identity.principal, leaf.as_ref())?;
        Ok(())
    }

    fn observed_frame(&self) -> u64 {
        self.observation_frames.fetch_add(1, Ordering::Relaxed).saturating_add(1)
    }

    fn observation_state(&self) -> pb::HealthState {
        if self.draining.load(Ordering::Acquire) {
            pb::HealthState::Draining
        } else {
            // The placement journal has no engine membership or data service.
            pb::HealthState::Starting
        }
    }
}

#[tonic::async_trait]
impl MaintenanceService for Arc<PlacementControlServer> {
    async fn catch_up_status(
        &self,
        request: Request<pb::MaintenanceServiceCatchUpStatusRequest>,
    ) -> Result<Response<pb::MaintenanceServiceCatchUpStatusResponse>, Status> {
        self.authenticate_observer(&request)?;
        self.observed_frame();
        let Some(source) = &self.catchup else {
            return Ok(Response::new(pb::MaintenanceServiceCatchUpStatusResponse::default()));
        };
        let response = source
            .observe()
            .map_err(|_| control_status(Code::Unavailable, "catchup observation unavailable"))?;
        Ok(Response::new(response))
    }

    async fn topology_read(
        &self,
        request: Request<pb::MaintenanceServiceTopologyReadRequest>,
    ) -> Result<Response<pb::MaintenanceServiceTopologyReadResponse>, Status> {
        self.authenticate_observer(&request)?;
        self.observed_frame();
        Ok(Response::new(pb::MaintenanceServiceTopologyReadResponse {
            binding: pb::BindingKind::NetworkMtls as i32,
            subsystems: Vec::new(),
            engine_topology_available: false,
            cluster_mode: pb::ClusterMode::Unspecified as i32,
        }))
    }

    async fn admin_status(
        &self,
        request: Request<pb::MaintenanceServiceAdminStatusRequest>,
    ) -> Result<Response<pb::MaintenanceServiceAdminStatusResponse>, Status> {
        self.authenticate_observer(&request)?;
        Ok(Response::new(pb::MaintenanceServiceAdminStatusResponse {
            registry_version: String::new(),
            frames_served: self.observed_frame(),
            sessions: 0,
            subscriptions: 0,
            scopes: 0,
        }))
    }

    async fn supervision_health(
        &self,
        request: Request<pb::MaintenanceServiceSupervisionHealthRequest>,
    ) -> Result<Response<pb::MaintenanceServiceSupervisionHealthResponse>, Status> {
        self.authenticate_observer(&request)?;
        Ok(Response::new(pb::MaintenanceServiceSupervisionHealthResponse {
            state: self.observation_state() as i32,
            sessions: 0,
            subscriptions: 0,
            frames_served: self.observed_frame(),
        }))
    }

    async fn supervision_shutdown(
        &self,
        request: Request<pb::MaintenanceServiceSupervisionShutdownRequest>,
    ) -> Result<Response<pb::MaintenanceServiceSupervisionShutdownResponse>, Status> {
        self.authenticate_observer(&request)?;
        Err(control_status(Code::PermissionDenied, "observation does not grant shutdown authority"))
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::placement::tests::Fixture;
    use pb::maintenance_service_client::MaintenanceServiceClient;
    use tonic::transport::{Certificate, ClientTlsConfig, Endpoint, Identity};

    const OBSERVER: &str = "spiffe://terrascale.tech/production/engine-observer/gru-1";
    const OTHER_OBSERVER: &str = "spiffe://terrascale.tech/production/engine-observer/gru-2";
    const ORCHESTRATOR: &str = "spiffe://terrascale.tech/production/orchestrator/gru-1";

    fn request<T>(message: T, principal: &str) -> Request<T> {
        let mut request = Request::new(message);
        for (key, value) in [
            (super::super::super::PRINCIPAL_METADATA, principal),
            (super::super::super::TENANT_METADATA, "operations"),
            (super::super::super::PROJECT_METADATA, "operations"),
            (super::super::super::DATABASE_METADATA, "observation"),
        ] {
            request.metadata_mut().insert(key, value.parse().unwrap());
        }
        request
    }

    #[tokio::test]
    async fn real_mtls_observation_is_pending_scoped_and_read_only() {
        use rcgen::{
            BasicConstraints, CertificateParams, ExtendedKeyUsagePurpose, Ia5String, IsCa, KeyPair,
            SanType,
        };
        let ca_key = KeyPair::generate().unwrap();
        let mut params = CertificateParams::new(Vec::new()).unwrap();
        params.is_ca = IsCa::Ca(BasicConstraints::Unconstrained);
        let ca = params.self_signed(&ca_key).unwrap();
        let server_key = KeyPair::generate().unwrap();
        let mut params = CertificateParams::new(vec!["localhost".into()]).unwrap();
        params.extended_key_usages = vec![ExtendedKeyUsagePurpose::ServerAuth];
        let certificate = params.signed_by(&server_key, &ca, &ca_key).unwrap();
        let client_key = KeyPair::generate().unwrap();
        let mut params = CertificateParams::new(Vec::new()).unwrap();
        params.extended_key_usages = vec![ExtendedKeyUsagePurpose::ClientAuth];
        params.subject_alt_names = vec![SanType::URI(Ia5String::try_from(OBSERVER).unwrap())];
        let client_certificate = params.signed_by(&client_key, &ca, &ca_key).unwrap();
        let fixture = Fixture::new();
        let config = TbepGrpcConfig::try_new(
            256 * 1024,
            8,
            Duration::from_secs(2),
            Duration::from_secs(2),
            Duration::from_secs(2),
            [ORCHESTRATOR.to_owned()].into(),
        )
        .unwrap();
        let server = Arc::new(
            PlacementControlServer::new(
                Arc::new(fixture.open()),
                Arc::new(fixture.open_auth()),
                config,
                [OBSERVER.to_owned(), OTHER_OBSERVER.to_owned()].into(),
            )
            .unwrap(),
        );
        let listener = server
            .bind_tls(
                "127.0.0.1:0".parse().unwrap(),
                &certificate.pem(),
                &server_key.serialize_pem(),
                &ca.pem(),
            )
            .await
            .unwrap();
        let address = listener.local_addr().unwrap();
        let (stop, signal) = tokio::sync::oneshot::channel::<()>();
        let serving = tokio::spawn(listener.serve(async {
            let _ = signal.await;
        }));
        let channel = Endpoint::from_shared(format!("https://{address}"))
            .unwrap()
            .tls_config(
                ClientTlsConfig::new()
                    .domain_name("localhost")
                    .ca_certificate(Certificate::from_pem(ca.pem()))
                    .identity(Identity::from_pem(
                        client_certificate.pem(),
                        client_key.serialize_pem(),
                    )),
            )
            .unwrap()
            .connect()
            .await
            .unwrap();
        let mut client = MaintenanceServiceClient::new(channel.clone());
        let health = client
            .supervision_health(request(
                pb::MaintenanceServiceSupervisionHealthRequest {},
                OBSERVER,
            ))
            .await
            .unwrap()
            .into_inner();
        assert_eq!(health.state, pb::HealthState::Starting as i32);
        assert_eq!((health.sessions, health.subscriptions, health.frames_served), (0, 0, 1));
        let topology = client
            .topology_read(request(pb::MaintenanceServiceTopologyReadRequest {}, OBSERVER))
            .await
            .unwrap()
            .into_inner();
        assert!(!topology.engine_topology_available);
        assert!(topology.subsystems.is_empty());
        assert_eq!(topology.cluster_mode, pb::ClusterMode::Unspecified as i32);
        let admin = client
            .admin_status(request(pb::MaintenanceServiceAdminStatusRequest {}, OBSERVER))
            .await
            .unwrap()
            .into_inner();
        assert!(admin.registry_version.is_empty());
        assert_eq!(
            (admin.scopes, admin.sessions, admin.subscriptions, admin.frames_served),
            (0, 0, 0, 3)
        );
        assert_eq!(
            client
                .supervision_shutdown(request(
                    pb::MaintenanceServiceSupervisionShutdownRequest {
                        reason: pb::ShutdownReason::Operator as i32,
                    },
                    OBSERVER
                ))
                .await
                .unwrap_err()
                .code(),
            Code::PermissionDenied
        );
        assert!(!server.draining.load(Ordering::Acquire));
        assert_eq!(
            client
                .admin_status(request(pb::MaintenanceServiceAdminStatusRequest {}, ORCHESTRATOR))
                .await
                .unwrap_err()
                .code(),
            Code::PermissionDenied
        );
        assert_eq!(
            client
                .admin_status(request(pb::MaintenanceServiceAdminStatusRequest {}, OTHER_OBSERVER))
                .await
                .unwrap_err()
                .code(),
            Code::PermissionDenied,
        );
        let mut malformed = request(pb::MaintenanceServiceAdminStatusRequest {}, OBSERVER);
        malformed.metadata_mut().remove(super::super::super::DATABASE_METADATA);
        assert_eq!(client.admin_status(malformed).await.unwrap_err().code(), Code::Unauthenticated);
        let mut placement =
            pb::placement_control_service_client::PlacementControlServiceClient::new(channel);
        let mut mutation = Request::new(fixture.request.clone());
        mutation
            .metadata_mut()
            .insert(super::super::super::PRINCIPAL_METADATA, OBSERVER.parse().unwrap());
        mutation
            .metadata_mut()
            .insert(super::super::super::IDEMPOTENCY_METADATA, "forbidden".parse().unwrap());
        assert_eq!(placement.stage(mutation).await.unwrap_err().code(), Code::PermissionDenied);
        server.begin_drain();
        let health = client
            .supervision_health(request(
                pb::MaintenanceServiceSupervisionHealthRequest {},
                OBSERVER,
            ))
            .await
            .unwrap()
            .into_inner();
        assert_eq!(health.state, pb::HealthState::Draining as i32);
        let catchup = client
            .catch_up_status(request(pb::MaintenanceServiceCatchUpStatusRequest {}, OBSERVER))
            .await
            .unwrap()
            .into_inner();
        assert!(!catchup.complete);
        assert!(catchup.databases.is_empty());
        assert_eq!(catchup.observed_at_unix_ms, 0);
        assert!(catchup.assignment_revision.is_empty());
        stop.send(()).unwrap();
        serving.await.unwrap().unwrap();
    }
}
