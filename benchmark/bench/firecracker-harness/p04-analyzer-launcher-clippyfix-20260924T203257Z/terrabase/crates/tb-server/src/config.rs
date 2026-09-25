use std::net::{Ipv4Addr, SocketAddrV4};
use std::path::{Path, PathBuf};

use tb_core::{
    BudgetConfig, ConfigError as BudgetConfigError, MemoryBudget, ProcessBudgetAlreadyInitialized,
    TenantId, WorkerId, initialize_process_memory_budget,
};
use tb_storage::StorageConfig;

/// Default port for the loopback-only administrative and metrics listener.
pub const DEFAULT_LISTEN_PORT: u16 = 9464;

/// Minimum usable admission budget for default local-storage startup and recovery.
///
/// This floor covers the default storage working-set bounds conservatively. A
/// persisted shard can still require additional memory during recovery, which
/// `Storage::open` validates through the process budget before admitting it.
pub const MIN_USABLE_MEMORY_BYTES: usize = 64 * 1024 * 1024;

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
}
