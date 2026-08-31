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

const MAX_PEM_BYTES: u64 = 1024 * 1024;
const MAX_PEM_BYTES_USIZE: usize = 1024 * 1024;

/// Opens the configured engine and runs every production listener until a
/// termination signal begins bounded graceful drain.
///
/// # Errors
///
/// Returns an error when TLS material, the engine, a listener, shutdown, or
/// graceful draining fails.
pub async fn run(config: ServerConfig) -> Result<(), Box<dyn Error + Send + Sync>> {
    if config.mode == ServerMode::Production {
        return Err(Box::new(DaemonError::ProductionCompositionUnavailable));
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
    ProductionCompositionUnavailable,
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
            Self::ProductionCompositionUnavailable => formatter
                .write_str("production configuration requires the complete Member orchestrator"),
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
    async fn production_mode_refuses_before_secret_or_listener_side_effects() {
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
            observability_listen: SocketAddr::from(([127, 0, 0, 1], 0)),
            max_message_bytes: terrabase_tbep::registry::MAX_FRAME_BYTES,
            max_inflight: 1,
            request_timeout: std::time::Duration::from_secs(1),
            tls_handshake_timeout: std::time::Duration::from_secs(1),
            shutdown_grace: std::time::Duration::from_secs(1),
        };
        let error = run(config).await.expect_err("production handoff must refuse");
        assert!(error.to_string().contains("complete Member orchestrator"));
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
