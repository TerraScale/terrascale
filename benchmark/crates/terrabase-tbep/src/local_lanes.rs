//! Local-composition replication lane observer.
//!
//! The standalone (local development) composition enumerates static
//! cross-region lanes from its configuration. The lanes stay `Unknown` —
//! never `Healthy` — until an authoritative observation exists, so this
//! module dials each configured peer over the real mTLS TBEP listener and
//! issues an authenticated `MaintenanceService.TopologyRead` call. A lane's
//! frontier becomes `Healthy` only on an admitted response; replication and
//! membership components become `Healthy` only when every configured lane
//! is healthy. Any failure marks the lane `Degraded`, which keeps readiness
//! fail-closed: the member never claims a mesh it cannot observe.

use std::collections::BTreeMap;
use std::sync::Arc;
use std::time::Duration;

use terrabase::observability::{Component, HealthStatus, MetricsRegistry, Region};
use tokio::sync::watch;
use tonic::Request;
use tonic::transport::{Certificate, Channel, ClientTlsConfig, Identity};

use crate::proto::terrabase::v1::MaintenanceServiceTopologyReadRequest;
use crate::proto::terrabase::v1::maintenance_service_client::MaintenanceServiceClient;

/// Probe cadence while a lane answers the authenticated health call.
const HEALTHY_INTERVAL: Duration = Duration::from_secs(10);
/// Probe cadence while a lane is not yet observed healthy.
const PROBE_INTERVAL: Duration = Duration::from_secs(2);
/// Bound on one probe's connect-plus-RPC time.
const PROBE_TIMEOUT: Duration = Duration::from_secs(5);

/// One statically configured cross-region lane to observe.
#[derive(Clone)]
pub struct Lane {
    /// The peer's region label for frontier metrics.
    pub region: Region,
    /// The peer's mTLS TBEP authority (`host:port`).
    pub authority: String,
}

/// Inputs for [`spawn_lanes`]. PEM material is the member's own serving
/// certificate/key (used as the client identity) and the shared client CA.
#[derive(Clone)]
pub struct LaneInputs {
    /// This member's admitted member principal (`spiffe://…`).
    pub principal: String,
    /// The engine scope every probe binds (`x-tb-tenant`).
    pub tenant: String,
    /// The engine scope's project identifier.
    pub project: String,
    /// The engine scope's database identifier.
    pub database: String,
    /// PEM certificate chain presented to peers.
    pub certificate_pem: Vec<u8>,
    /// PEM private key for the certificate chain.
    pub private_key_pem: Vec<u8>,
    /// PEM CA bundle used to verify peers.
    pub client_ca_pem: Vec<u8>,
}

/// Spawns one observer task per lane. Tasks stop when `shutdown` fires.
/// The function never fails: an unobservable TLS setup keeps the lane
/// `Unknown` (never ready) instead of aborting the member.
pub fn spawn_lanes(
    lanes: Vec<Lane>,
    inputs: &LaneInputs,
    metrics: &Arc<MetricsRegistry>,
    shutdown: &watch::Receiver<bool>,
) {
    let lane_count = lanes.len();
    let healthy = Arc::new(std::sync::Mutex::new(BTreeMap::<Region, bool>::new()));
    for lane in lanes {
        tokio::spawn(observe_lane(
            lane,
            lane_count,
            inputs.clone(),
            Arc::clone(metrics),
            Arc::clone(&healthy),
            shutdown.clone(),
        ));
    }
}

async fn observe_lane(
    lane: Lane,
    lane_count: usize,
    inputs: LaneInputs,
    metrics: Arc<MetricsRegistry>,
    healthy: Arc<std::sync::Mutex<BTreeMap<Region, bool>>>,
    mut shutdown: watch::Receiver<bool>,
) {
    let domain = lane
        .authority
        .rsplit_once(':')
        .map_or_else(|| lane.authority.clone(), |(host, _)| host.to_owned());
    let endpoint = Channel::from_shared(format!("https://{}", lane.authority))
        .map_err(|_| ())
        .and_then(|endpoint| {
            endpoint
                .tls_config(
                    ClientTlsConfig::new()
                        .ca_certificate(Certificate::from_pem(&inputs.client_ca_pem))
                        .identity(Identity::from_pem(
                            &inputs.certificate_pem,
                            &inputs.private_key_pem,
                        ))
                        .domain_name(domain),
                )
                .map_err(|_| ())
        })
        .map(|endpoint| endpoint.connect_timeout(PROBE_TIMEOUT).connect_lazy());
    let Ok(endpoint) = endpoint else {
        // An unusable TLS/endpoint setup can never become an observation:
        // the lane stays Unknown and readiness stays false.
        let _ = shutdown.changed().await;
        return;
    };
    loop {
        let observed = probe(&endpoint, &inputs).await;
        metrics.set_replication_frontier_health(
            lane.region,
            if observed { HealthStatus::Healthy } else { HealthStatus::Degraded },
        );
        let (observed_count, healthy_count) = {
            let mut map = healthy.lock().expect("the lane map is not poisoned");
            map.insert(lane.region, observed);
            (map.len(), map.values().filter(|&&ok| ok).count())
        };
        let component = if observed_count == lane_count && healthy_count == lane_count {
            HealthStatus::Healthy
        } else {
            HealthStatus::Degraded
        };
        metrics.set_component_health(Component::Replication, component);
        metrics.set_component_health(Component::Membership, component);
        tokio::select! {
            () = tokio::time::sleep(if observed { HEALTHY_INTERVAL } else { PROBE_INTERVAL }) => {}
            _ = shutdown.changed() => return,
        }
    }
}

/// One authenticated health probe. `false` covers every failure shape:
/// transport, TLS, admission, and timeout; no failure is cached.
async fn probe(endpoint: &Channel, inputs: &LaneInputs) -> bool {
    let Ok(principal) = inputs.principal.parse() else {
        return false;
    };
    let Ok(tenant) = inputs.tenant.parse() else {
        return false;
    };
    let Ok(project) = inputs.project.parse() else {
        return false;
    };
    let Ok(database) = inputs.database.parse() else {
        return false;
    };
    let mut client = MaintenanceServiceClient::new(endpoint.clone());
    let mut request = Request::new(MaintenanceServiceTopologyReadRequest {});
    let metadata = request.metadata_mut();
    metadata.insert("x-tb-principal", principal);
    metadata.insert("x-tb-tenant", tenant);
    metadata.insert("x-tb-project", project);
    metadata.insert("x-tb-database", database);
    matches!(tokio::time::timeout(PROBE_TIMEOUT, client.topology_read(request)).await, Ok(Ok(_)))
}

#[cfg(test)]
mod tests {
    use super::*;

    use terrabase::observability::{
        Component, HealthStatus, MetricsRegistry, Region, ServiceState,
    };

    use crate::proto::terrabase::v1 as pb;
    use crate::{TbepHost, engine_host::EngineHost, grpc::TbepGrpcConfig, grpc::TbepGrpcServer};

    const PRINCIPAL: &str = "spiffe://local/stack-member";
    const TENANT: &str = "terrabase-local";
    const PROJECT: &str = "local";
    const DATABASE: &str = "local";

    fn stack_material() -> (Vec<u8>, Vec<u8>, Vec<u8>) {
        use rcgen::{CertificateParams, Ia5String, KeyPair, SanType};

        let issuer_key = KeyPair::generate().expect("the CA key generates");
        let mut issuer = CertificateParams::new(Vec::new()).expect("the CA parameters parse");
        issuer.is_ca = rcgen::IsCa::Ca(rcgen::BasicConstraints::Unconstrained);
        let issuer = issuer.self_signed(&issuer_key).expect("the CA signs");

        let key = KeyPair::generate().expect("the member key generates");
        let mut params =
            CertificateParams::new(vec!["127.0.0.1".into()]).expect("the member parameters parse");
        params.subject_alt_names = vec![
            SanType::IpAddress(std::net::IpAddr::V4(std::net::Ipv4Addr::LOCALHOST)),
            SanType::URI(Ia5String::try_from(PRINCIPAL).expect("the member principal is IA5 text")),
        ];
        let member =
            params.signed_by(&key, &issuer, &issuer_key).expect("the member certificate signs");
        (member.pem().into_bytes(), key.serialize_pem().into_bytes(), issuer.pem().into_bytes())
    }

    #[tokio::test(flavor = "current_thread")]
    async fn lane_observer_admits_a_real_peer_and_fails_closed_when_it_stops() {
        let (certificate, private_key, client_ca) = stack_material();
        let host = Arc::new(
            EngineHost::open(&tempdir(), 1, TENANT, PROJECT, DATABASE).expect("the engine opens"),
        );
        let metrics = host.metrics();
        metrics.set_component_health(Component::Metrics, HealthStatus::Healthy);
        metrics.set_component_health(Component::Storage, HealthStatus::Healthy);
        let config = TbepGrpcConfig::try_new(
            crate::registry::MAX_FRAME_BYTES,
            128,
            std::time::Duration::from_secs(30),
            std::time::Duration::from_secs(5),
            std::time::Duration::from_secs(5),
            std::collections::BTreeSet::from([PRINCIPAL.to_owned()]),
        )
        .expect("the serving config validates");
        let server = Arc::new(TbepGrpcServer::with_config_and_metrics(
            Arc::clone(&host) as Arc<dyn TbepHost>,
            pb::BindingKind::NetworkMtls,
            config,
            Region::Gru,
            Arc::new(MetricsRegistry::new()),
        ));
        let certificate = String::from_utf8(certificate).expect("the certificate is PEM text");
        let private_key = String::from_utf8(private_key).expect("the key is PEM text");
        let client_ca = String::from_utf8(client_ca).expect("the CA is PEM text");
        let mut listener = server
            .serve_tls(
                std::net::SocketAddr::from(([127, 0, 0, 1], 0)),
                &certificate,
                &private_key,
                Some(&client_ca),
            )
            .expect("the TBEP listener serves");
        let port = listener.local_addr().expect("the listener exposes its address").port();

        let registry = host.metrics();
        metrics.transition_service(ServiceState::Serving).expect("the transition is valid");
        let (_shutdown, signal) = watch::channel(false);
        let (cert_bytes, key_bytes, ca_bytes) =
            (certificate.into_bytes(), private_key.into_bytes(), client_ca.into_bytes());
        spawn_lanes(
            vec![Lane { region: Region::Sfo, authority: format!("127.0.0.1:{port}") }],
            &LaneInputs {
                principal: PRINCIPAL.to_owned(),
                tenant: TENANT.to_owned(),
                project: PROJECT.to_owned(),
                database: DATABASE.to_owned(),
                certificate_pem: cert_bytes,
                private_key_pem: key_bytes,
                client_ca_pem: ca_bytes,
            },
            &registry,
            &signal,
        );

        let deadline = std::time::Instant::now() + std::time::Duration::from_secs(15);
        while !registry.snapshot().is_ready() {
            assert!(
                std::time::Instant::now() < deadline,
                "the lane never became healthy; readiness stayed {}",
                registry.snapshot().is_ready()
            );
            tokio::time::sleep(Duration::from_millis(200)).await;
        }

        // Stop accepting without the graceful wait: the observer's pooled
        // HTTP/2 connection would otherwise hold the listener's grace open.
        // The observer keeps probing and must observe the loss fail-closed.
        listener.shutdown();
        let deadline = std::time::Instant::now() + std::time::Duration::from_secs(20);
        while registry.snapshot().is_ready() {
            assert!(
                std::time::Instant::now() < deadline,
                "readiness stayed true after the peer stopped"
            );
            tokio::time::sleep(Duration::from_millis(200)).await;
        }
    }

    fn tempdir() -> std::path::PathBuf {
        let dir = std::env::temp_dir().join(format!("tbep-lanes-{}", std::process::id()));
        std::fs::create_dir_all(&dir).expect("the temporary engine directory creates");
        dir
    }
}
