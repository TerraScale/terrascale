//! Production daemon composition and shutdown.

use std::error::Error;
use std::path::Path;
use std::sync::Arc;

use terrabase::observability::Component;
use terrabase::observability::HealthStatus;
use terrabase::observability::MetricsRegistry;
use terrabase::observability::ServiceState;
use terrabase_tbep::TbepHost;
use terrabase_tbep::engine_host::EngineHost;
use terrabase_tbep::grpc::TbepGrpcHandle;
use terrabase_tbep::grpc::TbepGrpcServer;
use terrabase_tbep::proto::terrabase::v1 as pb;
use terrabase_tbep::registry::ShutdownReason;
use terrabase_tbep::registry::SupervisionShutdownRequest;
use tokio::sync::oneshot;

use crate::config::ServerConfig;

const MAX_PEM_BYTES: u64 = 1024 * 1024;

/// Opens the configured engine and runs every production listener until a
/// termination signal begins bounded graceful drain.
pub async fn run(config: ServerConfig) -> Result<(), Box<dyn Error + Send + Sync>> {
    // TBEP currently validates the client CA but does not expose the peer
    // certificate/SAN to its application identity check. Metadata principal
    // values therefore cannot safely satisfy gateway_principals. Refuse to
    // start until the authenticated certificate-to-principal seam exists.
    if !config.gateway_principals.is_empty() {
        return Err(Box::new(DaemonError::GatewayPrincipalBindingUnavailable));
    }
    let certificate = read_pem(&config.server_certificate, false)?;
    let private_key = read_pem(&config.server_private_key, true)?;
    let client_ca = read_pem(&config.client_ca, false)?;

    let metrics = Arc::new(MetricsRegistry::new());
    metrics.set_component_health(Component::Metrics, HealthStatus::Healthy);
    let host = Arc::new(
        EngineHost::open(&config.engine_location, config.instance_id)
            .map_err(|error| DaemonError::Engine(error.detail()))?,
    );
    metrics.set_component_health(Component::Storage, HealthStatus::Healthy);

    let network = Arc::new(TbepGrpcServer::with_metrics(
        Arc::clone(&host) as Arc<dyn TbepHost>,
        pb::BindingKind::NetworkMtls,
        config.region,
        Arc::clone(&metrics),
    ));
    let mut network_handle =
        network.serve_tls(config.tbep_listen, &certificate, &private_key, Some(&client_ca))?;

    let mut uds_handle = if let Some(uds) = &config.uds {
        validate_uds_parent(&uds.path)?;
        let local = Arc::new(TbepGrpcServer::with_metrics(
            Arc::clone(&host) as Arc<dyn TbepHost>,
            pb::BindingKind::Local,
            config.region,
            Arc::clone(&metrics),
        ));
        let handle = local.serve_uds(&uds.path)?;
        set_socket_mode(&uds.path, uds.mode)?;
        Some(handle)
    } else {
        None
    };

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
    let _ = http_stop.send(());
    // Signal every listener together, then bound the total drain wait. The
    // TBEP handle owns a bounded five-second listener drain; running both
    // handles in one blocking task prevents sequential waits from exceeding
    // shutdown_grace. If the lower handle outlives the deadline, its own
    // bounded shutdown continues detached while the daemon reports failure.
    let grpc_shutdown = tokio::task::spawn_blocking(move || {
        network_handle.shutdown();
        if let Some(handle) = &mut uds_handle {
            handle.shutdown();
        }
    });
    let drain = async {
        grpc_shutdown.await.map_err(|error| -> Box<dyn Error + Send + Sync> { Box::new(error) })?;
        match http_task.await {
            Ok(result) => {
                result.map_err(|error| -> Box<dyn Error + Send + Sync> { Box::new(error) })?
            }
            Err(error) => return Err(Box::new(error) as Box<dyn Error + Send + Sync>),
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

fn read_pem(path: &Path, private: bool) -> Result<String, DaemonError> {
    let metadata = std::fs::symlink_metadata(path).map_err(|_| DaemonError::SecretFile)?;
    if metadata.file_type().is_symlink()
        || !metadata.is_file()
        || metadata.len() == 0
        || metadata.len() > MAX_PEM_BYTES
    {
        return Err(DaemonError::SecretFile);
    }
    #[cfg(unix)]
    if private {
        use std::os::unix::fs::PermissionsExt as _;
        if metadata.permissions().mode() & 0o077 != 0 {
            return Err(DaemonError::PrivateKeyPermissions);
        }
    }
    let text = std::fs::read_to_string(path).map_err(|_| DaemonError::SecretFile)?;
    if !text.contains("-----BEGIN") || !text.contains("-----END") {
        return Err(DaemonError::SecretFile);
    }
    Ok(text)
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
    GatewayPrincipalBindingUnavailable,
    ShutdownTimeout,
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
            Self::GatewayPrincipalBindingUnavailable => formatter.write_str(
                "the serving API cannot bind an authenticated client certificate to gateway_principals",
            ),
            Self::ShutdownTimeout => formatter.write_str("graceful shutdown exceeded its deadline"),
        }
    }
}

impl Error for DaemonError {}

#[allow(dead_code)]
fn _handle_type_is_send(_: &TbepGrpcHandle) {}
