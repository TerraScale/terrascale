use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Mutex, MutexGuard};

use tb_core::MemoryBudget;
use tb_storage::{Storage, StorageError};

use crate::{
    ClientAdmissions, Lifecycle, LifecycleError, MemoryMetricsSnapshot, ServerConfig,
    ServerConfigError,
};

/// Process state owning exactly one recovered local shard and its budget.
pub struct LocalServerState {
    // Raw mode retains its storage owner here. In local-v1, the dedicated
    // document worker owns the sole DocumentStore and this remains `None`.
    _storage: Option<Storage>,
    budget: MemoryBudget,
    lifecycle: Lifecycle,
    replication_ready: Option<AtomicBool>,
    document_ready: AtomicBool,
    document_readiness_gate: Mutex<()>,
    client_admissions: Option<Arc<ClientAdmissions>>,
}

impl LocalServerState {
    /// Initialize the process budget, recover one local shard, then mark readiness.
    ///
    /// Configuration validation is performed before this method is called, so
    /// an undersized budget, invalid port, or empty path fails before storage can
    /// create the data directory. Recovery may still reject persisted state that
    /// exceeds its configured storage limits; that failure is returned directly.
    ///
    /// # Errors
    ///
    /// Returns an explicit configuration, one-time budget initialization, storage
    /// recovery, or lifecycle error. The caller must log the error and exit.
    pub fn open(config: &ServerConfig) -> Result<Self, ServerStartError> {
        let budget = config.initialize_memory_budget()?;
        Self::open_with_budget(config, budget)
    }

    /// Recover local storage using the process budget already initialized by
    /// the caller. Networked startup uses this after accepted authority opens,
    /// so local storage, authority, and receivers share one accounting domain.
    ///
    /// # Errors
    /// Returns storage recovery or lifecycle errors. The caller owns the
    /// supplied budget and must ensure it is the one process-wide controller.
    pub fn open_with_budget(
        config: &ServerConfig,
        budget: MemoryBudget,
    ) -> Result<Self, ServerStartError> {
        Self::open_with_budget_mode(config, budget, false)
    }

    /// Recover local storage while keeping network readiness closed until the
    /// replication listener has started serving.
    ///
    /// # Errors
    /// Returns storage recovery or lifecycle errors. The caller owns the
    /// supplied budget and must ensure it is the one process-wide controller.
    pub fn open_network_with_budget(
        config: &ServerConfig,
        budget: MemoryBudget,
    ) -> Result<Self, ServerStartError> {
        Self::open_with_budget_mode(config, budget, true)
    }

    /// Start a signed-v2 document member after its exclusive document owner
    /// recovered the shard and all remote inboxes. This state opens no raw
    /// Storage handle; the replication listener still gates readiness.
    ///
    /// # Errors
    /// Returns a lifecycle error if the starting state cannot become ready.
    pub fn open_document_member_with_budget(
        budget: MemoryBudget,
    ) -> Result<Self, ServerStartError> {
        Self::open_document_member_with_admissions(budget, None)
    }

    pub(crate) fn open_document_member_with_admissions(
        budget: MemoryBudget,
        client_admissions: Option<Arc<ClientAdmissions>>,
    ) -> Result<Self, ServerStartError> {
        let lifecycle = Lifecycle::new();
        lifecycle.mark_ready()?;
        Ok(Self {
            _storage: None,
            budget,
            lifecycle,
            replication_ready: Some(AtomicBool::new(false)),
            document_ready: AtomicBool::new(true),
            document_readiness_gate: Mutex::new(()),
            client_admissions,
        })
    }

    fn open_with_budget_mode(
        config: &ServerConfig,
        budget: MemoryBudget,
        require_replication_ready: bool,
    ) -> Result<Self, ServerStartError> {
        let storage = Storage::open(config.storage_config(), budget.clone())?;
        let lifecycle = Lifecycle::new();
        lifecycle.mark_ready()?;
        Ok(Self {
            _storage: Some(storage),
            budget,
            lifecycle,
            replication_ready: require_replication_ready.then(|| AtomicBool::new(false)),
            document_ready: AtomicBool::new(true),
            document_readiness_gate: Mutex::new(()),
            client_admissions: None,
        })
    }

    /// Construct local-v1 lifecycle state before opening the document owner.
    /// The caller marks it ready only after strict marker and schema preflight.
    pub(crate) fn starting_local_v1(
        budget: MemoryBudget,
        client_admissions: Arc<ClientAdmissions>,
    ) -> Self {
        Self {
            _storage: None,
            budget,
            lifecycle: Lifecycle::new(),
            replication_ready: None,
            document_ready: AtomicBool::new(true),
            document_readiness_gate: Mutex::new(()),
            client_admissions: Some(client_admissions),
        }
    }

    /// Complete startup after the local document owner verified its marker and
    /// all configured active schema bindings.
    pub(crate) fn mark_ready(&self) -> Result<(), LifecycleError> {
        self.lifecycle.mark_ready()
    }

    /// Whether local recovery completed and the process is accepting work.
    #[must_use]
    pub fn is_ready(&self) -> bool {
        self.lifecycle.is_ready()
            && self.document_ready.load(Ordering::Acquire)
            && self
                .client_admissions
                .as_ref()
                .is_none_or(|admissions| admissions.is_open())
            && self
                .replication_ready
                .as_ref()
                .is_none_or(|ready| ready.load(Ordering::Acquire))
    }

    /// Open the network-mode readiness gate after the replication server starts.
    pub fn mark_replication_ready(&self) {
        if self.lifecycle.is_ready()
            && let Some(ready) = &self.replication_ready
        {
            ready.store(true, Ordering::Release);
        }
    }

    /// Whether the network receiver has started accepting connections.
    #[must_use]
    pub fn replication_listener_ready(&self) -> bool {
        self.replication_ready
            .as_ref()
            .is_some_and(|ready| ready.load(Ordering::Acquire))
    }

    /// Whether the process is healthy enough to finish health checks while draining.
    #[must_use]
    pub fn is_live(&self) -> bool {
        self.lifecycle.is_live()
    }

    /// Stop readiness before the listener stops accepting requests.
    pub fn begin_draining(&self) -> bool {
        self.clear_replication_readiness();
        self.clear_document_readiness();
        self.lifecycle.begin_draining()
    }

    /// Mark an unrecoverable runtime failure so health and readiness fail closed.
    pub fn mark_failed(&self) {
        self.clear_replication_readiness();
        self.clear_document_readiness();
        self.lifecycle.mark_failed();
    }

    /// Mark the process stopped after bounded request drain completes.
    pub fn mark_stopped(&self) {
        self.clear_replication_readiness();
        self.clear_document_readiness();
        self.lifecycle.mark_stopped();
    }

    /// Fail closed for client/data readiness after an uncertain durable write.
    /// Admin health and metrics remain available for diagnosis.
    pub(crate) fn clear_document_readiness(&self) {
        let _gate = self.document_readiness_lock();
        self.document_ready.store(false, Ordering::Release);
        if let Some(admissions) = &self.client_admissions {
            admissions.close();
        }
    }

    /// Run one admission-generation publication only while fatal document
    /// readiness remains open. Fatal closure takes the same gate, so a
    /// successful reload can never reopen admissions after corruption or an
    /// uncertain durable write has closed the data path.
    pub(crate) fn with_document_available<T>(&self, operation: impl FnOnce() -> T) -> Option<T> {
        let _gate = self.document_readiness_lock();
        if !self.document_ready.load(Ordering::Acquire) {
            return None;
        }
        Some(operation())
    }

    fn document_readiness_lock(&self) -> MutexGuard<'_, ()> {
        self.document_readiness_gate
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner)
    }

    fn clear_replication_readiness(&self) {
        if let Some(ready) = &self.replication_ready {
            ready.store(false, Ordering::Release);
        }
    }

    /// Read aggregate fixed-category memory accounting for one scrape.
    #[must_use]
    pub fn memory_metrics(&self) -> MemoryMetricsSnapshot {
        MemoryMetricsSnapshot::capture(&self.budget)
    }
}

/// Startup failure returned before the server begins accepting connections.
#[derive(Debug, thiserror::Error)]
pub enum ServerStartError {
    /// Configuration or process budget initialization failed.
    #[error(transparent)]
    Config(#[from] ServerConfigError),
    /// Local shard creation or recovery failed.
    #[error(transparent)]
    Storage(#[from] StorageError),
    /// Readiness state could not be entered after storage recovery.
    #[error(transparent)]
    Lifecycle(#[from] LifecycleError),
}
