//! Production daemon composition and shutdown.

use std::error::Error;
use std::fs::{File, OpenOptions};
use std::io::Read;
use std::path::Path;
use std::sync::Arc;

use terrabase::observability::Component;
use terrabase::observability::HealthStatus;
use terrabase::observability::ServiceState;
use terrabase_tbep::TbepHost;
use terrabase_tbep::engine_host::EngineHost;
use terrabase_tbep::grpc::TbepGrpcConfig;
use terrabase_tbep::grpc::TbepGrpcHandle;
use terrabase_tbep::grpc::TbepGrpcServer;
use terrabase_tbep::proto::terrabase::v1 as pb;
use terrabase_tbep::registry::ShutdownReason;
use terrabase_tbep::registry::SupervisionShutdownRequest;
use tokio::sync::oneshot;

use crate::config::{ServerConfig, ServerMode};
use crate::production::ProductionStartupError;

const MAX_PEM_BYTES: u64 = 1024 * 1024;
const MAX_PEM_BYTES_USIZE: usize = 1024 * 1024;

/// Opens the configured engine and runs every production listener until a
/// termination signal begins bounded graceful drain.
///
/// # Errors
///
/// Returns an error when TLS material, the engine, a listener, shutdown, or
/// graceful draining fails.
#[allow(clippy::too_many_lines, reason = "one listener per block, one drain")]
pub async fn run(config: ServerConfig) -> Result<(), Box<dyn Error + Send + Sync>> {
    if config.mode == ServerMode::Production {
        return run_production(config).await;
    }
    let grpc_config = TbepGrpcConfig::try_new(
        config.max_message_bytes,
        config.max_inflight,
        config.request_timeout,
        config.tls_handshake_timeout,
        config.shutdown_grace,
        config.gateway_principals.clone(),
    )?;
    let certificate = read_pem(&config.server_certificate, false)?;
    let private_key = read_pem(&config.server_private_key, true)?;
    let client_ca = read_pem(&config.client_ca, false)?;

    let host = Arc::new(
        EngineHost::open(
            &config.engine_location,
            config.instance_id,
            &config.tenant,
            &config.project,
            &config.database,
        )
        .map_err(|error| DaemonError::Engine(error.detail()))?,
    );
    let metrics = host.metrics();
    metrics.set_component_health(Component::Metrics, HealthStatus::Healthy);
    metrics.set_component_health(Component::Storage, HealthStatus::Healthy);

    // The static peer list enumerates this member's cross-region lanes
    // (SHORT_GOAL section 1): region code plus replication endpoint, fail
    // closed when the configuration names none. The lanes are inert until
    // the S-09 member composition spawns the transports this plan names;
    // until then each configured peer's frontier reports Unknown — never
    // Healthy — so readiness keeps telling the truth.
    for lane in config.replication_lanes() {
        metrics.set_replication_frontier_health(lane.region, HealthStatus::Unknown);
    }

    let network = Arc::new(TbepGrpcServer::with_config_and_metrics(
        Arc::clone(&host) as Arc<dyn TbepHost>,
        pb::BindingKind::NetworkMtls,
        grpc_config.clone(),
        config.region,
        Arc::clone(&metrics),
    ));
    let mut network_handle =
        network.serve_tls(config.tbep_listen, &certificate, &private_key, Some(&client_ca))?;

    let mut uds_handle = if let Some(uds) = &config.uds {
        validate_uds_parent(&uds.path)?;
        let local = Arc::new(TbepGrpcServer::with_config_and_metrics(
            Arc::clone(&host) as Arc<dyn TbepHost>,
            pb::BindingKind::Local,
            grpc_config,
            config.region,
            Arc::clone(&metrics),
        ));
        let handle = local.serve_uds(&uds.path)?;
        set_socket_mode(&uds.path, uds.mode)?;
        Some(handle)
    } else {
        None
    };

    let (api_task, api_stop) = serve_public_api(
        &config,
        Arc::clone(&metrics),
        config.tenant.clone(),
        config.project.clone(),
    )
    .await?;

    let observability = tokio::net::TcpListener::bind(config.observability_listen).await?;
    let (http_stop, http_signal) = oneshot::channel::<()>();
    let app = crate::http::router(Arc::clone(&metrics));
    let http_task = tokio::spawn(async move {
        axum::serve(observability, app)
            .with_graceful_shutdown(async {
                let _ = http_signal.await;
            })
            .await
    });

    // A standalone EngineHost cannot assert replication or membership health.
    // Those required components remain Unknown, so readiness stays false until
    // the cluster composition supplies authoritative observations.
    metrics.transition_service(ServiceState::Serving)?;
    wait_for_shutdown().await?;
    metrics.transition_service(ServiceState::Draining)?;
    let _ = host.begin_shutdown(&SupervisionShutdownRequest { reason: ShutdownReason::Supervisor });
    network.begin_drain();
    if let Some(handle) = &uds_handle {
        // Keep the local administrative lane from admitting new work while
        // the shared host finishes already-admitted calls.
        handle.begin_drain();
    }
    let _ = http_stop.send(());
    let _ = api_stop.send(());
    let grpc_shutdown = tokio::task::spawn_blocking(move || -> std::io::Result<()> {
        network_handle.shutdown_and_wait()?;
        if let Some(handle) = &mut uds_handle {
            handle.shutdown_and_wait()?;
        }
        Ok(())
    });
    let drain = async {
        grpc_shutdown
            .await
            .map_err(|error| -> Box<dyn Error + Send + Sync> { Box::new(error) })?
            .map_err(|error| -> Box<dyn Error + Send + Sync> { Box::new(error) })?;
        match http_task.await {
            Ok(result) => {
                result.map_err(|error| -> Box<dyn Error + Send + Sync> { Box::new(error) })?;
            }
            Err(error) => return Err(Box::new(error) as Box<dyn Error + Send + Sync>),
        }
        if let Some(task) = api_task {
            task.await
                .map_err(|error| -> Box<dyn Error + Send + Sync> { Box::new(error) })?
                .map_err(|error| -> Box<dyn Error + Send + Sync> { Box::new(error) })?;
        }
        Ok::<(), Box<dyn Error + Send + Sync>>(())
    };
    match tokio::time::timeout(config.shutdown_grace, drain).await {
        Ok(Ok(())) => {}
        Ok(Err(error)) => return Err(error),
        Err(_) => return Err(Box::new(DaemonError::ShutdownTimeout)),
    }
    metrics.transition_service(ServiceState::Stopped)?;
    Ok(())
}

/// Opens the production catalog and performs the production-only startup
/// checks before any serving claim is made.
///
/// The storage/catalog half of the production process is available today. The
/// remaining Member-host adapter is intentionally an explicit seam: silently
/// opening one compatibility [`EngineHost`] here would create a second
/// storage authority and could acknowledge writes without the RF=2 data
/// plane. Until that adapter is supplied, startup fails with its component
/// name after opening (and dropping) the real catalog.
async fn run_production(config: ServerConfig) -> Result<(), Box<dyn Error + Send + Sync>> {
    let production =
        config.production.as_ref().ok_or(DaemonError::ProductionConfigurationMissing)?;

    // This is the production storage boundary. It opens every enabled
    // database below one root and verifies the route and placement catalogs;
    // it must happen before a listener is allowed to claim readiness.
    let bootstrap = crate::production::ProductionCatalogBootstrap::open(production)
        .map_err(|error| DaemonError::ProductionStartup(ProductionStartupError::Catalog(error)))?;
    let mut parts = bootstrap.into_parts();
    let transport = match build_production_transport(production) {
        Ok(transport) => transport,
        Err(error) => {
            let _ = parts.member.close(config.shutdown_grace);
            return Err(Box::new(DaemonError::ProductionStartup(
                ProductionStartupError::Transport(error),
            )));
        }
    };
    let member_for_cleanup = parts.member.clone();
    let member = match terrabase_cluster::production::ProductionMemberRuntime::try_new(
        parts.placement.clone(),
        parts.routes.clone(),
        parts.member,
        transport,
    ) {
        Ok(member) => member,
        Err(error) => {
            let _ = member_for_cleanup.close(config.shutdown_grace);
            return Err(Box::new(DaemonError::ProductionStartup(ProductionStartupError::Member(
                error,
            ))));
        }
    };

    // Membership is deliberately not inferred from the static file. The
    // orchestrator's durable admission record is the authority; until its
    // source is wired, close the already-opened endpoint and catalog and
    // identify that one missing dependency.
    let _ = member.drain().await;
    Err(Box::new(DaemonError::ProductionStartup(ProductionStartupError::Component(
        crate::production::ProductionComponentError {
            component: crate::production::ProductionComponent::Membership,
            detail: "no durable orchestrator membership source is configured",
        },
    ))))
}

fn build_production_transport(
    config: &crate::config::ProductionConfig,
) -> Result<terrabase_cluster::production::ProductionQuicEndpoint, String> {
    use terrabase_cluster::production::{
        MemberWorkloadIdentity, ProductionPeerAdmission, ProductionQuicEndpoint,
        ProductionTlsMaterial, ProductionTransportConfig, ProductionTransportLimits,
    };

    let local = MemberWorkloadIdentity::try_new(
        config.environment.name.as_str(),
        config.environment.cluster.to_text().as_str(),
        config.environment.member.to_text().as_str(),
    )
    .map_err(|error| format!("local workload identity: {error:?}"))?;
    let local_region = production_region(
        config
            .databases
            .iter()
            .find(|db| db.enabled)
            .map(|db| db.region)
            .ok_or("no enabled database")?,
    )?;
    let mut peers = Vec::with_capacity(config.transport.peers.len());
    for peer in &config.transport.peers {
        let identity = MemberWorkloadIdentity::parse(&peer.spiffe_identity)
            .map_err(|error| format!("peer workload identity: {error:?}"))?;
        let region = config
            .placement
            .regions
            .iter()
            .find_map(|(region, members)| members.contains(&peer.member).then_some(*region))
            .ok_or_else(|| format!("peer {} has no placement Region", peer.member))?;
        let region = production_region(region)?;
        let server_name = peer.endpoint.ip().to_string();
        peers.push(
            ProductionPeerAdmission::try_new(identity, region, peer.endpoint, &server_name)
                .map_err(|error| format!("peer transport: {error:?}"))?,
        );
    }
    // Production transport limits are fixed policy, not customer-provided
    // request settings. Keep the member boundary bounded and fail closed in
    // the transport constructor if these constants ever violate its caps.
    let max_frame_bytes: usize = 16 * 1024 * 1024;
    let max_inflight: usize = 1024;
    let max_queued_bytes =
        max_frame_bytes.saturating_mul(max_inflight).min(1024 * 1024 * 1024).max(max_frame_bytes);
    let limits = ProductionTransportLimits::try_new(
        max_frame_bytes,
        max_inflight,
        256,
        max_queued_bytes,
        std::time::Duration::from_secs(30),
        std::time::Duration::from_secs(30),
        std::time::Duration::from_mins(1),
        std::time::Duration::from_mins(5),
    )
    .map_err(|error| format!("transport limits: {error:?}"))?;
    let transport = ProductionTransportConfig::try_new(
        config.transport.quic_listen,
        local,
        local_region,
        config.placement.generation,
        config.placement.epoch,
        peers,
        limits,
    )
    .map_err(|error| format!("transport configuration: {error:?}"))?;
    let certificate =
        read_pem(&config.transport.server_certificate, false).map_err(|error| error.to_string())?;
    let private_key =
        read_pem(&config.transport.server_private_key, true).map_err(|error| error.to_string())?;
    let client_ca =
        read_pem(&config.transport.client_ca, false).map_err(|error| error.to_string())?;
    let tls = ProductionTlsMaterial::from_pem(
        certificate.as_bytes(),
        private_key.as_bytes(),
        client_ca.as_bytes(),
    )
    .map_err(|error| format!("TLS material: {error:?}"))?;
    ProductionQuicEndpoint::bind(transport, tls).map_err(|error| format!("QUIC bind: {error:?}"))
}

fn production_region(
    region: terrabase::regions::RegionCode,
) -> Result<terrabase_cluster::production::ProductionRegion, String> {
    match region.as_str() {
        "GRU" => Ok(terrabase_cluster::production::ProductionRegion::Gru),
        "SFO" => Ok(terrabase_cluster::production::ProductionRegion::Sfo),
        "FRA" => Ok(terrabase_cluster::production::ProductionRegion::Fra),
        _ => Err(format!("unsupported production Region {region}")),
    }
}

async fn wait_for_shutdown() -> Result<(), std::io::Error> {
    #[cfg(unix)]
    {
        let mut terminate =
            tokio::signal::unix::signal(tokio::signal::unix::SignalKind::terminate())?;
        tokio::select! {
            result = tokio::signal::ctrl_c() => result,
            _ = terminate.recv() => Ok(()),
        }
    }
    #[cfg(not(unix))]
    {
        tokio::signal::ctrl_c().await
    }
}

/// Binds and serves the public `/api/v1` surface when the configuration
/// names it. The data root carries the control-plane engine and the
/// database catalog; the bootstrap key file, when present, registers one
/// all-verbs credential (the CLI that wrote it is the only other holder),
/// and an absent file leaves the surface deny-all.
///
/// Returns the serve task and its drain signal; both `None` when the
/// deployment binds no public surface.
async fn serve_public_api(
    config: &ServerConfig,
    metrics: Arc<terrabase::observability::MetricsRegistry>,
    tenant: String,
    project: String,
) -> Result<
    (Option<tokio::task::JoinHandle<Result<(), std::io::Error>>>, oneshot::Sender<()>),
    Box<dyn Error + Send + Sync>,
> {
    let (stop, signal) = oneshot::channel::<()>();
    let Some(public) = config.public_api.clone() else {
        return Ok((None, stop));
    };
    std::fs::create_dir_all(&public.data_root)?;
    let control = crate::control::ControlPlane::open(&public.data_root)
        .map_err(|error| DaemonError::Engine(format!("{error:?}")))?;
    // ponytail: fixed catalog bounds (64 open databases, 5-minute idle
    // close) until the member budget exists (S-05).
    let catalog = terrabase_tbep::catalog::HostCatalog::open(
        &public.data_root,
        64,
        std::time::Duration::from_mins(5),
    )
    .map_err(|error| DaemonError::Engine(error.to_string()))?;
    let keys = crate::api_keys::KeyStore::empty();
    if let Some(key_file) = &public.bootstrap_key_file {
        let key = std::fs::read_to_string(key_file)?.trim().to_owned();
        if key.is_empty() {
            return Err(Box::new(DaemonError::Engine("the bootstrap key file is empty".into())));
        }
        keys.register(
            &key,
            "bootstrap",
            crate::api_keys::Scope::every_database(crate::api_keys::Verbs::ALL),
        );
    }
    let region = match config.region {
        terrabase::observability::Region::Gru => terrabase::regions::RegionCode::GRU,
        terrabase::observability::Region::Sfo => terrabase::regions::RegionCode::SFO,
        terrabase::observability::Region::Fra => terrabase::regions::RegionCode::FRA,
        terrabase::observability::Region::Unknown => {
            return Err(Box::new(DaemonError::Engine(
                "the public API needs the member's own region".into(),
            )));
        }
    };
    let backups = Arc::new(crate::backup::BackupStreams::open_member());
    let backup_root = public.data_root.join("backup");
    let timetravel = Arc::new(crate::backup::TimeTravelCache::new(
        Arc::clone(&backups),
        &backup_root.join("timetravel"),
    ));
    let origin_proof_keys = match public.origin_proof_key_file.as_deref() {
        Some(current) => {
            let current = read_secret_key(current)?;
            let previous = public
                .origin_proof_previous_key_file
                .as_deref()
                .map(read_secret_key)
                .transpose()?;
            crate::reactive_api::OriginProofKeyRing::new(current, previous)
                .ok_or_else(|| DaemonError::Engine("invalid origin-proof key ring".into()))?
        }
        None if public.origin_proof_previous_key_file.is_some() => {
            return Err(Box::new(DaemonError::Engine(
                "origin-proof previous key requires a current key".into(),
            )));
        }
        None => crate::reactive_api::OriginProofKeyRing::default(),
    };
    let capability_keys = match public.capability_key_file.as_deref() {
        Some(current) => {
            let current = read_secret_key(current)?;
            let previous =
                public.capability_previous_key_file.as_deref().map(read_secret_key).transpose()?;
            crate::reactive_api::CapabilityKeyRing::new(current, previous)
                .ok_or_else(|| DaemonError::Engine("invalid capability key ring".into()))?
        }
        None if public.capability_previous_key_file.is_some() => {
            return Err(Box::new(DaemonError::Engine(
                "capability previous key requires a current key".into(),
            )));
        }
        None => crate::reactive_api::CapabilityKeyRing::default(),
    };
    let state = Arc::new(crate::api::ApiState {
        region,
        metrics,
        keys: Some(Arc::new(keys)),
        control: Some(Arc::new(control)),
        catalog: Some(Arc::new(catalog)),
        scope: (tenant, project),
        reactive: Arc::new(crate::reactive_api::ReactiveSessions::default()),
        backups,
        backup_root,
        timetravel,
        active_regions: public.active_regions,
        guards: Arc::new(crate::guards::AbuseGuards::default()),
        origin_proof_keys: Arc::new(origin_proof_keys),
        capability_keys: Arc::new(capability_keys),
    });
    let listener = tokio::net::TcpListener::bind(public.listen).await?;
    let router = crate::api::router(state);
    let task = tokio::spawn(async move {
        axum::serve(listener, router)
            .with_graceful_shutdown(async {
                let _ = signal.await;
            })
            .await
    });
    Ok((Some(task), stop))
}

fn read_pem(path: &Path, private: bool) -> Result<String, DaemonError> {
    let mut file = open_readonly(path)?;
    let metadata = file.metadata().map_err(|_| DaemonError::SecretFile)?;
    if !metadata.is_file() || metadata.len() == 0 || metadata.len() > MAX_PEM_BYTES {
        return Err(DaemonError::SecretFile);
    }
    #[cfg(unix)]
    if private {
        use std::os::unix::fs::PermissionsExt as _;
        if metadata.permissions().mode() & 0o077 != 0 {
            return Err(DaemonError::PrivateKeyPermissions);
        }
    }
    let expected_len = usize::try_from(metadata.len()).map_err(|_| DaemonError::SecretFile)?;
    let mut bytes = Vec::with_capacity(expected_len);
    (&mut file)
        .take(MAX_PEM_BYTES.saturating_add(1))
        .read_to_end(&mut bytes)
        .map_err(|_| DaemonError::SecretFile)?;
    let after = file.metadata().map_err(|_| DaemonError::SecretFile)?;
    if bytes.len() > MAX_PEM_BYTES_USIZE
        || bytes.len() != expected_len
        || after.len() != metadata.len()
    {
        return Err(DaemonError::SecretFile);
    }
    let text = String::from_utf8(bytes).map_err(|_| DaemonError::SecretFile)?;
    if !valid_pem(&text, private) {
        return Err(DaemonError::SecretFile);
    }
    Ok(text)
}

fn open_readonly(path: &Path) -> Result<File, DaemonError> {
    let mut options = OpenOptions::new();
    options.read(true);
    #[cfg(unix)]
    {
        use std::os::unix::fs::OpenOptionsExt as _;
        #[cfg(any(target_os = "linux", target_os = "android"))]
        options.custom_flags(0o400_000); // O_NOFOLLOW
        #[cfg(any(
            target_os = "dragonfly",
            target_os = "freebsd",
            target_os = "ios",
            target_os = "macos",
            target_os = "netbsd",
            target_os = "openbsd"
        ))]
        options.custom_flags(0x100); // O_NOFOLLOW
    }
    options.open(path).map_err(|_| DaemonError::SecretFile)
}

/// Reads bounded raw HMAC key material from a private, no-follow file. Keys
/// are delivered as UTF-8 bytes so operators can rotate them through the
/// same secret-file mechanism as the rest of the daemon configuration.
fn read_secret_key(path: &Path) -> Result<Vec<u8>, DaemonError> {
    let mut file = open_readonly(path)?;
    let metadata = file.metadata().map_err(|_| DaemonError::SecretFile)?;
    if !metadata.is_file() || metadata.len() < 16 || metadata.len() > 4096 {
        return Err(DaemonError::SecretFile);
    }
    #[cfg(unix)]
    {
        use std::os::unix::fs::PermissionsExt as _;
        if metadata.permissions().mode() & 0o077 != 0 {
            return Err(DaemonError::PrivateKeyPermissions);
        }
    }
    let mut bytes =
        Vec::with_capacity(usize::try_from(metadata.len()).map_err(|_| DaemonError::SecretFile)?);
    file.read_to_end(&mut bytes).map_err(|_| DaemonError::SecretFile)?;
    if bytes.len() != usize::try_from(metadata.len()).map_err(|_| DaemonError::SecretFile)? {
        return Err(DaemonError::SecretFile);
    }
    while bytes.last().is_some_and(u8::is_ascii_whitespace) {
        bytes.pop();
    }
    if bytes.len() < 16 || bytes.iter().any(|byte| byte.is_ascii_control() || *byte > 0x7f) {
        return Err(DaemonError::SecretFile);
    }
    Ok(bytes)
}

/// Checks PEM framing and base64 syntax before the TLS parser sees the bytes.
/// The decoded DER is intentionally left to rustls, which owns certificate and
/// key semantics; this layer only enforces a bounded, structurally complete
/// PEM document without retaining another copy of the file.
fn valid_pem(text: &str, private: bool) -> bool {
    let mut blocks = 0usize;
    let mut private_block = false;
    let mut lines = text.split('\n');
    while let Some(raw_line) = lines.next() {
        let line = raw_line.strip_suffix('\r').unwrap_or(raw_line);
        if line.is_empty() {
            continue;
        }
        let Some(label) =
            line.strip_prefix("-----BEGIN ").and_then(|value| value.strip_suffix("-----"))
        else {
            return false;
        };
        if label.is_empty() || !label.bytes().all(|byte| byte.is_ascii_uppercase() || byte == b' ')
        {
            return false;
        }
        let mut encoded = String::new();
        let mut ended = false;
        for raw_body in lines.by_ref() {
            let body = raw_body.strip_suffix('\r').unwrap_or(raw_body);
            if let Some(end_label) =
                body.strip_prefix("-----END ").and_then(|value| value.strip_suffix("-----"))
            {
                if end_label != label || encoded.is_empty() {
                    return false;
                }
                ended = true;
                break;
            }
            if body.bytes().any(|byte| !byte.is_ascii_whitespace() && byte != b'=')
                && body
                    .bytes()
                    .any(|byte| !byte.is_ascii_alphanumeric() && !matches!(byte, b'+' | b'/'))
            {
                return false;
            }
            encoded.push_str(body);
        }
        if !ended || decode_base64(&encoded).is_none() {
            return false;
        }
        blocks = blocks.saturating_add(1);
        private_block |= label.contains("PRIVATE KEY");
    }
    blocks > 0 && (!private || private_block)
}

fn decode_base64(value: &str) -> Option<Vec<u8>> {
    let bytes: Vec<u8> = value.bytes().filter(|byte| !byte.is_ascii_whitespace()).collect();
    if bytes.is_empty() || !bytes.len().is_multiple_of(4) {
        return None;
    }
    let (chunks, _remainder) = bytes.as_chunks::<4>();
    let chunk_count = chunks.len();
    let mut decoded = Vec::with_capacity(bytes.len() / 4 * 3);
    for (index, chunk) in chunks.iter().enumerate() {
        let padding = if chunk[2] == b'=' {
            if chunk[3] != b'=' {
                return None;
            }
            2
        } else {
            u8::from(chunk[3] == b'=')
        };
        if padding != 0 && index + 1 != chunk_count {
            return None;
        }
        let a = base64_value(chunk[0])?;
        let b = base64_value(chunk[1])?;
        let c = if padding == 2 { 0 } else { base64_value(chunk[2])? };
        let d = if padding != 0 { 0 } else { base64_value(chunk[3])? };
        decoded.push((a << 2) | (b >> 4));
        if padding < 2 {
            decoded.push((b << 4) | (c >> 2));
        }
        if padding == 0 {
            decoded.push((c << 6) | d);
        }
    }
    Some(decoded)
}

fn base64_value(byte: u8) -> Option<u8> {
    match byte {
        b'A'..=b'Z' => Some(byte - b'A'),
        b'a'..=b'z' => Some(byte - b'a' + 26),
        b'0'..=b'9' => Some(byte - b'0' + 52),
        b'+' => Some(62),
        b'/' => Some(63),
        _ => None,
    }
}

fn validate_uds_parent(path: &Path) -> Result<(), DaemonError> {
    let parent = path.parent().ok_or(DaemonError::UnsafeUdsPath)?;
    let metadata = std::fs::symlink_metadata(parent).map_err(|_| DaemonError::UnsafeUdsPath)?;
    if metadata.file_type().is_symlink() || !metadata.is_dir() {
        return Err(DaemonError::UnsafeUdsPath);
    }
    #[cfg(unix)]
    {
        use std::os::unix::fs::PermissionsExt as _;
        if metadata.permissions().mode() & 0o002 != 0 {
            return Err(DaemonError::UnsafeUdsPath);
        }
    }
    Ok(())
}

fn set_socket_mode(path: &Path, mode: u32) -> Result<(), DaemonError> {
    #[cfg(unix)]
    {
        use std::os::unix::fs::PermissionsExt as _;
        std::fs::set_permissions(path, std::fs::Permissions::from_mode(mode))
            .map_err(|_| DaemonError::UnsafeUdsPath)?;
    }
    let _ = (path, mode);
    Ok(())
}

/// A redacted daemon startup or shutdown failure.
#[derive(Debug)]
enum DaemonError {
    Engine(String),
    SecretFile,
    PrivateKeyPermissions,
    UnsafeUdsPath,
    ShutdownTimeout,
    ProductionConfigurationMissing,
    ProductionStartup(ProductionStartupError),
}

impl std::fmt::Display for DaemonError {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::Engine(detail) => {
                write!(formatter, "the durable engine could not open: {detail}")
            }
            Self::SecretFile => formatter.write_str("a TLS material file is unsafe or invalid"),
            Self::PrivateKeyPermissions => {
                formatter.write_str("the TLS private key is accessible outside its owner")
            }
            Self::UnsafeUdsPath => formatter.write_str("the local UDS path is unsafe"),
            Self::ShutdownTimeout => formatter.write_str("graceful shutdown exceeded its deadline"),
            Self::ProductionConfigurationMissing => {
                formatter.write_str("production mode has no typed production configuration")
            }
            Self::ProductionStartup(error) => {
                write!(formatter, "production startup failed: {error}")
            }
        }
    }
}

impl Error for DaemonError {}

#[allow(dead_code)]
fn _handle_type_is_send(_: &TbepGrpcHandle) {}

#[cfg(test)]
mod tests {
    use super::*;
    use std::collections::BTreeSet;
    use std::net::SocketAddr;
    use std::sync::atomic::{AtomicUsize, Ordering};
    use terrabase::observability::Region;

    fn temporary_path(label: &str) -> std::path::PathBuf {
        static NEXT: AtomicUsize = AtomicUsize::new(0);
        std::env::temp_dir().join(format!(
            "terrabase-server-{label}-{}-{}",
            std::process::id(),
            NEXT.fetch_add(1, Ordering::Relaxed)
        ))
    }

    fn write_temporary(label: &str, bytes: &[u8]) -> std::path::PathBuf {
        let path = temporary_path(label);
        std::fs::write(&path, bytes).expect("temporary PEM writes");
        path
    }

    #[tokio::test]
    async fn configured_gateway_identity_reaches_secret_validation() {
        let config = ServerConfig {
            mode: crate::config::ServerMode::LocalDevelopment,
            production: None,
            engine_location: "/var/lib/terrabase".into(),
            tenant: "tenant-a".into(),
            project: "project-a".into(),
            database: "database-a".into(),
            instance_id: 1,
            region: Region::Gru,
            tbep_listen: SocketAddr::from(([127, 0, 0, 1], 8421)),
            server_certificate: "/missing/server.crt".into(),
            server_private_key: "/missing/server.key".into(),
            client_ca: "/missing/client-ca.crt".into(),
            gateway_principals: BTreeSet::from([
                "spiffe://terrascale.tech/prod/database-gateway/01ARZ3NDEKTSV4RRFFQ69G5FAV"
                    .to_owned(),
            ]),
            uds: None,
            public_api: None,
            replication_peers: Vec::new(),
            observability_listen: SocketAddr::from(([127, 0, 0, 1], 9464)),
            max_message_bytes: terrabase_tbep::registry::MAX_FRAME_BYTES,
            max_inflight: terrabase_tbep::grpc::MAX_INFLIGHT_OPERATIONS,
            request_timeout: std::time::Duration::from_secs(30),
            tls_handshake_timeout: std::time::Duration::from_secs(5),
            shutdown_grace: std::time::Duration::from_secs(30),
        };
        let error = run(config).await.expect_err("missing TLS material must fail closed");
        assert!(error.to_string().contains("TLS material file is unsafe or invalid"));
    }

    #[tokio::test]
    async fn production_mode_requires_typed_configuration_before_side_effects() {
        let engine_location = temporary_path("production-refused-engine");
        let config = ServerConfig {
            mode: ServerMode::Production,
            production: None,
            engine_location: engine_location.clone(),
            tenant: "tenant-a".into(),
            project: "project-a".into(),
            database: "database-a".into(),
            instance_id: 1,
            region: Region::Gru,
            tbep_listen: SocketAddr::from(([127, 0, 0, 1], 0)),
            server_certificate: "/missing/server.crt".into(),
            server_private_key: "/missing/server.key".into(),
            client_ca: "/missing/client-ca.crt".into(),
            gateway_principals: BTreeSet::new(),
            uds: None,
            public_api: None,
            replication_peers: Vec::new(),
            observability_listen: SocketAddr::from(([127, 0, 0, 1], 0)),
            max_message_bytes: terrabase_tbep::registry::MAX_FRAME_BYTES,
            max_inflight: 1,
            request_timeout: std::time::Duration::from_secs(1),
            tls_handshake_timeout: std::time::Duration::from_secs(1),
            shutdown_grace: std::time::Duration::from_secs(1),
        };
        let error = run(config).await.expect_err("production configuration must be present");
        assert!(error.to_string().contains("no typed production configuration"));
        assert!(!engine_location.exists());
    }

    #[test]
    fn pem_validation_rejects_empty_malformed_and_non_utf8_material() {
        assert!(!valid_pem("", false));
        assert!(!valid_pem(
            "-----BEGIN CERTIFICATE-----\nnot-base64\n-----END CERTIFICATE-----",
            false
        ));
        assert!(!valid_pem("-----BEGIN PRIVATE KEY-----\nAQ==\n-----END CERTIFICATE-----", true));
        assert!(!valid_pem(
            "-----BEGIN CERTIFICATE-----\nAQ==\n-----END CERTIFICATE-----\ntrailing",
            false
        ));

        let empty = write_temporary("pem-empty", b"");
        assert!(matches!(read_pem(&empty, false), Err(DaemonError::SecretFile)));
        std::fs::remove_file(&empty).expect("temporary PEM removes");

        let invalid_utf8 = write_temporary("pem-utf8", &[0xff, 0xfe]);
        assert!(matches!(read_pem(&invalid_utf8, false), Err(DaemonError::SecretFile)));
        std::fs::remove_file(&invalid_utf8).expect("temporary PEM removes");
    }

    #[test]
    fn pem_read_rejects_growth_and_oversize_without_path_redaction() {
        let oversized = write_temporary("pem-oversized", &vec![b'x'; MAX_PEM_BYTES_USIZE + 1]);
        let error = read_pem(&oversized, false).expect_err("oversized PEM refuses");
        assert!(matches!(error, DaemonError::SecretFile));
        assert!(!error.to_string().contains(oversized.to_string_lossy().as_ref()));
        std::fs::remove_file(&oversized).expect("temporary PEM removes");
    }

    #[cfg(unix)]
    #[test]
    fn opened_pem_handle_is_stable_when_path_is_replaced() {
        let path = write_temporary(
            "pem-replacement-original",
            b"-----BEGIN CERTIFICATE-----\nAQ==\n-----END CERTIFICATE-----\n",
        );
        let replacement = temporary_path("pem-replacement-new");
        let mut opened = open_readonly(&path).expect("PEM opens");
        std::fs::rename(&path, &replacement).expect("original PEM moves aside");
        std::fs::write(
            &path,
            b"-----BEGIN CERTIFICATE-----\nnot-base64\n-----END CERTIFICATE-----\n",
        )
        .expect("replacement PEM writes");

        let mut bytes = Vec::new();
        opened.read_to_end(&mut bytes).expect("opened PEM reads");
        assert_eq!(bytes, b"-----BEGIN CERTIFICATE-----\nAQ==\n-----END CERTIFICATE-----\n");
        assert!(matches!(read_pem(&path, false), Err(DaemonError::SecretFile)));

        std::fs::remove_file(&path).expect("replacement PEM removes");
        std::fs::remove_file(&replacement).expect("original PEM removes");
    }

    #[cfg(unix)]
    #[test]
    fn private_key_permissions_and_symlinks_fail_closed() {
        use std::os::unix::fs::{PermissionsExt as _, symlink};

        let target = write_temporary(
            "pem-target",
            b"-----BEGIN PRIVATE KEY-----\nAQ==\n-----END PRIVATE KEY-----\n",
        );
        let link = temporary_path("pem-link");
        symlink(&target, &link).expect("temporary PEM symlink");
        assert!(matches!(read_pem(&link, true), Err(DaemonError::SecretFile)));
        std::fs::remove_file(&link).expect("temporary symlink removes");

        let private = write_temporary(
            "pem-private-permissions",
            b"-----BEGIN PRIVATE KEY-----\nAQ==\n-----END PRIVATE KEY-----\n",
        );
        std::fs::set_permissions(&private, std::fs::Permissions::from_mode(0o644))
            .expect("temporary PEM permissions");
        assert!(matches!(read_pem(&private, true), Err(DaemonError::PrivateKeyPermissions)));
        std::fs::remove_file(&target).expect("temporary PEM removes");
        std::fs::remove_file(&private).expect("temporary PEM removes");
    }

    #[cfg(unix)]
    #[test]
    fn uds_parent_and_socket_mode_are_restricted() {
        use std::os::unix::fs::{MetadataExt as _, PermissionsExt as _};
        use std::os::unix::net::UnixListener;

        let directory = temporary_path("uds-parent");
        std::fs::create_dir(&directory).expect("temporary UDS directory creates");
        std::fs::set_permissions(&directory, std::fs::Permissions::from_mode(0o750))
            .expect("temporary UDS directory permissions");
        let socket = directory.join("operator.sock");
        assert!(validate_uds_parent(&socket).is_ok());

        std::fs::set_permissions(&directory, std::fs::Permissions::from_mode(0o752))
            .expect("temporary UDS directory permissions");
        assert!(matches!(validate_uds_parent(&socket), Err(DaemonError::UnsafeUdsPath)));
        std::fs::set_permissions(&directory, std::fs::Permissions::from_mode(0o750))
            .expect("temporary UDS directory permissions");

        let listener = UnixListener::bind(&socket).expect("temporary UDS binds");
        set_socket_mode(&socket, 0o660).expect("temporary UDS mode applies");
        assert_eq!(std::fs::symlink_metadata(&socket).expect("UDS metadata").mode() & 0o777, 0o660);
        drop(listener);
        std::fs::remove_file(&socket).expect("temporary UDS removes");
        std::fs::remove_dir(&directory).expect("temporary UDS directory removes");
    }
}
