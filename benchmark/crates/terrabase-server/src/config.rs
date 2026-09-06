//! Fail-closed daemon configuration.

use std::collections::{BTreeMap, BTreeSet};
use std::ffi::OsString;
use std::fs::{File, OpenOptions};
use std::io::Read;
use std::net::SocketAddr;
use std::path::{Path, PathBuf};
use std::time::Duration;

use serde::Deserialize;
use terrabase::encryption::{DatabaseKeyVersion, KeyProviderIdentity};
use terrabase::identity::{ClusterUlid, DatabaseUlid, MemberUlid, ProjectUlid, TenantUlid};
use terrabase::member::EnvironmentName;
use terrabase::observability::Region;
use terrabase::regions::RegionCode;

const MAX_CONFIG_BYTES: u64 = 64 * 1024;
const MAX_CONFIG_BYTES_USIZE: usize = 64 * 1024;
const MAX_MEMBER_PRINCIPALS: usize = 32;
const MAX_DATABASES: usize = 1_024;
const MAX_PEERS: usize = 64;
const MAX_CAPABILITIES: usize = 32;
const MAX_PATH_BYTES: usize = 4_096;
const MAX_RELEASE_BYTES: usize = 128;
const MAX_QUOTA_BYTES: u64 = 1 << 50;
const MAX_QUOTA_DOCUMENTS: u64 = 1_000_000_000;
const MAX_QUOTA_INFLIGHT: usize = 1_000_000;

/// The composition mode selected by the configuration boundary.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ServerMode {
    /// A fully bound production Member configuration.
    Production,
    /// The explicitly named one-database compatibility path for local work.
    LocalDevelopment,
}

/// Typed environment and Member identity for production composition.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ProductionEnvironment {
    /// Bounded environment name, never a filesystem path.
    pub name: EnvironmentName,
    /// Cluster identity used by exact peer workload identities.
    pub cluster: ClusterUlid,
    /// The one Member seat owned by this process.
    pub member: MemberUlid,
}

/// Per-database bounded admission quotas.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct DatabaseQuotas {
    /// Maximum durable bytes admitted for the database.
    pub max_storage_bytes: u64,
    /// Maximum document count admitted for the database.
    pub max_documents: u64,
    /// Maximum concurrent operations admitted for the database.
    pub max_inflight: usize,
}

/// One independently routed, encrypted production database.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ProductionDatabase {
    /// Canonical tenant identity.
    pub tenant: TenantUlid,
    /// Canonical Project identity.
    pub project: ProjectUlid,
    /// Canonical database identity.
    pub database: DatabaseUlid,
    /// Canonical home Region.
    pub region: RegionCode,
    /// Distinct absolute durable location.
    pub location: PathBuf,
    /// Durable authorization-policy journal path.
    pub policy_journal: PathBuf,
    /// Durable backup root path.
    pub backup_root: PathBuf,
    /// Accepted production key provider.
    pub encryption_provider: KeyProviderIdentity,
    /// Canonical database-key version identity.
    pub encryption_key: DatabaseKeyVersion,
    /// Bounded resource quotas.
    pub quotas: DatabaseQuotas,
    /// Whether this catalog entry is enabled for admission.
    pub enabled: bool,
}

/// An exact authenticated peer endpoint in one placement generation.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ProductionPeer {
    /// Canonical remote Member identity.
    pub member: MemberUlid,
    /// Remote QUIC endpoint.
    pub endpoint: SocketAddr,
    /// Exact SPIFFE URI SAN admitted for the peer.
    pub spiffe_identity: String,
}

/// Separate production listener and transport bindings.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ProductionTransport {
    /// Mandatory TBEP mTLS listener.
    pub tbep_listen: SocketAddr,
    /// Mandatory Member QUIC listener.
    pub quic_listen: SocketAddr,
    /// Private metrics and lifecycle listener.
    pub metrics_listen: SocketAddr,
    /// Whether wildcard TBEP binding was explicitly approved.
    pub allow_wildcard_tbep: bool,
    /// Whether wildcard QUIC binding was explicitly approved.
    pub allow_wildcard_quic: bool,
    /// Whether wildcard metrics binding was explicitly approved.
    pub allow_wildcard_metrics: bool,
    /// TLS server certificate chain path.
    pub server_certificate: PathBuf,
    /// TLS private key path.
    pub server_private_key: PathBuf,
    /// TLS client/member CA bundle path.
    pub client_ca: PathBuf,
    /// Exact remote peer endpoint and identity set.
    pub peers: Vec<ProductionPeer>,
}

/// Release and requested capability binding supplied to production handoff.
///
/// These values are configuration claims only; parsing them never proves
/// release qualification, FIPS operation, or external provider capability.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ReleaseBinding {
    /// Operator-selected release identifier.
    pub release: String,
    /// Closed capability names supplied by release qualification.
    pub capabilities: BTreeSet<String>,
}

/// Fully typed production configuration handed to the eventual Member
/// orchestrator.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ProductionConfig {
    /// Typed environment and local Member identity.
    pub environment: ProductionEnvironment,
    /// One-to-many isolated database catalog.
    pub databases: Vec<ProductionDatabase>,
    /// RF=2 placement generation and replica-set epoch.
    pub placement: ProductionPlacement,
    /// Separate TBEP, QUIC, and metrics bindings.
    pub transport: ProductionTransport,
    /// Release/capability qualification binding.
    pub release: ReleaseBinding,
    /// Explicit authenticated control-intent listener configuration.
    pub control: Option<ProductionControl>,
    /// OCI Vault delivery needed before admitting encrypted data operations.
    pub keys: Option<ProductionKeys>,
}

/// Durable pending-placement control policy. It cannot grant membership or readiness.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ProductionControl {
    /// Owner-private SQLite proposal and retry journal.
    pub proposal_journal: PathBuf,
    /// Exact SPIFFE principals allowed to stage host-scoped intent over mTLS.
    pub orchestrator_principals: BTreeSet<String>,
    /// Separate read-only principals for authenticated lifecycle observations.
    pub observer_principals: BTreeSet<String>,
}

/// Authenticated local orchestrator key-delivery endpoint.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ProductionKeys {
    /// Absolute Unix socket path under an owner-private directory.
    pub socket: PathBuf,
    /// Required orchestrator peer UID, checked on every key-delivery connection.
    pub orchestrator_uid: u32,
}

/// One RF=2 placement generation.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ProductionPlacement {
    /// Monotonic placement generation.
    pub generation: u64,
    /// Monotonic replica-set epoch.
    pub epoch: u64,
    /// Canonical Region and exactly two Member seats.
    pub regions: BTreeMap<RegionCode, [MemberUlid; 2]>,
}

/// The resolved public `/api/v1` listener configuration.
#[derive(Clone, Debug)]
pub struct PublicApiConfig {
    /// The listener address.
    pub listen: SocketAddr,
    /// The member's data root: the control-plane engine and the database
    /// catalog both live under it.
    pub data_root: PathBuf,
    /// A file holding one bootstrap API key, when the deployment mints
    /// one. Absent means deny-all: the surface answers, nothing is
    /// admitted.
    pub bootstrap_key_file: Option<PathBuf>,
    /// Current HMAC key file used by the sync edge's origin proof.
    pub origin_proof_key_file: Option<PathBuf>,
    /// Previous origin-proof HMAC key retained for one rotation overlap.
    pub origin_proof_previous_key_file: Option<PathBuf>,
    /// Current HMAC key file used to issue view capabilities.
    pub capability_key_file: Option<PathBuf>,
    /// Previous view-capability HMAC key retained for one overlap.
    pub capability_previous_key_file: Option<PathBuf>,
}

/// Fully validated production server configuration.
#[derive(Clone, Debug)]
pub struct ServerConfig {
    /// Selected composition mode.
    pub mode: ServerMode,
    /// Fully typed production configuration, absent only in local mode.
    pub production: Option<ProductionConfig>,
    /// Durable engine directory.
    pub engine_location: PathBuf,
    /// Tenant pinned to the standalone engine location.
    pub tenant: String,
    /// Project pinned to the standalone engine location.
    pub project: String,
    /// Database pinned to the standalone engine location.
    pub database: String,
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
    /// Exact SPIFFE URI SANs admitted from verified private-member
    /// certificates.
    pub member_principals: BTreeSet<String>,
    /// Optional local UDS configuration.
    pub uds: Option<UdsConfig>,
    /// Public `/api/v1` listener and its data root; `None` leaves the
    /// public surface unbound (the standalone TBEP-only shape).
    pub public_api: Option<PublicApiConfig>,
    /// The static replication peers whose lanes this member enumerates;
    /// empty when the configuration names none (fail closed: no lanes, no
    /// replication).
    pub replication_peers: Vec<ReplicationPeer>,
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

/// One static replication peer the dev member composition dials
/// (`SHORT_GOAL` section 1: "Members discover each other from a static peer
/// list in the config"): a peer region's code and its replication
/// endpoint. The runtime enumerates its cross-region lanes from this list;
/// an absent list fails closed — no lanes, no replication.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct ReplicationPeer {
    /// The peer's region.
    pub region: Region,
    /// The peer's replication (QUIC) endpoint.
    pub address: SocketAddr,
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
    #[serde(default)]
    mode: Option<String>,
    engine: FileEngine,
    tbep: FileTbep,
    observability: FileObservability,
    #[serde(default)]
    public_api: Option<FilePublicApi>,
    #[serde(default)]
    replication: Option<FileReplication>,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct FileReplication {
    peers: Vec<FileReplicationPeer>,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct FileReplicationPeer {
    region: String,
    address: String,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct FileEngine {
    location: PathBuf,
    tenant: String,
    project: String,
    database: String,
    instance_id: u64,
    region: String,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct FileTbep {
    listen: String,
    #[serde(default)]
    allow_wildcard: bool,
    server_certificate: PathBuf,
    server_private_key: PathBuf,
    client_ca: PathBuf,
    member_principals: Vec<String>,
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
    #[serde(default)]
    allow_wildcard: bool,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct FilePublicApi {
    listen: String,
    #[serde(default)]
    allow_wildcard: bool,
    data_root: PathBuf,
    /// A file holding one bootstrap API key (printed once by the CLI that
    /// wrote it).
    #[serde(default)]
    bootstrap_key_file: Option<PathBuf>,
    /// Current HMAC key file used by the sync edge's origin proof.
    #[serde(default, alias = "origin_proof_current_key_file")]
    origin_proof_key_file: Option<PathBuf>,
    /// Previous origin-proof HMAC key retained for one rotation overlap.
    #[serde(default)]
    origin_proof_previous_key_file: Option<PathBuf>,
    /// Current HMAC key file used to issue view capabilities.
    #[serde(default)]
    capability_key_file: Option<PathBuf>,
    /// Previous view-capability HMAC key retained for one overlap.
    #[serde(default)]
    capability_previous_key_file: Option<PathBuf>,
    /// The region codes this deployment considers active (D-37).
    #[serde(default)]
    regions: Vec<String>,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct FileProductionConfig {
    mode: String,
    environment: FileProductionEnvironment,
    databases: Vec<FileProductionDatabase>,
    placement: FileProductionPlacement,
    transport: FileProductionTransport,
    release: FileReleaseBinding,
    control: Option<FileProductionControl>,
    keys: Option<FileProductionKeys>,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct FileProductionKeys {
    socket: PathBuf,
    orchestrator_uid: u32,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct FileProductionControl {
    proposal_journal: PathBuf,
    #[serde(default)]
    orchestrator_principals: Vec<String>,
    #[serde(default)]
    observer_principals: Vec<String>,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct FileProductionEnvironment {
    name: String,
    cluster: String,
    member: String,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct FileProductionDatabase {
    tenant: String,
    project: String,
    database: String,
    region: String,
    location: PathBuf,
    policy_journal: PathBuf,
    backup_root: PathBuf,
    encryption_provider: String,
    encryption_key: String,
    quotas: FileDatabaseQuotas,
    enabled: Option<bool>,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
#[allow(
    clippy::struct_field_names,
    reason = "the max_ prefixes are part of the production configuration contract"
)]
struct FileDatabaseQuotas {
    max_storage_bytes: u64,
    max_documents: u64,
    max_inflight: usize,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct FileProductionPlacement {
    generation: u64,
    epoch: u64,
    regions: Vec<FilePlacementRegion>,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct FilePlacementRegion {
    region: String,
    members: Vec<String>,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct FileProductionTransport {
    tbep_listen: String,
    quic_listen: String,
    metrics_listen: String,
    #[serde(default)]
    allow_wildcard_tbep: bool,
    #[serde(default)]
    allow_wildcard_quic: bool,
    #[serde(default)]
    allow_wildcard_metrics: bool,
    server_certificate: PathBuf,
    server_private_key: PathBuf,
    client_ca: PathBuf,
    peers: Vec<FileProductionPeer>,
    uds: Option<FileUds>,
    max_message_bytes: usize,
    max_inflight: usize,
    request_timeout_millis: u64,
    tls_handshake_timeout_millis: u64,
    shutdown_grace_millis: u64,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct FileProductionPeer {
    member: String,
    endpoint: String,
    spiffe_identity: String,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct FileReleaseBinding {
    release: String,
    capabilities: Vec<String>,
}

impl ServerConfig {
    /// Resolves the configuration file from `--config PATH` or
    /// `TERRABASE_CONFIG_FILE`. No other command-line value is accepted, so
    /// certificate or key material cannot accidentally enter process argv.
    ///
    /// # Errors
    ///
    /// Returns an error when the command line and environment do not select
    /// exactly one configuration path.
    pub fn path_from_process() -> Result<PathBuf, ConfigError> {
        let arguments = std::env::args_os().skip(1).collect::<Vec<_>>();
        let environment = std::env::var_os("TERRABASE_CONFIG_FILE");
        Self::path_from(&arguments, environment)
    }

    /// Resolves a configuration path from explicit argument and environment
    /// values. This pure form keeps parsing testable without mutating process
    /// environment.
    ///
    /// # Errors
    ///
    /// Returns an error when the arguments are not exactly `--config PATH`, or
    /// when both arguments and environment select a path.
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

    /// Reads and validates one configuration file from one inspected handle.
    /// Symlinks, oversized files, unknown keys, relative production paths, zero
    /// bounds, and plaintext TCP configurations fail closed.
    ///
    /// # Errors
    ///
    /// Returns an error when the file is unsafe, changes while being read, is
    /// not UTF-8, or contains invalid configuration.
    pub fn load(path: &Path) -> Result<Self, ConfigError> {
        let text = read_configuration(path)?;
        Self::parse(&text)
    }

    /// Parses and validates TOML configuration text.
    ///
    /// # Errors
    ///
    /// Returns an error when the TOML or any production serving bound is
    /// invalid.
    pub fn parse(text: &str) -> Result<Self, ConfigError> {
        let document: toml::Value =
            toml::from_str(text).map_err(ConfigError::ParseConfiguration)?;
        if let Some(mode) = document.get("mode") {
            let mode = mode.as_str().ok_or(ConfigError::InvalidMode)?;
            return match mode {
                "production" => Self::parse_production(text),
                "local-development" => Self::parse_local(text),
                _ => Err(ConfigError::InvalidMode),
            };
        }
        Self::parse_local(text)
    }

    /// The static replication peers whose cross-region lanes this member
    /// enumerates (`SHORT_GOAL` section 1). Empty means no lanes: the member
    /// runs standalone and replicates nothing.
    #[must_use]
    pub fn replication_lanes(&self) -> &[ReplicationPeer] {
        &self.replication_peers
    }

    #[allow(
        clippy::too_many_lines,
        reason = "local configuration is validated as one fail-closed boundary"
    )]
    fn parse_local(text: &str) -> Result<Self, ConfigError> {
        let file: FileConfig = toml::from_str(text).map_err(ConfigError::ParseConfiguration)?;
        if file.mode.as_deref().is_some_and(|mode| mode != "local-development") {
            return Err(ConfigError::InvalidMode);
        }
        let tbep_listen = socket(&file.tbep.listen, "tbep.listen")?;
        let observability_listen = socket(&file.observability.listen, "observability.listen")?;
        if tbep_listen.port() == 0
            || observability_listen.port() == 0
            || tbep_listen == observability_listen
            || (tbep_listen.ip().is_unspecified() && !file.tbep.allow_wildcard)
            || (observability_listen.ip().is_unspecified() && !file.observability.allow_wildcard)
        {
            return Err(ConfigError::InvalidListenAddress);
        }
        for path in [
            &file.engine.location,
            &file.tbep.server_certificate,
            &file.tbep.server_private_key,
            &file.tbep.client_ca,
        ] {
            absolute(path)?;
        }
        let public_api =
            parse_public_api(file.public_api.as_ref(), tbep_listen, observability_listen)?;
        let region = match file.engine.region.as_str() {
            "GRU" => Region::Gru,
            "SFO" => Region::Sfo,
            "FRA" => Region::Fra,
            _ => return Err(ConfigError::InvalidRegion),
        };
        terrabase_tbep::registry::BoundScope {
            principal: "spiffe://local/config-validation".into(),
            tenant: file.engine.tenant.clone(),
            project: file.engine.project.clone(),
            database: file.engine.database.clone(),
        }
        .validate()
        .map_err(|_| ConfigError::InvalidBound)?;
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
        if file.tbep.member_principals.is_empty()
            || file.tbep.member_principals.len() > MAX_MEMBER_PRINCIPALS
        {
            return Err(ConfigError::InvalidMemberPrincipal);
        }
        let mut member_principals = BTreeSet::new();
        for principal in file.tbep.member_principals {
            if principal.is_empty()
                || principal.len() > terrabase_tbep::grpc::MAX_PRINCIPAL_BYTES
                || principal != principal.trim()
                || principal.chars().any(char::is_control)
                || !member_principals.insert(principal)
            {
                return Err(ConfigError::InvalidMemberPrincipal);
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
        let replication_peers = parse_replication_peers(file.replication.as_ref(), region)?;
        Ok(Self {
            mode: ServerMode::LocalDevelopment,
            production: None,
            engine_location: file.engine.location,
            tenant: file.engine.tenant,
            project: file.engine.project,
            database: file.engine.database,
            instance_id: file.engine.instance_id,
            region,
            tbep_listen,
            server_certificate: file.tbep.server_certificate,
            server_private_key: file.tbep.server_private_key,
            client_ca: file.tbep.client_ca,
            member_principals,
            uds,
            public_api,
            replication_peers,
            observability_listen,
            max_message_bytes: file.tbep.max_message_bytes,
            max_inflight: file.tbep.max_inflight,
            request_timeout,
            tls_handshake_timeout,
            shutdown_grace,
        })
    }

    #[allow(
        clippy::too_many_lines,
        reason = "production configuration is validated as one fail-closed boundary"
    )]
    fn parse_production(text: &str) -> Result<Self, ConfigError> {
        let file: FileProductionConfig =
            toml::from_str(text).map_err(ConfigError::ParseConfiguration)?;
        if file.mode != "production" {
            return Err(ConfigError::InvalidMode);
        }
        let environment = ProductionEnvironment {
            name: EnvironmentName::try_new(&file.environment.name)
                .map_err(|_| ConfigError::InvalidProductionBoundary)?,
            cluster: parse_ulid(&file.environment.cluster, ConfigError::InvalidProductionBoundary)?,
            member: parse_ulid(&file.environment.member, ConfigError::InvalidProductionBoundary)?,
        };
        if file.databases.is_empty() || file.databases.len() > MAX_DATABASES {
            return Err(ConfigError::InvalidProductionBoundary);
        }
        let mut databases = Vec::with_capacity(file.databases.len());
        let mut identities = BTreeSet::new();
        let mut database_ids = BTreeSet::new();
        let mut projects = BTreeMap::new();
        let mut paths = Vec::with_capacity(file.databases.len() * 3);
        for entry in file.databases {
            let tenant = parse_ulid(&entry.tenant, ConfigError::InvalidProductionBoundary)?;
            let project = parse_ulid(&entry.project, ConfigError::InvalidProductionBoundary)?;
            let database = parse_ulid(&entry.database, ConfigError::InvalidProductionBoundary)?;
            let region = parse_region(&entry.region)?;
            if !identities.insert((tenant, project, database))
                || !database_ids.insert(database)
                || projects.insert(project, tenant).is_some_and(|owner| owner != tenant)
            {
                return Err(ConfigError::InvalidProductionBoundary);
            }
            let location = production_path(&entry.location)?;
            let policy_journal = production_path(&entry.policy_journal)?;
            let backup_root = production_path(&entry.backup_root)?;
            paths.extend([location.clone(), policy_journal.clone(), backup_root.clone()]);
            let provider = match entry.encryption_provider.as_str() {
                "oci-vault" => KeyProviderIdentity::OciVault,
                "local-file" => return Err(ConfigError::LocalProviderInProduction),
                _ => return Err(ConfigError::InvalidProductionBoundary),
            };
            let encryption_key = DatabaseKeyVersion::from_text(&entry.encryption_key)
                .map_err(|_| ConfigError::InvalidProductionBoundary)?;
            let quotas = DatabaseQuotas {
                max_storage_bytes: entry.quotas.max_storage_bytes,
                max_documents: entry.quotas.max_documents,
                max_inflight: entry.quotas.max_inflight,
            };
            if quotas.max_storage_bytes == 0
                || quotas.max_storage_bytes > MAX_QUOTA_BYTES
                || quotas.max_documents == 0
                || quotas.max_documents > MAX_QUOTA_DOCUMENTS
                || quotas.max_inflight == 0
                || quotas.max_inflight > MAX_QUOTA_INFLIGHT
                || entry.enabled.is_none()
            {
                return Err(ConfigError::InvalidProductionBoundary);
            }
            databases.push(ProductionDatabase {
                tenant,
                project,
                database,
                region,
                location,
                policy_journal,
                backup_root,
                encryption_provider: provider,
                encryption_key,
                quotas,
                enabled: entry.enabled.unwrap_or(false),
            });
        }
        if !databases.iter().any(|database| database.enabled) {
            return Err(ConfigError::InvalidProductionBoundary);
        }

        let placement = parse_placement(&file.placement, environment.member)?;
        if production_regions_missing(&databases, &placement) {
            return Err(ConfigError::InvalidProductionBoundary);
        }
        let transport = parse_transport(&file.transport, &environment, &placement)?;
        paths.extend([
            transport.server_certificate.clone(),
            transport.server_private_key.clone(),
            transport.client_ca.clone(),
        ]);
        let release = parse_release(&file.release)?;
        let control = file
            .control
            .map(|control| {
                let proposal_journal = production_path(&control.proposal_journal)?;
                let principals = control_principals(control.orchestrator_principals)?;
                let observers = control_principals(control.observer_principals)?;
                if (principals.is_empty() && observers.is_empty()) || !principals.is_disjoint(&observers) {
                    return Err(ConfigError::InvalidMemberPrincipal);
                }
                paths.push(proposal_journal.clone());
                Ok(ProductionControl { proposal_journal, orchestrator_principals: principals, observer_principals: observers })
            })
            .transpose()?;
        let keys = file
            .keys
            .map(|keys| {
                let socket = production_path(&keys.socket)?;
                paths.push(socket.clone());
                Ok(ProductionKeys { socket, orchestrator_uid: keys.orchestrator_uid })
            })
            .transpose()?;
        let production = ProductionConfig {
            environment,
            databases,
            placement,
            transport,
            release,
            control,
            keys,
        };
        let first = production
            .databases
            .iter()
            .find(|database| database.enabled)
            .ok_or(ConfigError::InvalidProductionBoundary)?;
        let tbep_listen = production.transport.tbep_listen;
        let metrics_listen = production.transport.metrics_listen;
        let uds = file
            .transport
            .uds
            .map(|uds| {
                let path = production_path(&uds.path)?;
                if uds.mode & !0o770 != 0 || uds.mode & 0o600 != 0o600 {
                    return Err(ConfigError::InvalidUdsMode);
                }
                Ok(UdsConfig { path, mode: uds.mode })
            })
            .transpose()?;
        if let Some(uds) = &uds {
            paths.push(uds.path.clone());
        }
        reject_nested_paths(&paths)?;
        Ok(Self {
            mode: ServerMode::Production,
            production: Some(production.clone()),
            // These fields are a temporary, explicitly documented projection
            // for the current daemon entry point. Production serving must use
            // `ProductionConfig` and the complete Member orchestrator.
            engine_location: first.location.clone(),
            tenant: first.tenant.to_text(),
            project: first.project.to_text(),
            database: first.database.to_text(),
            instance_id: 1,
            region: region_label(first.region),
            tbep_listen,
            server_certificate: production.transport.server_certificate.clone(),
            server_private_key: production.transport.server_private_key.clone(),
            client_ca: production.transport.client_ca.clone(),
            member_principals: BTreeSet::new(),
            uds,
            public_api: None,
            replication_peers: Vec::new(),
            observability_listen: metrics_listen,
            max_message_bytes: file.transport.max_message_bytes,
            max_inflight: file.transport.max_inflight,
            request_timeout: bounded_duration(file.transport.request_timeout_millis, 100, 300_000)?,
            tls_handshake_timeout: bounded_duration(
                file.transport.tls_handshake_timeout_millis,
                100,
                30_000,
            )?,
            shutdown_grace: bounded_duration(file.transport.shutdown_grace_millis, 100, 300_000)?,
        })
    }
}

fn absolute(path: &Path) -> Result<(), ConfigError> {
    if !path.is_absolute() {
        return Err(ConfigError::RelativeProductionPath);
    }
    Ok(())
}

fn control_principals(values: Vec<String>) -> Result<BTreeSet<String>, ConfigError> {
    if values.len() > MAX_MEMBER_PRINCIPALS { return Err(ConfigError::InvalidMemberPrincipal); }
    let mut principals = BTreeSet::new();
    for principal in values {
        if !principal.starts_with("spiffe://")
            || principal.len() > terrabase_tbep::grpc::MAX_PRINCIPAL_BYTES
            || principal.chars().any(char::is_whitespace)
            || principal.chars().any(char::is_control)
            || !principals.insert(principal) {
            return Err(ConfigError::InvalidMemberPrincipal);
        }
    }
    Ok(principals)
}

fn parse_ulid<T>(value: &str, error: ConfigError) -> Result<T, ConfigError>
where
    T: std::str::FromStr,
{
    value.parse().map_err(|_| error)
}

fn parse_region(value: &str) -> Result<RegionCode, ConfigError> {
    terrabase::regions::RegionCatalog::production()
        .parse(value)
        .map_err(|_| ConfigError::InvalidRegion)
}

/// Parses the static replication peer list (`SHORT_GOAL` section 1) against
/// the member's own region: bounded, each peer a distinct known region
/// with a dialable non-wildcard endpoint, never the member's own region.
/// An absent section fails closed as an empty list — no lanes.
fn parse_replication_peers(
    file: Option<&FileReplication>,
    own: Region,
) -> Result<Vec<ReplicationPeer>, ConfigError> {
    let Some(file) = file else { return Ok(Vec::new()) };
    if file.peers.is_empty() || file.peers.len() > MAX_PEERS {
        return Err(ConfigError::InvalidRegion);
    }
    let mut peers = Vec::with_capacity(file.peers.len());
    let mut regions = BTreeSet::new();
    let mut addresses = BTreeSet::new();
    for peer in &file.peers {
        let region = match peer.region.as_str() {
            "GRU" => Region::Gru,
            "SFO" => Region::Sfo,
            "FRA" => Region::Fra,
            _ => return Err(ConfigError::InvalidRegion),
        };
        let address =
            peer.address.parse::<SocketAddr>().map_err(|_| ConfigError::InvalidListenAddress)?;
        if region == own
            || address.port() == 0
            || address.ip().is_unspecified()
            || !regions.insert(region)
            || !addresses.insert(address)
        {
            return Err(ConfigError::InvalidListenAddress);
        }
        peers.push(ReplicationPeer { region, address });
    }
    Ok(peers)
}

fn region_label(region: RegionCode) -> Region {
    match region.as_str() {
        "GRU" => Region::Gru,
        "SFO" => Region::Sfo,
        "FRA" => Region::Fra,
        _ => Region::Unknown,
    }
}

fn parse_placement(
    file: &FileProductionPlacement,
    local_member: MemberUlid,
) -> Result<ProductionPlacement, ConfigError> {
    if file.generation == 0 || file.epoch == 0 || file.regions.is_empty() || file.regions.len() > 3
    {
        return Err(ConfigError::InvalidProductionBoundary);
    }
    let mut regions = BTreeMap::new();
    let mut members = BTreeSet::new();
    let mut local_seats = 0usize;
    for entry in &file.regions {
        let region = parse_region(&entry.region)?;
        if entry.members.len() != 2 || regions.contains_key(&region) {
            return Err(ConfigError::InvalidPlacement);
        }
        let first = parse_ulid::<MemberUlid>(&entry.members[0], ConfigError::InvalidPlacement)?;
        let second = parse_ulid::<MemberUlid>(&entry.members[1], ConfigError::InvalidPlacement)?;
        if first == second || !members.insert(first) || !members.insert(second) {
            return Err(ConfigError::InvalidPlacement);
        }
        local_seats += usize::from(first == local_member) + usize::from(second == local_member);
        regions.insert(region, [first, second]);
    }
    if local_seats != 1 {
        return Err(ConfigError::InvalidPlacement);
    }
    Ok(ProductionPlacement { generation: file.generation, epoch: file.epoch, regions })
}

fn parse_transport(
    file: &FileProductionTransport,
    environment: &ProductionEnvironment,
    placement: &ProductionPlacement,
) -> Result<ProductionTransport, ConfigError> {
    let tbep_listen = production_socket(&file.tbep_listen, file.allow_wildcard_tbep)?;
    let quic_listen = production_socket(&file.quic_listen, file.allow_wildcard_quic)?;
    let metrics_listen = production_socket(&file.metrics_listen, file.allow_wildcard_metrics)?;
    if BTreeSet::from([tbep_listen, quic_listen, metrics_listen]).len() != 3 {
        return Err(ConfigError::InvalidListenAddress);
    }
    if !(1024..=terrabase_tbep::registry::MAX_FRAME_BYTES).contains(&file.max_message_bytes)
        || !(1..=4096).contains(&file.max_inflight)
    {
        return Err(ConfigError::InvalidBound);
    }
    let server_certificate = production_path(&file.server_certificate)?;
    let server_private_key = production_path(&file.server_private_key)?;
    let client_ca = production_path(&file.client_ca)?;
    if file.peers.is_empty() || file.peers.len() > MAX_PEERS {
        return Err(ConfigError::InvalidTransportBinding);
    }
    let expected = placement
        .regions
        .values()
        .flat_map(|members| members.iter().copied())
        .filter(|member| *member != environment.member)
        .collect::<BTreeSet<_>>();
    let mut seen = BTreeSet::new();
    let mut endpoints = BTreeSet::new();
    let mut peers = Vec::with_capacity(file.peers.len());
    for peer in &file.peers {
        let member = parse_ulid::<MemberUlid>(&peer.member, ConfigError::InvalidTransportBinding)?;
        let endpoint = peer
            .endpoint
            .parse::<SocketAddr>()
            .map_err(|_| ConfigError::InvalidTransportBinding)?;
        if endpoint.port() == 0
            || endpoint.ip().is_unspecified()
            || BTreeSet::from([tbep_listen, quic_listen, metrics_listen]).contains(&endpoint)
            || !seen.insert(member)
            || !endpoints.insert(endpoint)
        {
            return Err(ConfigError::InvalidTransportBinding);
        }
        let expected_spiffe = format!(
            "spiffe://terrascale.tech/{}/terrabase-member/{}/{}",
            environment.name, environment.cluster, member
        );
        if peer.spiffe_identity != expected_spiffe || peer.spiffe_identity.len() > MAX_PATH_BYTES {
            return Err(ConfigError::InvalidTransportBinding);
        }
        peers.push(ProductionPeer {
            member,
            endpoint,
            spiffe_identity: peer.spiffe_identity.clone(),
        });
    }
    if seen != expected {
        return Err(ConfigError::InvalidTransportBinding);
    }
    Ok(ProductionTransport {
        tbep_listen,
        quic_listen,
        metrics_listen,
        allow_wildcard_tbep: file.allow_wildcard_tbep,
        allow_wildcard_quic: file.allow_wildcard_quic,
        allow_wildcard_metrics: file.allow_wildcard_metrics,
        server_certificate,
        server_private_key,
        client_ca,
        peers,
    })
}

fn production_regions_missing(
    databases: &[ProductionDatabase],
    placement: &ProductionPlacement,
) -> bool {
    databases.iter().any(|database| !placement.regions.contains_key(&database.region))
}

fn production_socket(value: &str, allow_wildcard: bool) -> Result<SocketAddr, ConfigError> {
    let address: SocketAddr = value.parse().map_err(|_| ConfigError::InvalidListenAddress)?;
    if address.port() == 0 || (address.ip().is_unspecified() && !allow_wildcard) {
        return Err(ConfigError::InvalidListenAddress);
    }
    Ok(address)
}

fn parse_release(file: &FileReleaseBinding) -> Result<ReleaseBinding, ConfigError> {
    if file.release.is_empty()
        || file.release.len() > MAX_RELEASE_BYTES
        || !file.release.is_ascii()
        || file.capabilities.is_empty()
        || file.capabilities.len() > MAX_CAPABILITIES
    {
        return Err(ConfigError::InvalidReleaseBinding);
    }
    let allowed = ["production", "tbep", "quic", "metrics", "oci-vault"];
    let capabilities = file.capabilities.iter().cloned().collect::<BTreeSet<_>>();
    if capabilities.len() != file.capabilities.len()
        || file.capabilities.iter().any(|capability| !allowed.contains(&capability.as_str()))
        || allowed.iter().any(|required| !capabilities.contains(*required))
    {
        return Err(ConfigError::InvalidReleaseBinding);
    }
    Ok(ReleaseBinding { release: file.release.clone(), capabilities })
}

fn production_path(path: &Path) -> Result<PathBuf, ConfigError> {
    if !path.is_absolute() || path.as_os_str().len() > MAX_PATH_BYTES {
        return Err(ConfigError::RelativeProductionPath);
    }
    let mut current = PathBuf::from(std::path::MAIN_SEPARATOR.to_string());
    for component in path.components() {
        match component {
            std::path::Component::RootDir => continue,
            std::path::Component::Normal(part) => current.push(part),
            _ => return Err(ConfigError::InvalidProductionPath),
        }
        if let Ok(metadata) = std::fs::symlink_metadata(&current) {
            if metadata.file_type().is_symlink() {
                return Err(ConfigError::UnsafeProductionPath);
            }
            #[cfg(unix)]
            {
                use std::os::unix::fs::PermissionsExt as _;
                if metadata.permissions().mode() & 0o022 != 0 {
                    return Err(ConfigError::UnsafeProductionPath);
                }
            }
        }
    }
    Ok(path.to_path_buf())
}

fn reject_nested_paths(paths: &[PathBuf]) -> Result<(), ConfigError> {
    for (index, left) in paths.iter().enumerate() {
        for right in paths.iter().skip(index + 1) {
            if left == right || left.starts_with(right) || right.starts_with(left) {
                return Err(ConfigError::DuplicateProductionPath);
            }
        }
    }
    Ok(())
}

/// Opens a file for reading without following a final symlink where the host
/// platform provides that flag. Metadata and bytes are then obtained from the
/// same descriptor, closing the metadata/read replacement race.
fn open_readonly(path: &Path) -> Result<File, ConfigError> {
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
    options.open(path).map_err(|error| {
        #[cfg(any(target_os = "linux", target_os = "android"))]
        if error.raw_os_error() == Some(40) {
            return ConfigError::UnsafeConfigurationFile;
        }
        ConfigError::ReadConfiguration(error)
    })
}

fn read_configuration(path: &Path) -> Result<String, ConfigError> {
    let mut file = open_readonly(path)?;
    let metadata = file.metadata().map_err(ConfigError::ReadConfiguration)?;
    if !metadata.is_file() {
        return Err(ConfigError::UnsafeConfigurationFile);
    }
    let expected_len =
        usize::try_from(metadata.len()).map_err(|_| ConfigError::ConfigurationTooLarge)?;
    if expected_len == 0 {
        return Err(ConfigError::EmptyConfiguration);
    }
    if metadata.len() > MAX_CONFIG_BYTES {
        return Err(ConfigError::ConfigurationTooLarge);
    }
    #[cfg(unix)]
    {
        use std::os::unix::fs::PermissionsExt as _;
        if metadata.permissions().mode() & 0o022 != 0 {
            return Err(ConfigError::UnsafeConfigurationFile);
        }
    }
    let mut bytes = Vec::with_capacity(expected_len);
    let read_limit = MAX_CONFIG_BYTES.checked_add(1).ok_or(ConfigError::ConfigurationTooLarge);
    (&mut file)
        .take(read_limit?)
        .read_to_end(&mut bytes)
        .map_err(ConfigError::ReadConfiguration)?;
    let after = file.metadata().map_err(ConfigError::ReadConfiguration)?;
    if bytes.len() > MAX_CONFIG_BYTES_USIZE {
        return Err(ConfigError::ConfigurationTooLarge);
    }
    if bytes.len() != expected_len || after.len() != metadata.len() {
        return Err(ConfigError::ConfigurationChanged);
    }
    String::from_utf8(bytes).map_err(|_| {
        ConfigError::ReadConfiguration(std::io::Error::new(
            std::io::ErrorKind::InvalidData,
            "configuration is not UTF-8",
        ))
    })
}

fn parse_public_api(
    section: Option<&FilePublicApi>,
    tbep_listen: SocketAddr,
    observability_listen: SocketAddr,
) -> Result<Option<PublicApiConfig>, ConfigError> {
    let Some(section) = section else {
        return Ok(None);
    };
    let listen = socket(&section.listen, "public_api.listen")?;
    if listen.port() == 0
        || listen == tbep_listen
        || listen == observability_listen
        || (listen.ip().is_unspecified() && !section.allow_wildcard)
    {
        return Err(ConfigError::InvalidListenAddress);
    }
    absolute(&section.data_root)?;
    if let Some(key_file) = &section.bootstrap_key_file {
        absolute(key_file)?;
    }
    if let Some(key_file) = &section.origin_proof_key_file {
        absolute(key_file)?;
    }
    if let Some(key_file) = &section.origin_proof_previous_key_file {
        absolute(key_file)?;
    }
    if let Some(key_file) = &section.capability_key_file {
        absolute(key_file)?;
    }
    if let Some(key_file) = &section.capability_previous_key_file {
        absolute(key_file)?;
    }
    // Every named region must parse; duplicates collapse (the count is
    // the durability class's input, not a placement).
    let named: std::collections::BTreeSet<String> = section.regions.iter().cloned().collect();
    for region in &named {
        terrabase::regions::RegionCode::from_text(region)
            .map_err(|_| ConfigError::InvalidRegion)?;
    }
    Ok(Some(PublicApiConfig {
        listen,
        data_root: section.data_root.clone(),
        bootstrap_key_file: section.bootstrap_key_file.clone(),
        origin_proof_key_file: section.origin_proof_key_file.clone(),
        origin_proof_previous_key_file: section.origin_proof_previous_key_file.clone(),
        capability_key_file: section.capability_key_file.clone(),
        capability_previous_key_file: section.capability_previous_key_file.clone(),
    }))
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
    /// The configuration was empty.
    EmptyConfiguration,
    /// The opened configuration changed while it was being read.
    ConfigurationChanged,
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
    /// The admitted private-member principal set was empty, duplicated, or invalid.
    InvalidMemberPrincipal,
    /// The UDS mode was broader than 0770 or omitted owner read/write.
    InvalidUdsMode,
    /// The mode selector was absent or not one of the named modes.
    InvalidMode,
    /// The production configuration failed a typed boundary invariant.
    InvalidProductionBoundary,
    /// A local key provider was selected for production.
    LocalProviderInProduction,
    /// The RF=2 placement generation or epoch was invalid.
    InvalidPlacement,
    /// A peer endpoint or exact workload identity was invalid.
    InvalidTransportBinding,
    /// Release or capability qualification was incomplete or unknown.
    InvalidReleaseBinding,
    /// A production path was malformed.
    InvalidProductionPath,
    /// A production path or one of its existing ancestors was unsafe.
    UnsafeProductionPath,
    /// Production paths were reused or nested.
    DuplicateProductionPath,
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
            Self::EmptyConfiguration => "the configuration file is empty",
            Self::ConfigurationChanged => "the configuration file changed while being read",
            Self::ParseConfiguration(_) => "the configuration file is not valid strict TOML",
            Self::RelativeProductionPath => "production file paths must be absolute",
            Self::InvalidListenAddress => "configured listener addresses are invalid",
            Self::InvalidRegion => "engine.region must be GRU, SFO, or FRA",
            Self::InvalidBound => "a production server bound is outside its accepted range",
            Self::InvalidMemberPrincipal => "member_principals is invalid",
            Self::InvalidUdsMode => "the local UDS mode is unsafe",
            Self::InvalidMode => "the server mode is invalid",
            Self::InvalidProductionBoundary => "the production configuration is invalid",
            Self::LocalProviderInProduction => "local key providers are unavailable in production",
            Self::InvalidPlacement => "the production placement is invalid",
            Self::InvalidTransportBinding => "the production transport binding is invalid",
            Self::InvalidReleaseBinding => "the release capability binding is invalid",
            Self::InvalidProductionPath => "a production path is invalid",
            Self::UnsafeProductionPath => "a production path is unsafe",
            Self::DuplicateProductionPath => "production paths must be distinct and non-nested",
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
    use std::sync::atomic::{AtomicUsize, Ordering};

    fn temporary_path(label: &str) -> PathBuf {
        static NEXT: AtomicUsize = AtomicUsize::new(0);
        std::env::temp_dir().join(format!(
            "terrabase-server-{label}-{}-{}",
            std::process::id(),
            NEXT.fetch_add(1, Ordering::Relaxed)
        ))
    }

    fn write_temporary(label: &str, bytes: &[u8]) -> PathBuf {
        let path = temporary_path(label);
        std::fs::write(&path, bytes).expect("temporary configuration writes");
        path
    }

    fn valid() -> String {
        r#"
[engine]
location = "/var/lib/terrabase"
tenant = "tenant-a"
project = "project-a"
database = "database-a"
instance_id = 7
region = "GRU"

[tbep]
listen = "0.0.0.0:8421"
allow_wildcard = true
server_certificate = "/run/secrets/terrabase.crt"
server_private_key = "/run/secrets/terrabase.key"
client_ca = "/run/secrets/member-ca.crt"
member_principals = ["spiffe://local/stack-member"]
max_message_bytes = 16777216
max_inflight = 128
request_timeout_millis = 30000
tls_handshake_timeout_millis = 5000
shutdown_grace_millis = 30000

[observability]
listen = "127.0.0.1:9464"
allow_wildcard = false
"#
        .to_owned()
    }

    fn production_valid() -> String {
        r#"
mode = "production"

[environment]
name = "prod"
cluster = "01ARZ3NDEKTSV4RRFFQ69G5FAV"
member = "01ARZ3NDEKTSV4RRFFQ69G5FAW"

[[databases]]
tenant = "01ARZ3NDEKTSV4RRFFQ69G5FAX"
project = "01ARZ3NDEKTSV4RRFFQ69G5FAY"
database = "01ARZ3NDEKTSV4RRFFQ69G5FAZ"
region = "GRU"
location = "/var/lib/terrabase/db-a"
policy_journal = "/var/lib/terrabase/policy-a"
backup_root = "/var/lib/terrabase/backup-a"
encryption_provider = "oci-vault"
encryption_key = "01ARZ3NDEKTSV4RRFFQ69G5FB0"
quotas = { max_storage_bytes = 1073741824, max_documents = 100000, max_inflight = 64 }
enabled = true

[placement]
generation = 4
epoch = 9
regions = [
  { region = "GRU", members = ["01ARZ3NDEKTSV4RRFFQ69G5FAW", "01ARZ3NDEKTSV4RRFFQ69G5FB1"] },
  { region = "SFO", members = ["01ARZ3NDEKTSV4RRFFQ69G5FB2", "01ARZ3NDEKTSV4RRFFQ69G5FB3"] },
]

[transport]
tbep_listen = "127.0.0.1:8421"
quic_listen = "127.0.0.1:7421"
metrics_listen = "127.0.0.1:9464"
server_certificate = "/run/secrets/terrabase.crt"
server_private_key = "/run/secrets/terrabase.key"
client_ca = "/run/secrets/member-ca.crt"
peers = [
  { member = "01ARZ3NDEKTSV4RRFFQ69G5FB1", endpoint = "127.0.0.1:7422", spiffe_identity = "spiffe://terrascale.tech/prod/terrabase-member/01ARZ3NDEKTSV4RRFFQ69G5FAV/01ARZ3NDEKTSV4RRFFQ69G5FB1" },
  { member = "01ARZ3NDEKTSV4RRFFQ69G5FB2", endpoint = "127.0.0.1:7423", spiffe_identity = "spiffe://terrascale.tech/prod/terrabase-member/01ARZ3NDEKTSV4RRFFQ69G5FAV/01ARZ3NDEKTSV4RRFFQ69G5FB2" },
  { member = "01ARZ3NDEKTSV4RRFFQ69G5FB3", endpoint = "127.0.0.1:7424", spiffe_identity = "spiffe://terrascale.tech/prod/terrabase-member/01ARZ3NDEKTSV4RRFFQ69G5FAV/01ARZ3NDEKTSV4RRFFQ69G5FB3" },
]
max_message_bytes = 16777216
max_inflight = 128
request_timeout_millis = 30000
tls_handshake_timeout_millis = 5000
shutdown_grace_millis = 30000

[release]
release = "terrabase-server-2026.08"
capabilities = ["production", "tbep", "quic", "metrics", "oci-vault"]
"#
        .to_owned()
    }

    #[test]
    fn production_configuration_builds_typed_catalog_and_bindings() {
        let config = ServerConfig::parse(&production_valid()).expect("production config");
        assert_eq!(config.mode, ServerMode::Production);
        let production = config.production.expect("typed production config");
        assert_eq!(production.databases.len(), 1);
        assert_eq!(production.placement.generation, 4);
        assert_eq!(production.placement.epoch, 9);
        assert_eq!(production.transport.peers.len(), 3);
        assert_eq!(production.release.capabilities.len(), 5);
        assert_eq!(production.databases[0].encryption_provider, KeyProviderIdentity::OciVault);
    }

    #[test]
    fn pending_control_requires_private_path_and_explicit_principals() {
        let suffix = r#"
[control]
proposal_journal = "/var/lib/terrabase/control/proposals.sqlite"
orchestrator_principals = ["spiffe://terrascale.tech/prod/orchestrator/gru-1"]

[keys]
socket = "/run/terrabase-keys/provider.sock"
orchestrator_uid = 1234
"#;
        let config = ServerConfig::parse(&(production_valid() + suffix)).expect("control policy");
        let production = config.production.unwrap();
        assert_eq!(production.control.unwrap().orchestrator_principals.len(), 1);
        assert_eq!(production.keys.unwrap().orchestrator_uid, 1234);
        let observe_only = suffix.replace("orchestrator_principals", "observer_principals");
        let observed = ServerConfig::parse(&(production_valid() + &observe_only)).expect("observer-only bootstrap");
        let policy = observed.production.unwrap().control.unwrap();
        assert!(policy.orchestrator_principals.is_empty());
        assert_eq!(policy.observer_principals.len(), 1);

        for changed in [
            suffix.replace(
                "spiffe://terrascale.tech/prod/orchestrator/gru-1",
                "not-a-spiffe-principal",
            ),
            suffix.replace("/var/lib/terrabase/control/proposals.sqlite", "relative.sqlite"),
            suffix.replace(
                "/var/lib/terrabase/control/proposals.sqlite",
                "/var/lib/terrabase/db-a/proposals.sqlite",
            ),
            suffix.replace("[\"spiffe://terrascale.tech/prod/orchestrator/gru-1\"]", "[]"),
        ] {
            assert!(ServerConfig::parse(&(production_valid() + &changed)).is_err());
        }
    }

    #[test]
    fn replication_peers_enumerate_as_lanes_and_fail_closed_when_absent() {
        let base = valid();
        let absent = ServerConfig::parse(&base).expect("the base configuration parses");
        assert!(
            absent.replication_lanes().is_empty(),
            "an absent [replication] section fails closed: no lanes, no replication"
        );

        let configured = base
            + r#"
[[replication.peers]]
region = "SFO"
address = "127.0.0.1:7202"

[[replication.peers]]
region = "FRA"
address = "127.0.0.1:7203"
"#;
        let config = ServerConfig::parse(&configured).expect("the peer list parses");
        let lanes = config.replication_lanes();
        assert_eq!(lanes.len(), 2);
        assert_eq!(lanes[0].region, Region::Sfo);
        assert_eq!(
            lanes[0].address,
            "127.0.0.1:7202".parse::<SocketAddr>().expect("the address parses")
        );
        assert_eq!(lanes[1].region, Region::Fra);
    }

    #[test]
    fn replication_peers_reject_own_region_unknown_regions_and_undialable_addresses() {
        let own = valid()
            + r#"
[[replication.peers]]
region = "GRU"
address = "127.0.0.1:7202"
"#;
        assert!(matches!(
            ServerConfig::parse(&own).map(|config| config.replication_lanes().to_owned()),
            Err(ConfigError::InvalidListenAddress)
        ));

        let unknown = valid()
            + r#"
[[replication.peers]]
region = "SYD"
address = "127.0.0.1:7202"
"#;
        assert!(matches!(ServerConfig::parse(&unknown), Err(ConfigError::InvalidRegion)));

        let wildcard = valid()
            + r#"
[[replication.peers]]
region = "SFO"
address = "0.0.0.0:7202"
"#;
        assert!(matches!(ServerConfig::parse(&wildcard), Err(ConfigError::InvalidListenAddress)));
    }

    #[test]
    fn production_rejects_local_keys_duplicate_paths_and_wildcards() {
        let local_key = production_valid()
            .replace("encryption_provider = \"oci-vault\"", "encryption_provider = \"local-file\"");
        assert!(matches!(
            ServerConfig::parse(&local_key),
            Err(ConfigError::LocalProviderInProduction)
        ));
        let duplicate = production_valid().replace(
            "backup_root = \"/var/lib/terrabase/backup-a\"",
            "backup_root = \"/var/lib/terrabase/db-a\"",
        );
        assert!(matches!(
            ServerConfig::parse(&duplicate),
            Err(ConfigError::DuplicateProductionPath)
        ));
        let wildcard = production_valid()
            .replace("tbep_listen = \"127.0.0.1:8421\"", "tbep_listen = \"0.0.0.0:8421\"");
        assert!(matches!(ServerConfig::parse(&wildcard), Err(ConfigError::InvalidListenAddress)));
    }

    #[test]
    fn production_binds_peer_endpoints_exactly() {
        let duplicate_endpoint = production_valid()
            .replace("endpoint = \"127.0.0.1:7423\"", "endpoint = \"127.0.0.1:7422\"");
        assert!(matches!(
            ServerConfig::parse(&duplicate_endpoint),
            Err(ConfigError::InvalidTransportBinding)
        ));
        let local_endpoint = production_valid()
            .replace("endpoint = \"127.0.0.1:7422\"", "endpoint = \"127.0.0.1:8421\"");
        assert!(matches!(
            ServerConfig::parse(&local_endpoint),
            Err(ConfigError::InvalidTransportBinding)
        ));
    }

    #[test]
    fn strict_production_configuration_parses() {
        let config = ServerConfig::parse(&valid()).expect("valid config");
        assert_eq!(config.region, Region::Gru);
        assert_eq!(config.max_inflight, 128);
        assert!(config.member_principals.contains("spiffe://local/stack-member"));
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
    fn member_principals_are_bounded_trimmed_and_unique() {
        for principal in [
            "spiffe://\u{0001}",
            " member",
            "member ",
            &"g".repeat(terrabase_tbep::grpc::MAX_PRINCIPAL_BYTES + 1),
        ] {
            let text = valid().replace("spiffe://local/stack-member", principal);
            assert!(
                matches!(
                    ServerConfig::parse(&text),
                    Err(ConfigError::InvalidMemberPrincipal | ConfigError::ParseConfiguration(_))
                ),
                "principal {principal:?}: {:?}",
                ServerConfig::parse(&text)
            );
        }
    }

    #[test]
    fn observability_can_bind_the_orchestrator_private_network() {
        let private_network = valid()
            .replace("127.0.0.1:9464", "0.0.0.0:9464")
            .replace("allow_wildcard = false", "allow_wildcard = true");
        let config = ServerConfig::parse(&private_network).expect("private-network metrics bind");
        assert_eq!(config.observability_listen, "0.0.0.0:9464".parse().expect("socket"));
    }

    #[test]
    fn wildcard_listeners_require_explicit_opt_in() {
        let tbep = valid().replace("allow_wildcard = true", "allow_wildcard = false");
        assert!(matches!(ServerConfig::parse(&tbep), Err(ConfigError::InvalidListenAddress)));

        let without_opt_in = valid().replace("127.0.0.1:9464", "0.0.0.0:9464");
        assert!(matches!(
            ServerConfig::parse(&without_opt_in),
            Err(ConfigError::InvalidListenAddress)
        ));
    }

    #[test]
    fn load_reads_one_regular_handle_and_rejects_empty_oversized_and_non_utf8() {
        let valid_path = write_temporary("config-valid", valid().as_bytes());
        let config = ServerConfig::load(&valid_path).expect("regular configuration loads");
        assert_eq!(config.region, Region::Gru);
        std::fs::remove_file(&valid_path).expect("temporary configuration removes");

        let empty_path = write_temporary("config-empty", b"");
        assert!(matches!(ServerConfig::load(&empty_path), Err(ConfigError::EmptyConfiguration)));
        std::fs::remove_file(&empty_path).expect("temporary configuration removes");

        let oversized_path =
            write_temporary("config-oversized", &vec![b'x'; MAX_CONFIG_BYTES_USIZE + 1]);
        assert!(matches!(
            ServerConfig::load(&oversized_path),
            Err(ConfigError::ConfigurationTooLarge)
        ));
        std::fs::remove_file(&oversized_path).expect("temporary configuration removes");

        let invalid_utf8_path = write_temporary("config-utf8", &[0xff, 0xfe]);
        let error = ServerConfig::load(&invalid_utf8_path).expect_err("invalid UTF-8 refuses");
        assert!(matches!(error, ConfigError::ReadConfiguration(_)));
        assert!(!error.to_string().contains(invalid_utf8_path.to_string_lossy().as_ref()));
        std::fs::remove_file(&invalid_utf8_path).expect("temporary configuration removes");
    }

    #[cfg(unix)]
    #[test]
    fn load_rejects_symlink_and_group_or_other_writable_configuration() {
        use std::os::unix::fs::PermissionsExt as _;

        let target = write_temporary("config-target", valid().as_bytes());
        let link = temporary_path("config-link");
        std::os::unix::fs::symlink(&target, &link).expect("temporary configuration symlink");
        assert!(matches!(
            ServerConfig::load(&link),
            Err(ConfigError::UnsafeConfigurationFile | ConfigError::ReadConfiguration(_))
        ));
        std::fs::remove_file(&link).expect("temporary symlink removes");

        let writable = write_temporary("config-writable", valid().as_bytes());
        std::fs::set_permissions(&writable, std::fs::Permissions::from_mode(0o666))
            .expect("temporary configuration permissions");
        assert!(matches!(ServerConfig::load(&writable), Err(ConfigError::UnsafeConfigurationFile)));
        std::fs::remove_file(&target).expect("temporary configuration removes");
        std::fs::remove_file(&writable).expect("temporary configuration removes");
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
