use std::fs::File;
use std::io::{self, Read};
use std::net::{Ipv4Addr, SocketAddr, SocketAddrV4};
use std::path::{Path, PathBuf};

use serde::Deserialize;
use tb_core::{
    Attribution, BudgetConfig, CollectionId, ConfigError as BudgetConfigError, DatabaseId,
    MemoryBudget, MemoryCategory, OriginId, ProcessBudgetAlreadyInitialized, ReplicaId, ShardId,
    TenantId, WorkerId, initialize_process_memory_budget,
};
use tb_document::{DocumentStoreConfig, LocalSchemaBinding};
use tb_replication::{ConfigurationId, MAX_EVENT_CHANGES, MAX_WAL_GROUP_BYTES, ReplicationLimits};
use tb_storage::{LocalDocumentMode, StorageConfig};

/// Default port for the loopback-only administrative and metrics listener.
pub const DEFAULT_LISTEN_PORT: u16 = 9464;

/// Minimum usable admission budget for default local-storage startup and recovery.
///
/// This floor covers the default storage working-set bounds conservatively. A
/// persisted shard can still require additional memory during recovery, which
/// `Storage::open` validates through the process budget before admitting it.
pub const MIN_USABLE_MEMORY_BYTES: usize = 64 * 1024 * 1024;

const MAX_LOCAL_V1_CONFIG_BYTES: usize = 256 * 1024;
const MAX_CLIENT_CONFIG_BYTES: usize = 16 * 1024;
const DEFAULT_CLIENT_LISTEN_ADDR: &str = "127.0.0.1:9465";
const DEFAULT_CLIENT_HANDSHAKE_TIMEOUT_MS: u64 = 5_000;

/// Configuration for one local durable shard and its administrative listener.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ServerConfig {
    data_dir: PathBuf,
    listen_port: u16,
    memory_limit_bytes: usize,
    memory_headroom_bytes: usize,
}

impl ServerConfig {
    /// Validate explicit local-server settings without touching the data directory.
    ///
    /// The listener address is always `127.0.0.1`; callers cannot configure a
    /// non-loopback bind through this type. Port zero is reserved for test-only
    /// listener construction.
    ///
    /// # Errors
    ///
    /// Returns [`ServerConfigError`] for an empty data path, port zero, invalid
    /// memory headroom, or a usable budget below the startup floor.
    pub fn new(
        data_dir: impl Into<PathBuf>,
        listen_port: u16,
        memory_limit_bytes: usize,
        memory_headroom_bytes: usize,
    ) -> Result<Self, ServerConfigError> {
        let data_dir = data_dir.into();
        if data_dir.as_os_str().is_empty() {
            return Err(ServerConfigError::EmptyDataDirectory);
        }
        if listen_port == 0 {
            return Err(ServerConfigError::EphemeralPortNotAllowed);
        }
        if memory_headroom_bytes == 0 {
            return Err(ServerConfigError::ZeroMemoryHeadroom);
        }
        if memory_headroom_bytes >= memory_limit_bytes {
            return Err(ServerConfigError::HeadroomNotBelowLimit);
        }
        let usable_bytes = memory_limit_bytes
            .checked_sub(memory_headroom_bytes)
            .ok_or(ServerConfigError::HeadroomNotBelowLimit)?;
        if usable_bytes < MIN_USABLE_MEMORY_BYTES {
            return Err(ServerConfigError::InsufficientUsableMemory {
                configured: usable_bytes,
                minimum: MIN_USABLE_MEMORY_BYTES,
            });
        }
        Ok(Self {
            data_dir,
            listen_port,
            memory_limit_bytes,
            memory_headroom_bytes,
        })
    }

    /// Shard directory supplied by the operator.
    #[must_use]
    pub fn data_dir(&self) -> &Path {
        &self.data_dir
    }

    /// Port on the fixed IPv4 loopback listener.
    #[must_use]
    pub const fn listen_port(&self) -> u16 {
        self.listen_port
    }

    /// Process admission limit, including explicit headroom.
    #[must_use]
    pub const fn memory_limit_bytes(&self) -> usize {
        self.memory_limit_bytes
    }

    /// Memory excluded from the engine admission budget for runtime and allocator overhead.
    #[must_use]
    pub const fn memory_headroom_bytes(&self) -> usize {
        self.memory_headroom_bytes
    }

    /// Fixed loopback address; there is intentionally no configurable bind host.
    #[must_use]
    pub const fn listen_addr(&self) -> SocketAddrV4 {
        SocketAddrV4::new(Ipv4Addr::LOCALHOST, self.listen_port)
    }

    /// Build the process memory-budget configuration after settings validation.
    ///
    /// # Errors
    ///
    /// Returns the core configuration error if the memory limits are invalid.
    pub fn memory_budget_config(&self) -> Result<BudgetConfig, BudgetConfigError> {
        BudgetConfig::new(self.memory_limit_bytes, self.memory_headroom_bytes)
    }

    /// Initialize the one process-wide memory controller before storage opens.
    ///
    /// # Errors
    ///
    /// Returns an error if budget initialization failed or a process budget was
    /// already installed.
    pub fn initialize_memory_budget(&self) -> Result<MemoryBudget, ServerConfigError> {
        let config = self.memory_budget_config()?;
        let budget = initialize_process_memory_budget(config)?;
        Ok(budget.clone())
    }

    /// Create the one shard configuration with private, process-local accounting identity.
    #[must_use]
    pub fn storage_config(&self) -> StorageConfig {
        StorageConfig::local(&self.data_dir, SYSTEM_TENANT_ID, SYSTEM_WORKER_ID)
    }

    /// Create storage settings attributed to the tenant pinned by local-v1.
    #[must_use]
    pub(crate) fn local_v1_storage_config(&self, tenant: TenantId) -> StorageConfig {
        StorageConfig::local(&self.data_dir, tenant, SYSTEM_WORKER_ID)
    }

    /// Fixed low-cardinality attribution for local-v1 runtime leases.
    #[must_use]
    pub(crate) const fn local_v1_attribution(tenant: TenantId) -> Attribution {
        Attribution {
            tenant,
            worker: SYSTEM_WORKER_ID,
            category: MemoryCategory::RuntimeOverhead,
        }
    }
}

/// Trusted, preprovisioned identity and active collection bindings for one
/// local-v1 document shard.
#[derive(Clone, Debug)]
pub(crate) struct LocalV1Config {
    pub(crate) tenant: TenantId,
    pub(crate) database: DatabaseId,
    pub(crate) shard: ShardId,
    pub(crate) origin: OriginId,
    pub(crate) epoch: tb_core::MembershipEpoch,
    pub(crate) configuration: ConfigurationId,
    pub(crate) collections: Vec<LocalSchemaBinding>,
}

impl LocalV1Config {
    /// Return the exact durable mode marker expected for this process.
    #[must_use]
    pub(crate) const fn mode(&self) -> LocalDocumentMode {
        LocalDocumentMode::new(
            self.tenant,
            self.database,
            self.shard,
            self.origin,
            self.epoch,
            self.configuration.to_bytes(),
        )
    }

    /// Compose the document owner using local-v1's configured tenant for all
    /// memory and storage attribution.
    #[must_use]
    pub(crate) fn document_store_config(&self, server: &ServerConfig) -> DocumentStoreConfig {
        DocumentStoreConfig {
            storage: server.local_v1_storage_config(self.tenant),
            database: self.database,
            shard: self.shard,
            origin: self.origin,
            epoch: self.epoch,
            configuration: self.configuration,
            replication: ReplicationLimits {
                max_origins: 64,
                max_changes: MAX_EVENT_CHANGES,
                max_siblings: 64,
                max_value_bytes: tb_core::MAX_VALUE_BYTES,
                max_payload_bytes: MAX_WAL_GROUP_BYTES,
            },
        }
    }
}

/// Strict client listener settings. The three file paths hold all TLS and key
/// material outside arguments and environment variables.
#[derive(Clone, Debug, Eq, PartialEq)]
pub(crate) struct ClientConfig {
    listen_addr: SocketAddr,
    certificate_chain: PathBuf,
    private_key: PathBuf,
    api_key_registry: PathBuf,
    handshake_timeout: std::time::Duration,
}

impl ClientConfig {
    pub(crate) fn listen_addr(&self) -> SocketAddr {
        self.listen_addr
    }

    pub(crate) fn certificate_chain(&self) -> &Path {
        &self.certificate_chain
    }

    pub(crate) fn private_key(&self) -> &Path {
        &self.private_key
    }

    pub(crate) fn api_key_registry(&self) -> &Path {
        &self.api_key_registry
    }

    pub(crate) const fn handshake_timeout(&self) -> std::time::Duration {
        self.handshake_timeout
    }

    /// Require a concrete address and an explicit opt-in for nonloopback IPs.
    pub(crate) fn validate_bind(&self, allow_nonloopback: bool) -> Result<(), ClientConfigError> {
        if self.listen_addr.ip().is_unspecified() {
            return Err(ClientConfigError::WildcardListener);
        }
        if !self.listen_addr.ip().is_loopback() && !allow_nonloopback {
            return Err(ClientConfigError::NonLoopbackListener);
        }
        Ok(())
    }
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct LocalV1File {
    version: u64,
    tenant: String,
    database: String,
    shard: String,
    replica: String,
    incarnation: String,
    epoch: String,
    configuration: String,
    collections: Vec<LocalCollectionFile>,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct LocalCollectionFile {
    collection: String,
    generation: String,
    digest: String,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct ClientFile {
    version: u64,
    listen_addr: Option<String>,
    certificate_chain: String,
    private_key: String,
    api_key_registry: String,
    handshake_timeout_ms: Option<u64>,
}

/// Load the strict, bounded trusted local-v1 configuration file.
pub(crate) fn load_local_v1_config(path: &Path) -> Result<LocalV1Config, ModeConfigError> {
    let bytes = read_bounded(path, MAX_LOCAL_V1_CONFIG_BYTES)?;
    parse_local_v1_config(&bytes)
}

/// Load strict client TLS/auth paths and listener policy before shard open.
pub(crate) fn load_client_config(path: &Path) -> Result<ClientConfig, ClientConfigError> {
    let bytes = read_bounded(path, MAX_CLIENT_CONFIG_BYTES).map_err(ClientConfigError::File)?;
    parse_client_config(&bytes)
}

fn parse_local_v1_config(bytes: &[u8]) -> Result<LocalV1Config, ModeConfigError> {
    let file: LocalV1File = serde_json::from_slice(bytes).map_err(|_| ModeConfigError::Invalid)?;
    if file.version != 1
        || file.collections.is_empty()
        || file.collections.len() > MAX_EVENT_CHANGES
    {
        return Err(ModeConfigError::Invalid);
    }
    let tenant = TenantId::parse_canonical(&file.tenant).map_err(|_| ModeConfigError::Invalid)?;
    let database =
        DatabaseId::parse_canonical(&file.database).map_err(|_| ModeConfigError::Invalid)?;
    let shard = ShardId::new(parse_canonical_u64(&file.shard).ok_or(ModeConfigError::Invalid)?);
    let replica_id =
        ReplicaId::parse_canonical(&file.replica).map_err(|_| ModeConfigError::Invalid)?;
    let incarnation = parse_lower_hex::<16>(&file.incarnation).ok_or(ModeConfigError::Invalid)?;
    let epoch = parse_canonical_u64(&file.epoch)
        .filter(|epoch| *epoch != 0)
        .map(tb_core::MembershipEpoch::new)
        .ok_or(ModeConfigError::Invalid)?;
    let configuration_bytes =
        parse_lower_hex::<32>(&file.configuration).ok_or(ModeConfigError::Invalid)?;
    let configuration =
        ConfigurationId::from_bytes(configuration_bytes).map_err(|_| ModeConfigError::Invalid)?;
    if incarnation == [0; 16] {
        return Err(ModeConfigError::Invalid);
    }

    let mut collections = Vec::with_capacity(file.collections.len());
    for entry in file.collections {
        let collection = CollectionId::parse_canonical(&entry.collection)
            .map_err(|_| ModeConfigError::Invalid)?;
        if collections
            .last()
            .is_some_and(|previous: &LocalSchemaBinding| previous.collection >= collection)
        {
            return Err(ModeConfigError::Invalid);
        }
        let generation = parse_canonical_u64(&entry.generation)
            .filter(|generation| *generation != 0)
            .ok_or(ModeConfigError::Invalid)?;
        let schema_digest = parse_lower_hex::<32>(&entry.digest).ok_or(ModeConfigError::Invalid)?;
        collections.push(LocalSchemaBinding {
            collection,
            generation,
            schema_digest,
        });
    }

    Ok(LocalV1Config {
        tenant,
        database,
        shard,
        origin: OriginId {
            replica_id,
            incarnation,
        },
        epoch,
        configuration,
        collections,
    })
}

fn parse_client_config(bytes: &[u8]) -> Result<ClientConfig, ClientConfigError> {
    let file: ClientFile = serde_json::from_slice(bytes).map_err(|_| ClientConfigError::Invalid)?;
    if file.version != 1 {
        return Err(ClientConfigError::Invalid);
    }
    let listen_addr = file
        .listen_addr
        .as_deref()
        .unwrap_or(DEFAULT_CLIENT_LISTEN_ADDR)
        .parse::<SocketAddr>()
        .map_err(|_| ClientConfigError::Invalid)?;
    let certificate_chain = absolute_path(file.certificate_chain)?;
    let private_key = absolute_path(file.private_key)?;
    let api_key_registry = absolute_path(file.api_key_registry)?;
    let timeout = file
        .handshake_timeout_ms
        .unwrap_or(DEFAULT_CLIENT_HANDSHAKE_TIMEOUT_MS);
    if !(1_000..=10_000).contains(&timeout) {
        return Err(ClientConfigError::Invalid);
    }
    Ok(ClientConfig {
        listen_addr,
        certificate_chain,
        private_key,
        api_key_registry,
        handshake_timeout: std::time::Duration::from_millis(timeout),
    })
}

fn absolute_path(value: String) -> Result<PathBuf, ClientConfigError> {
    let path = PathBuf::from(value);
    if path.is_absolute() {
        Ok(path)
    } else {
        Err(ClientConfigError::Invalid)
    }
}

fn read_bounded(path: &Path, max_bytes: usize) -> Result<Vec<u8>, ModeConfigError> {
    let file = File::open(path).map_err(ModeConfigError::Io)?;
    let metadata = file.metadata().map_err(ModeConfigError::Io)?;
    if !metadata.is_file() || metadata.len() > max_bytes as u64 {
        return Err(ModeConfigError::TooLargeOrNotFile);
    }
    let capacity =
        usize::try_from(metadata.len()).map_err(|_| ModeConfigError::TooLargeOrNotFile)?;
    let mut bytes = Vec::with_capacity(capacity);
    file.take(max_bytes as u64 + 1)
        .read_to_end(&mut bytes)
        .map_err(ModeConfigError::Io)?;
    if bytes.len() > max_bytes {
        return Err(ModeConfigError::TooLargeOrNotFile);
    }
    Ok(bytes)
}

fn parse_canonical_u64(value: &str) -> Option<u64> {
    let bytes = value.as_bytes();
    if bytes.is_empty()
        || (bytes.len() > 1 && bytes[0] == b'0')
        || !bytes.iter().all(u8::is_ascii_digit)
    {
        return None;
    }
    value.parse().ok()
}

fn parse_lower_hex<const N: usize>(value: &str) -> Option<[u8; N]> {
    if value.len() != N * 2 {
        return None;
    }
    let mut bytes = [0_u8; N];
    for (index, pair) in value.as_bytes().as_chunks::<2>().0.iter().enumerate() {
        let high = lower_hex_nibble(pair[0])?;
        let low = lower_hex_nibble(pair[1])?;
        bytes[index] = (high << 4) | low;
    }
    Some(bytes)
}

fn lower_hex_nibble(byte: u8) -> Option<u8> {
    match byte {
        b'0'..=b'9' => Some(byte - b'0'),
        b'a'..=b'f' => Some(byte - b'a' + 10),
        _ => None,
    }
}

/// Trusted local-v1 configuration failure without exposing file contents.
#[derive(Debug, thiserror::Error)]
pub(crate) enum ModeConfigError {
    #[error("cannot read local-v1 configuration")]
    Io(#[source] io::Error),
    #[error("local-v1 configuration is not a bounded regular file")]
    TooLargeOrNotFile,
    #[error("local-v1 configuration has invalid format or identity")]
    Invalid,
}

/// Client listener configuration failure without exposing TLS or key data.
#[derive(Debug, thiserror::Error)]
pub(crate) enum ClientConfigError {
    #[error(transparent)]
    File(#[from] ModeConfigError),
    #[error("client configuration has invalid format or paths")]
    Invalid,
    #[error("client listener must use a concrete address")]
    WildcardListener,
    #[error("nonloopback client listener requires --client-allow-nonloopback")]
    NonLoopbackListener,
}

// Storage requires attribution even for this single local-only process. These
// private fixed IDs are accounting keys only and are never exposed as labels.
const SYSTEM_TENANT_ID: TenantId = TenantId::from_bytes([0; 16]);
const SYSTEM_WORKER_ID: WorkerId = WorkerId(0);

/// Configuration or one-time initialization failure at the server boundary.
#[derive(Debug, thiserror::Error)]
pub enum ServerConfigError {
    /// The configured shard path is empty.
    #[error("data directory must not be empty")]
    EmptyDataDirectory,
    /// Port zero is not available to the production listener.
    #[error("listen port zero is reserved for the test harness")]
    EphemeralPortNotAllowed,
    /// Headroom must explicitly reserve some process memory.
    #[error("memory headroom must be greater than zero")]
    ZeroMemoryHeadroom,
    /// Headroom must be strictly smaller than the process limit.
    #[error("memory headroom must be smaller than the process memory limit")]
    HeadroomNotBelowLimit,
    /// The remaining engine budget is below the conservative local startup floor.
    #[error("usable memory budget is {configured} bytes; at least {minimum} bytes are required")]
    InsufficientUsableMemory { configured: usize, minimum: usize },
    /// The core rejected the process memory-budget configuration.
    #[error(transparent)]
    BudgetConfig(#[from] BudgetConfigError),
    /// A process-wide budget was already initialized.
    #[error(transparent)]
    ProcessBudgetAlreadyInitialized(#[from] ProcessBudgetAlreadyInitialized),
}

#[cfg(test)]
mod tests {
    use std::net::{Ipv4Addr, SocketAddrV4};

    use super::*;

    fn config(
        port: u16,
        memory_limit_bytes: usize,
        memory_headroom_bytes: usize,
    ) -> Result<ServerConfig, ServerConfigError> {
        ServerConfig::new(
            "/var/lib/terrabase/shard-0",
            port,
            memory_limit_bytes,
            memory_headroom_bytes,
        )
    }

    #[test]
    fn server_config_binds_only_loopback_and_uses_the_admin_port_default() {
        let config = config(
            DEFAULT_LISTEN_PORT,
            MIN_USABLE_MEMORY_BYTES + 8 * 1024 * 1024,
            8 * 1024 * 1024,
        )
        .expect("valid local server configuration");

        assert_eq!(
            config.listen_addr(),
            SocketAddrV4::new(Ipv4Addr::LOCALHOST, 9464)
        );
    }

    #[test]
    fn server_config_rejects_undersized_memory_limits() {
        assert!(matches!(
            config(
                DEFAULT_LISTEN_PORT,
                MIN_USABLE_MEMORY_BYTES + 8 * 1024 * 1024 - 1,
                8 * 1024 * 1024,
            ),
            Err(ServerConfigError::InsufficientUsableMemory { .. })
        ));
        assert!(matches!(
            config(DEFAULT_LISTEN_PORT, 8 * 1024 * 1024, 8 * 1024 * 1024),
            Err(ServerConfigError::HeadroomNotBelowLimit)
        ));
        assert!(matches!(
            config(
                DEFAULT_LISTEN_PORT,
                usize::MAX,
                usize::MAX - MIN_USABLE_MEMORY_BYTES + 1,
            ),
            Err(ServerConfigError::InsufficientUsableMemory { .. })
        ));
    }

    #[test]
    fn server_config_requires_explicit_positive_headroom_and_nonzero_port() {
        assert!(matches!(
            config(DEFAULT_LISTEN_PORT, MIN_USABLE_MEMORY_BYTES, 0),
            Err(ServerConfigError::ZeroMemoryHeadroom)
        ));
        assert!(matches!(
            config(0, MIN_USABLE_MEMORY_BYTES + 1, 1),
            Err(ServerConfigError::EphemeralPortNotAllowed)
        ));
    }

    #[test]
    fn server_config_does_not_touch_the_data_directory() {
        let path = std::env::temp_dir().join(format!("terrabase-config-{}", TenantId::new()));
        let config = ServerConfig::new(
            &path,
            DEFAULT_LISTEN_PORT,
            MIN_USABLE_MEMORY_BYTES + 1024,
            1024,
        )
        .expect("valid config");

        assert_eq!(config.data_dir(), path.as_path());
        assert!(!path.exists());
    }

    fn valid_local_v1_json() -> String {
        format!(
            r#"{{"version":1,"tenant":"{}","database":"{}","shard":"0","replica":"{}","incarnation":"{}","epoch":"1","configuration":"{}","collections":[{{"collection":"{}","generation":"2","digest":"{}"}}]}}"#,
            TenantId::from_bytes([1; 16]),
            DatabaseId::from_bytes([2; 16]),
            ReplicaId::from_bytes([3; 16]),
            "04".repeat(16),
            "05".repeat(32),
            CollectionId::from_bytes([6; 16]),
            "07".repeat(32),
        )
    }

    #[test]
    fn local_v1_config_parses_exact_identity_and_sorted_active_bindings() {
        let parsed = parse_local_v1_config(valid_local_v1_json().as_bytes())
            .expect("valid local-v1 configuration");
        assert_eq!(parsed.tenant, TenantId::from_bytes([1; 16]));
        assert_eq!(parsed.database, DatabaseId::from_bytes([2; 16]));
        assert_eq!(parsed.shard, ShardId::new(0));
        assert_eq!(parsed.origin.replica_id, ReplicaId::from_bytes([3; 16]));
        assert_eq!(parsed.origin.incarnation, [4; 16]);
        assert_eq!(parsed.epoch.get(), 1);
        assert_eq!(parsed.configuration.to_bytes(), [5; 32]);
        assert_eq!(parsed.collections[0].generation, 2);
        assert_eq!(parsed.collections[0].schema_digest, [7; 32]);
        assert_eq!(parsed.mode().tenant(), parsed.tenant);
    }

    #[test]
    fn local_v1_config_rejects_noncanonical_or_ambiguous_bindings() {
        let valid = valid_local_v1_json();
        assert!(
            parse_local_v1_config(
                valid
                    .replace("\"shard\":\"0\"", "\"shard\":\"00\"")
                    .as_bytes()
            )
            .is_err()
        );
        assert!(
            parse_local_v1_config(
                valid
                    .replace("\"epoch\":\"1\"", "\"epoch\":\"0\"")
                    .as_bytes()
            )
            .is_err()
        );
        assert!(
            parse_local_v1_config(
                valid
                    .replace("\"configuration\":\"05", "\"configuration\":\"A5")
                    .as_bytes()
            )
            .is_err()
        );
        assert!(
            parse_local_v1_config(
                valid
                    .replace("\"version\":1", "\"version\":1,\"unexpected\":true")
                    .as_bytes()
            )
            .is_err()
        );
        assert!(
            parse_local_v1_config(
                valid
                    .replace("\"version\":1", "\"version\":1,\"version\":1")
                    .as_bytes()
            )
            .is_err()
        );
    }

    #[test]
    fn client_config_defaults_loopback_timeout_and_requires_absolute_paths() {
        let parsed = parse_client_config(
            br#"{"version":1,"certificate_chain":"/etc/terrabase/tls.pem","private_key":"/etc/terrabase/tls.key","api_key_registry":"/etc/terrabase/keys.json"}"#,
        )
        .expect("valid client configuration");
        assert_eq!(parsed.listen_addr(), "127.0.0.1:9465".parse().unwrap());
        assert_eq!(
            parsed.handshake_timeout(),
            std::time::Duration::from_secs(5)
        );
        assert!(parsed.validate_bind(false).is_ok());

        assert!(parse_client_config(
            br#"{"version":1,"certificate_chain":"relative/tls.pem","private_key":"/x/key","api_key_registry":"/x/keys"}"#,
        )
        .is_err());
        assert!(parse_client_config(
            br#"{"version":1,"certificate_chain":"/x/cert","private_key":"/x/key","api_key_registry":"/x/keys","handshake_timeout_ms":10001}"#,
        )
        .is_err());
    }

    #[test]
    fn client_config_requires_explicit_nonloopback_opt_in_and_never_allows_wildcard() {
        let public = parse_client_config(
            br#"{"version":1,"listen_addr":"192.0.2.10:9465","certificate_chain":"/x/cert","private_key":"/x/key","api_key_registry":"/x/keys"}"#,
        )
        .expect("valid concrete listener configuration");
        assert!(matches!(
            public.validate_bind(false),
            Err(ClientConfigError::NonLoopbackListener)
        ));
        assert!(public.validate_bind(true).is_ok());

        let wildcard = parse_client_config(
            br#"{"version":1,"listen_addr":"0.0.0.0:9465","certificate_chain":"/x/cert","private_key":"/x/key","api_key_registry":"/x/keys"}"#,
        )
        .expect("wildcard is rejected at bind validation");
        assert!(matches!(
            wildcard.validate_bind(true),
            Err(ClientConfigError::WildcardListener)
        ));
    }
}
