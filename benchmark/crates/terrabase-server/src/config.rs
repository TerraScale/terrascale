//! Fail-closed daemon configuration.

use std::collections::BTreeSet;
use std::ffi::OsString;
use std::net::SocketAddr;
use std::path::{Path, PathBuf};
use std::time::Duration;

use serde::Deserialize;
use terrabase::observability::Region;

const MAX_CONFIG_BYTES: u64 = 64 * 1024;
const MAX_GATEWAY_PRINCIPALS: usize = 32;

/// Fully validated production server configuration.
#[derive(Clone, Debug)]
pub struct ServerConfig {
    /// Durable engine directory.
    pub engine_location: PathBuf,
    /// Stable serving instance identity.
    pub instance_id: u64,
    /// Bounded metrics Region label.
    pub region: Region,
    /// Mutual-TLS TBEP listen address.
    pub tbep_listen: SocketAddr,
    /// Leaf-first server certificate chain file.
    pub server_certificate: PathBuf,
    /// Private server key file.
    pub server_private_key: PathBuf,
    /// Client trust bundle file. Client certificates are mandatory.
    pub client_ca: PathBuf,
    /// Exact SPIFFE URI SANs admitted from verified client certificates.
    pub gateway_principals: BTreeSet<String>,
    /// Optional local UDS configuration.
    pub uds: Option<UdsConfig>,
    /// Private Prometheus and probe listen address.
    pub observability_listen: SocketAddr,
    /// Maximum protobuf message size.
    pub max_message_bytes: usize,
    /// Maximum in-flight engine calls.
    pub max_inflight: usize,
    /// Per-RPC execution timeout.
    pub request_timeout: Duration,
    /// TLS handshake timeout.
    pub tls_handshake_timeout: Duration,
    /// Graceful shutdown deadline.
    pub shutdown_grace: Duration,
}

/// Opt-in local Unix-domain socket configuration.
#[derive(Clone, Debug)]
pub struct UdsConfig {
    /// Absolute socket path.
    pub path: PathBuf,
    /// Socket permission bits after bind.
    pub mode: u32,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct FileConfig {
    engine: FileEngine,
    tbep: FileTbep,
    observability: FileObservability,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct FileEngine {
    location: PathBuf,
    instance_id: u64,
    region: String,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct FileTbep {
    listen: String,
    server_certificate: PathBuf,
    server_private_key: PathBuf,
    client_ca: PathBuf,
    gateway_principals: Vec<String>,
    uds: Option<FileUds>,
    max_message_bytes: usize,
    max_inflight: usize,
    request_timeout_millis: u64,
    tls_handshake_timeout_millis: u64,
    shutdown_grace_millis: u64,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct FileUds {
    path: PathBuf,
    mode: u32,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct FileObservability {
    listen: String,
}

impl ServerConfig {
    /// Resolves the configuration file from `--config PATH` or
    /// `TERRABASE_CONFIG_FILE`. No other command-line value is accepted, so
    /// certificate or key material cannot accidentally enter process argv.
    pub fn path_from_process() -> Result<PathBuf, ConfigError> {
        let arguments = std::env::args_os().skip(1).collect::<Vec<_>>();
        let environment = std::env::var_os("TERRABASE_CONFIG_FILE");
        Self::path_from(&arguments, environment)
    }

    /// Resolves a configuration path from explicit argument and environment
    /// values. This pure form keeps parsing testable without mutating process
    /// environment.
    pub fn path_from(
        arguments: &[OsString],
        environment: Option<OsString>,
    ) -> Result<PathBuf, ConfigError> {
        match arguments {
            [] => environment.map(PathBuf::from).ok_or(ConfigError::MissingConfigurationPath),
            [flag, path] if flag == "--config" && !path.is_empty() => {
                if environment.is_some() {
                    return Err(ConfigError::AmbiguousConfigurationPath);
                }
                Ok(PathBuf::from(path))
            }
            _ => Err(ConfigError::InvalidArguments),
        }
    }

    /// Reads and validates one configuration file. Symlinks, oversized files,
    /// unknown keys, relative production paths, zero bounds, and plaintext TCP
    /// configurations fail closed.
    pub fn load(path: &Path) -> Result<Self, ConfigError> {
        let metadata = std::fs::symlink_metadata(path).map_err(ConfigError::ReadConfiguration)?;
        if metadata.file_type().is_symlink() || !metadata.is_file() {
            return Err(ConfigError::UnsafeConfigurationFile);
        }
        if metadata.len() > MAX_CONFIG_BYTES {
            return Err(ConfigError::ConfigurationTooLarge);
        }
        let text = std::fs::read_to_string(path).map_err(ConfigError::ReadConfiguration)?;
        Self::parse(&text)
    }

    /// Parses and validates TOML configuration text.
    pub fn parse(text: &str) -> Result<Self, ConfigError> {
        let file: FileConfig = toml::from_str(text).map_err(ConfigError::ParseConfiguration)?;
        let tbep_listen = socket(&file.tbep.listen, "tbep.listen")?;
        let observability_listen = socket(&file.observability.listen, "observability.listen")?;
        if tbep_listen.port() == 0
            || observability_listen.port() == 0
            || tbep_listen == observability_listen
        {
            return Err(ConfigError::InvalidListenAddress);
        }
        // The observability router has no authentication or TLS seam. Keep
        // it on the host loopback boundary until the orchestrator-owned
        // authenticated listener can be composed here.
        if !observability_listen.ip().is_loopback() {
            return Err(ConfigError::UnprotectedObservability);
        }
        for path in [
            &file.engine.location,
            &file.tbep.server_certificate,
            &file.tbep.server_private_key,
            &file.tbep.client_ca,
        ] {
            absolute(path)?;
        }
        let region = match file.engine.region.as_str() {
            "GRU" => Region::Gru,
            "SFO" => Region::Sfo,
            "FRA" => Region::Fra,
            _ => return Err(ConfigError::InvalidRegion),
        };
        if file.engine.instance_id == 0
            || !(1024..=terrabase_tbep::registry::MAX_FRAME_BYTES)
                .contains(&file.tbep.max_message_bytes)
            || !(1..=4096).contains(&file.tbep.max_inflight)
        {
            return Err(ConfigError::InvalidBound);
        }
        let request_timeout = bounded_duration(file.tbep.request_timeout_millis, 100, 300_000)?;
        let tls_handshake_timeout =
            bounded_duration(file.tbep.tls_handshake_timeout_millis, 100, 30_000)?;
        let shutdown_grace = bounded_duration(file.tbep.shutdown_grace_millis, 100, 300_000)?;
        if file.tbep.gateway_principals.is_empty()
            || file.tbep.gateway_principals.len() > MAX_GATEWAY_PRINCIPALS
        {
            return Err(ConfigError::InvalidGatewayPrincipal);
        }
        let mut gateway_principals = BTreeSet::new();
        for principal in file.tbep.gateway_principals {
            if principal.is_empty()
                || principal.len() > terrabase_tbep::grpc::MAX_PRINCIPAL_BYTES
                || principal != principal.trim()
                || principal.chars().any(char::is_control)
                || !valid_spiffe_uri(&principal)
                || !gateway_principals.insert(principal)
            {
                return Err(ConfigError::InvalidGatewayPrincipal);
            }
        }
        let uds = file
            .tbep
            .uds
            .map(|uds| {
                absolute(&uds.path)?;
                if uds.mode & !0o770 != 0 || uds.mode & 0o600 != 0o600 {
                    return Err(ConfigError::InvalidUdsMode);
                }
                Ok(UdsConfig { path: uds.path, mode: uds.mode })
            })
            .transpose()?;
        Ok(Self {
            engine_location: file.engine.location,
            instance_id: file.engine.instance_id,
            region,
            tbep_listen,
            server_certificate: file.tbep.server_certificate,
            server_private_key: file.tbep.server_private_key,
            client_ca: file.tbep.client_ca,
            gateway_principals,
            uds,
            observability_listen,
            max_message_bytes: file.tbep.max_message_bytes,
            max_inflight: file.tbep.max_inflight,
            request_timeout,
            tls_handshake_timeout,
            shutdown_grace,
        })
    }
}

fn absolute(path: &Path) -> Result<(), ConfigError> {
    if !path.is_absolute() {
        return Err(ConfigError::RelativeProductionPath);
    }
    Ok(())
}

fn valid_spiffe_uri(value: &str) -> bool {
    let authority = value.strip_prefix("spiffe://").unwrap_or_default();
    !authority.is_empty()
        && !authority.starts_with('/')
        && value.len() <= terrabase_tbep::grpc::MAX_PRINCIPAL_BYTES
        && value == value.trim()
        && !value.chars().any(char::is_control)
        && !value.chars().any(char::is_whitespace)
}

fn socket(value: &str, _field: &'static str) -> Result<SocketAddr, ConfigError> {
    value.parse().map_err(|_| ConfigError::InvalidListenAddress)
}

fn bounded_duration(value: u64, minimum: u64, maximum: u64) -> Result<Duration, ConfigError> {
    if !(minimum..=maximum).contains(&value) {
        return Err(ConfigError::InvalidBound);
    }
    Ok(Duration::from_millis(value))
}

/// A fail-closed configuration error. Variants never contain secret material.
#[derive(Debug)]
pub enum ConfigError {
    /// Neither argv nor environment supplied a configuration path.
    MissingConfigurationPath,
    /// Both argv and environment supplied a configuration path.
    AmbiguousConfigurationPath,
    /// Arguments were not exactly `--config PATH`.
    InvalidArguments,
    /// The configuration file could not be inspected or read.
    ReadConfiguration(std::io::Error),
    /// The configuration path was a symlink or non-regular file.
    UnsafeConfigurationFile,
    /// The configuration exceeded its byte bound.
    ConfigurationTooLarge,
    /// TOML parsing or unknown-field validation failed.
    ParseConfiguration(toml::de::Error),
    /// A production path was relative.
    RelativeProductionPath,
    /// A listener was invalid, port zero, or reused.
    InvalidListenAddress,
    /// The Region was not GRU, SFO, or FRA.
    InvalidRegion,
    /// A configured numeric bound was zero or outside its production range.
    InvalidBound,
    /// The admitted gateway principal set was empty, duplicated, or invalid.
    InvalidGatewayPrincipal,
    /// The UDS mode was broader than 0770 or omitted owner read/write.
    InvalidUdsMode,
    /// The private observability listener has no secure/authenticated seam.
    UnprotectedObservability,
}

impl std::fmt::Display for ConfigError {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        formatter.write_str(match self {
            Self::MissingConfigurationPath => "no TerraBase configuration file was selected",
            Self::AmbiguousConfigurationPath => "the configuration path was specified twice",
            Self::InvalidArguments => "expected --config PATH and no other arguments",
            Self::ReadConfiguration(_) => "the configuration file could not be read",
            Self::UnsafeConfigurationFile => "the configuration path is not a safe regular file",
            Self::ConfigurationTooLarge => "the configuration file exceeds 64 KiB",
            Self::ParseConfiguration(_) => "the configuration file is not valid strict TOML",
            Self::RelativeProductionPath => "production file paths must be absolute",
            Self::InvalidListenAddress => "configured listener addresses are invalid",
            Self::InvalidRegion => "engine.region must be GRU, SFO, or FRA",
            Self::InvalidBound => "a production server bound is outside its accepted range",
            Self::InvalidGatewayPrincipal => "gateway_principals is invalid",
            Self::InvalidUdsMode => "the local UDS mode is unsafe",
            Self::UnprotectedObservability => {
                "observability must bind loopback until an authenticated boundary is composed"
            }
        })
    }
}

impl std::error::Error for ConfigError {
    fn source(&self) -> Option<&(dyn std::error::Error + 'static)> {
        match self {
            Self::ReadConfiguration(error) => Some(error),
            Self::ParseConfiguration(error) => Some(error),
            _ => None,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn valid() -> String {
        r#"
[engine]
location = "/var/lib/terrabase"
instance_id = 7
region = "GRU"

[tbep]
listen = "0.0.0.0:8421"
server_certificate = "/run/secrets/terrabase.crt"
server_private_key = "/run/secrets/terrabase.key"
client_ca = "/run/secrets/gateway-ca.crt"
gateway_principals = ["spiffe://terrabase.example/gateway/ts-internals"]
max_message_bytes = 16777216
max_inflight = 128
request_timeout_millis = 30000
tls_handshake_timeout_millis = 5000
shutdown_grace_millis = 30000

[observability]
listen = "127.0.0.1:9464"
"#
        .to_owned()
    }

    #[test]
    fn strict_production_configuration_parses() {
        let config = ServerConfig::parse(&valid()).expect("valid config");
        assert_eq!(config.region, Region::Gru);
        assert_eq!(config.max_inflight, 128);
        assert!(
            config.gateway_principals.contains("spiffe://terrabase.example/gateway/ts-internals")
        );
    }

    #[test]
    fn unknown_fields_and_relative_paths_fail_closed() {
        let unknown = valid().replace("instance_id = 7", "instance_id = 7\ninvented = true");
        assert!(matches!(ServerConfig::parse(&unknown), Err(ConfigError::ParseConfiguration(_))));
        let relative = valid().replace("/var/lib/terrabase", "var/lib/terrabase");
        assert!(matches!(ServerConfig::parse(&relative), Err(ConfigError::RelativeProductionPath)));
    }

    #[test]
    fn bounded_runtime_controls_are_preserved() {
        for (field, value) in [
            ("max_message_bytes", "16384"),
            ("max_inflight", "1"),
            ("request_timeout_millis", "100"),
            ("tls_handshake_timeout_millis", "100"),
        ] {
            let text = valid().replace(
                &format!(
                    "{field} = {}",
                    match field {
                        "max_message_bytes" => "16777216",
                        "max_inflight" => "128",
                        "request_timeout_millis" => "30000",
                        _ => "5000",
                    }
                ),
                &format!("{field} = {value}"),
            );
            let parsed = ServerConfig::parse(&text).expect("the bounded control is supported");
            match field {
                "max_message_bytes" => assert_eq!(parsed.max_message_bytes, 16_384),
                "max_inflight" => assert_eq!(parsed.max_inflight, 1),
                "request_timeout_millis" => {
                    assert_eq!(parsed.request_timeout, Duration::from_millis(100));
                }
                _ => assert_eq!(parsed.tls_handshake_timeout, Duration::from_millis(100)),
            }
        }
    }

    #[test]
    fn gateway_principals_require_exact_spiffe_uris() {
        for principal in ["gateway", "https://example/gateway", "spiffe://\u{0001}"] {
            let text =
                valid().replace("spiffe://terrabase.example/gateway/ts-internals", principal);
            assert!(matches!(
                ServerConfig::parse(&text),
                Err(ConfigError::InvalidGatewayPrincipal)
            ));
        }
    }

    #[test]
    fn observability_must_remain_on_loopback() {
        let public = valid().replace("127.0.0.1:9464", "0.0.0.0:9464");
        assert!(matches!(ServerConfig::parse(&public), Err(ConfigError::UnprotectedObservability)));
    }

    #[test]
    fn secrets_cannot_be_passed_as_arguments() {
        let path = ServerConfig::path_from(
            &[OsString::from("--config"), OsString::from("/etc/terrabase.toml")],
            None,
        )
        .expect("path");
        assert_eq!(path, PathBuf::from("/etc/terrabase.toml"));
        assert!(matches!(
            ServerConfig::path_from(
                &[
                    OsString::from("--config"),
                    OsString::from("/etc/terrabase.toml"),
                    OsString::from("-----BEGIN PRIVATE KEY-----")
                ],
                None
            ),
            Err(ConfigError::InvalidArguments)
        ));
    }
}
