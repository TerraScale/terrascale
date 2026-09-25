//! Explicit, process-wide memory admission accounting.
//!
//! Callers must reserve before allocating or admitting work, then keep the
//! returned permit alive for as long as those bytes remain owned. Moving a
//! permit transfers its accounting ownership; splitting or reattributing it
//! does not create additional process capacity.
//!
//! `reserved_bytes` is an admission estimate, not allocator live bytes or RSS.
//! Configure `headroom_bytes` below the process/container limit to cover
//! allocator retention and fragmentation, stacks, runtime/network buffers,
//! mapped pages, controller metadata, and other measured overhead. This module
//! does not intercept allocations or measure process memory.

use std::collections::HashMap;
use std::error::Error;
use std::fmt;
use std::hash::Hash;
use std::sync::{Arc, Mutex, MutexGuard, OnceLock, TryLockError};

use crate::TenantId;

/// Default process cap on simultaneously admitted work items.
pub const DEFAULT_MAX_PROCESS_ITEMS: usize = 65_536;
/// Default cap on work items owned by one tenant.
pub const DEFAULT_MAX_TENANT_ITEMS: usize = 16_384;
/// Default cap on work items owned by one tenant-worker pair.
pub const DEFAULT_MAX_WORKER_ITEMS: usize = 4_096;
/// Default process-wide cap on work items in one category.
pub const DEFAULT_MAX_CATEGORY_ITEMS: usize = 65_536;

/// Categories that can be attributed to admitted memory.
#[derive(Clone, Copy, Debug, Eq, Hash, PartialEq)]
#[repr(usize)]
pub enum MemoryCategory {
    DatabaseState = 0,
    ReadCache = 1,
    WriteBuffer = 2,
    TransactionVersion = 3,
    Index = 4,
    Replication = 5,
    Compaction = 6,
    Snapshot = 7,
    Query = 8,
    Transfer = 9,
    Observability = 10,
    RuntimeOverhead = 11,
    Other = 12,
}

impl MemoryCategory {
    /// Number of supported accounting categories.
    pub const COUNT: usize = 13;

    /// Every category, in its stable counter-array order.
    pub const ALL: [Self; Self::COUNT] = [
        Self::DatabaseState,
        Self::ReadCache,
        Self::WriteBuffer,
        Self::TransactionVersion,
        Self::Index,
        Self::Replication,
        Self::Compaction,
        Self::Snapshot,
        Self::Query,
        Self::Transfer,
        Self::Observability,
        Self::RuntimeOverhead,
        Self::Other,
    ];

    const fn index(self) -> usize {
        self as usize
    }
}

/// Process-local worker identity. Pair it with a tenant in [`Attribution`].
#[derive(Clone, Copy, Debug, Eq, Hash, PartialEq)]
pub struct WorkerId(pub u64);

/// Accounting owner for an admitted allocation or unit of work.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct Attribution {
    pub tenant: TenantId,
    pub worker: WorkerId,
    pub category: MemoryCategory,
}

/// Limits for one [`MemoryBudget`]. The usable budget is the process limit
/// minus explicit headroom; per-tenant, per-worker, and category limits cannot
/// raise that process-wide ceiling.
#[derive(Clone, Debug)]
pub struct BudgetConfig {
    process_limit_bytes: usize,
    headroom_bytes: usize,
    tenant_limit_bytes: usize,
    worker_limit_bytes: usize,
    category_limit_bytes: [usize; MemoryCategory::COUNT],
    process_limit_items: usize,
    tenant_limit_items: usize,
    worker_limit_items: usize,
    category_limit_items: [usize; MemoryCategory::COUNT],
    max_tenant_accounts: usize,
    max_worker_accounts: usize,
}

impl BudgetConfig {
    /// Create a budget with a process limit and reserved headroom.
    /// # Errors
    ///
    /// Returns [`ConfigError::HeadroomExceedsProcessLimit`] when headroom is
    /// larger than the configured process limit.
    pub fn new(process_limit_bytes: usize, headroom_bytes: usize) -> Result<Self, ConfigError> {
        let usable = process_limit_bytes
            .checked_sub(headroom_bytes)
            .ok_or(ConfigError::HeadroomExceedsProcessLimit)?;

        Ok(Self {
            process_limit_bytes,
            headroom_bytes,
            tenant_limit_bytes: usable,
            worker_limit_bytes: usable,
            category_limit_bytes: [usable; MemoryCategory::COUNT],
            process_limit_items: DEFAULT_MAX_PROCESS_ITEMS,
            tenant_limit_items: DEFAULT_MAX_TENANT_ITEMS,
            worker_limit_items: DEFAULT_MAX_WORKER_ITEMS,
            category_limit_items: [DEFAULT_MAX_CATEGORY_ITEMS; MemoryCategory::COUNT],
            max_tenant_accounts: 1_024,
            max_worker_accounts: 4_096,
        })
    }

    /// Set the maximum admitted bytes attributed to any one tenant.
    #[must_use]
    pub fn with_tenant_limit_bytes(mut self, limit: usize) -> Self {
        self.tenant_limit_bytes = limit;
        self
    }

    /// Set the maximum admitted bytes attributed to any tenant-worker pair.
    #[must_use]
    pub fn with_worker_limit_bytes(mut self, limit: usize) -> Self {
        self.worker_limit_bytes = limit;
        self
    }

    /// Set the maximum admitted bytes for one category across the process.
    #[must_use]
    pub fn with_category_limit_bytes(mut self, category: MemoryCategory, limit: usize) -> Self {
        self.category_limit_bytes[category.index()] = limit;
        self
    }

    /// Set process, tenant, worker, and category limits for admitted work items.
    /// A work item is a caller-defined bounded unit such as one queued task,
    /// request, or batch; retain its [`ItemReservation`] until it completes.
    #[must_use]
    pub fn with_item_limits(
        mut self,
        process: usize,
        tenant: usize,
        worker: usize,
        category: usize,
    ) -> Self {
        self.process_limit_items = process;
        self.tenant_limit_items = tenant;
        self.worker_limit_items = worker;
        self.category_limit_items = [category; MemoryCategory::COUNT];
        self
    }

    /// Set the maximum number of admitted work items in one category.
    #[must_use]
    pub fn with_category_item_limit(mut self, category: MemoryCategory, limit: usize) -> Self {
        self.category_limit_items[category.index()] = limit;
        self
    }

    /// Bound accounting-table entries for tenants and tenant-worker pairs.
    /// This also bounds bookkeeping growth from arbitrary identifiers.
    #[must_use]
    pub fn with_account_limits(mut self, tenants: usize, workers: usize) -> Self {
        self.max_tenant_accounts = tenants;
        self.max_worker_accounts = workers;
        self
    }

    /// Configured process limit, including the headroom excluded from admission.
    #[must_use]
    pub const fn process_limit_bytes(&self) -> usize {
        self.process_limit_bytes
    }

    /// Explicit bytes withheld from admission for measured process overhead.
    #[must_use]
    pub const fn headroom_bytes(&self) -> usize {
        self.headroom_bytes
    }

    /// Maximum bytes available for admitted reservations.
    #[must_use]
    pub const fn usable_limit_bytes(&self) -> usize {
        self.process_limit_bytes - self.headroom_bytes
    }
}

/// Invalid process-budget configuration.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ConfigError {
    HeadroomExceedsProcessLimit,
}

impl fmt::Display for ConfigError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::HeadroomExceedsProcessLimit => {
                f.write_str("memory headroom cannot exceed the process limit")
            }
        }
    }
}

impl Error for ConfigError {}

/// Scope of a byte cap reported by [`AdmissionError::LimitExceeded`].
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum LimitScope {
    Process,
    Tenant,
    Worker,
    Category,
}

/// Scope of an item-count cap reported by [`AdmissionError::ItemLimitExceeded`].
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ItemLimitScope {
    Process,
    Tenant,
    Worker,
    Category,
}

/// Scope of a bounded accounting table reported by
/// [`AdmissionError::AccountLimitExceeded`].
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum AccountScope {
    Tenant,
    Worker,
}

/// A synchronous admission failure. There is no waiter queue: callers may
/// retry after releasing resources or return an explicit overload error.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum AdmissionError {
    LimitExceeded {
        scope: LimitScope,
        requested_bytes: usize,
        used_bytes: usize,
        limit_bytes: usize,
    },
    ItemLimitExceeded {
        scope: ItemLimitScope,
        requested_items: usize,
        used_items: usize,
        limit_items: usize,
    },
    AccountLimitExceeded {
        scope: AccountScope,
        limit: usize,
    },
    CounterOverflow {
        scope: LimitScope,
    },
    ItemCounterOverflow {
        scope: ItemLimitScope,
    },
    ZeroItemCount,
    ControllerBusy,
    AccountingAllocationFailed,
}

impl fmt::Display for AdmissionError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::LimitExceeded {
                scope,
                requested_bytes,
                used_bytes,
                limit_bytes,
            } => write!(
                f,
                "{scope:?} memory limit exceeded: {used_bytes} bytes used, {requested_bytes} requested, {limit_bytes} allowed"
            ),
            Self::AccountLimitExceeded { scope, limit } => {
                write!(f, "{scope:?} accounting limit reached ({limit} entries)")
            }
            Self::ItemLimitExceeded {
                scope,
                requested_items,
                used_items,
                limit_items,
            } => write!(
                f,
                "{scope:?} item limit exceeded: {used_items} items used, {requested_items} requested, {limit_items} allowed"
            ),
            Self::CounterOverflow { scope } => {
                write!(f, "{scope:?} memory accounting counter overflow")
            }
            Self::ControllerBusy => f.write_str("memory controller is busy; retry admission"),
            Self::ItemCounterOverflow { scope } => {
                write!(f, "{scope:?} item accounting counter overflow")
            }
            Self::ZeroItemCount => f.write_str("work admission requires at least one item"),
            Self::AccountingAllocationFailed => {
                f.write_str("unable to grow memory accounting metadata")
            }
        }
    }
}

impl Error for AdmissionError {}

/// The process already has a memory controller installed.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct ProcessBudgetAlreadyInitialized;

impl fmt::Display for ProcessBudgetAlreadyInitialized {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str("the process memory budget is already initialized")
    }
}

impl Error for ProcessBudgetAlreadyInitialized {}

/// Invalid ownership split request.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct SplitError {
    pub requested_bytes: usize,
    pub available_bytes: usize,
}

impl fmt::Display for SplitError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(
            f,
            "cannot split {} bytes from a {} byte reservation",
            self.requested_bytes, self.available_bytes
        )
    }
}

impl Error for SplitError {}

/// Invalid item-slot ownership split request.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct ItemSplitError {
    pub requested_items: usize,
    pub available_items: usize,
}

impl fmt::Display for ItemSplitError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(
            f,
            "cannot split {} items from a {} item reservation",
            self.requested_items, self.available_items
        )
    }
}

impl Error for ItemSplitError {}

/// Failed work-item ownership reattribution; the source permit remains intact.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum ItemTransferError {
    ExceedsReservation {
        requested_items: usize,
        available_items: usize,
    },
    Admission(AdmissionError),
}

impl fmt::Display for ItemTransferError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::ExceedsReservation {
                requested_items,
                available_items,
            } => write!(
                f,
                "cannot transfer {requested_items} items from a {available_items} item reservation"
            ),
            Self::Admission(error) => error.fmt(f),
        }
    }
}

impl Error for ItemTransferError {}

/// Failed ownership reattribution. The source reservation remains unchanged.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum TransferError {
    ExceedsReservation {
        requested_bytes: usize,
        available_bytes: usize,
    },
    Admission(AdmissionError),
}

impl fmt::Display for TransferError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::ExceedsReservation {
                requested_bytes,
                available_bytes,
            } => write!(
                f,
                "cannot transfer {requested_bytes} bytes from a {available_bytes} byte reservation"
            ),
            Self::Admission(error) => error.fmt(f),
        }
    }
}

impl Error for TransferError {}

/// A cloneable handle to one synchronized process-wide memory budget.
#[derive(Clone)]
pub struct MemoryBudget {
    inner: Arc<Inner>,
}

static PROCESS_MEMORY_BUDGET: OnceLock<MemoryBudget> = OnceLock::new();

/// Install the single process-wide controller during startup.
///
/// Services should clone the returned handle or borrow it through
/// [`process_memory_budget`]. This one-time entry point prevents independent
/// production controllers from accidentally admitting against separate caps.
///
/// # Errors
///
/// Returns [`ProcessBudgetAlreadyInitialized`] if another controller has
/// already been installed.
pub fn initialize_process_memory_budget(
    config: BudgetConfig,
) -> Result<&'static MemoryBudget, ProcessBudgetAlreadyInitialized> {
    PROCESS_MEMORY_BUDGET
        .set(MemoryBudget::new(config))
        .map_err(|_| ProcessBudgetAlreadyInitialized)?;
    PROCESS_MEMORY_BUDGET
        .get()
        .ok_or(ProcessBudgetAlreadyInitialized)
}

/// Read the process-wide controller after startup initialization.
pub fn process_memory_budget() -> Option<&'static MemoryBudget> {
    PROCESS_MEMORY_BUDGET.get()
}

struct Inner {
    config: BudgetConfig,
    state: Mutex<State>,
}

#[derive(Default)]
struct State {
    total_bytes: usize,
    tenants: HashMap<TenantId, usize>,
    workers: HashMap<WorkerKey, usize>,
    categories: [usize; MemoryCategory::COUNT],
    total_items: usize,
    tenant_items: HashMap<TenantId, usize>,
    worker_items: HashMap<WorkerKey, usize>,
    category_items: [usize; MemoryCategory::COUNT],
}

#[derive(Clone, Copy, Debug, Eq, Hash, PartialEq)]
struct WorkerKey {
    tenant: TenantId,
    worker: WorkerId,
}

/// Point-in-time accounting. These values describe reservations only and
/// should be reported separately from allocator and operating-system metrics.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct BudgetSnapshot {
    pub process_limit_bytes: usize,
    pub headroom_bytes: usize,
    pub usable_limit_bytes: usize,
    pub reserved_bytes: usize,
    pub reserved_items: usize,
    pub tenants: Vec<(TenantId, usize)>,
    pub workers: Vec<(TenantId, WorkerId, usize)>,
    pub categories: [usize; MemoryCategory::COUNT],
    pub tenant_items: Vec<(TenantId, usize)>,
    pub worker_items: Vec<(TenantId, WorkerId, usize)>,
    pub category_items: [usize; MemoryCategory::COUNT],
}

impl MemoryBudget {
    /// Create a process controller. Clone this value to share that same
    /// controller; constructing another value creates an independent budget.
    #[must_use]
    pub fn new(config: BudgetConfig) -> Self {
        Self {
            inner: Arc::new(Inner {
                config,
                state: Mutex::new(State::default()),
            }),
        }
    }

    /// Reserve bytes before allocating them. A zero-byte request is a no-op
    /// and does not admit a work item; use [`Self::try_reserve_items`] or
    /// [`Self::try_reserve_work`] for queued or in-flight work. Rejection is
    /// immediate and does not queue the caller. If another
    /// short controller update currently holds its lock, admission is rejected
    /// with [`AdmissionError::ControllerBusy`] for the caller to retry.
    ///
    /// # Errors
    ///
    /// Returns an [`AdmissionError`] when a byte or account limit is reached,
    /// accounting overflows, metadata allocation fails, or the controller is
    /// busy.
    pub fn try_reserve(
        &self,
        attribution: Attribution,
        bytes: usize,
    ) -> Result<Reservation, AdmissionError> {
        self.try_admit(attribution, bytes, 0)?;
        Ok(Reservation {
            inner: Arc::clone(&self.inner),
            attribution,
            bytes,
        })
    }

    /// Reserve bounded in-flight or queued work that may use zero bytes.
    /// Keep the returned permit until the work item completes or is removed.
    ///
    /// # Errors
    ///
    /// Returns [`AdmissionError`] when the item count is zero, an item or
    /// account limit is reached, accounting overflows, metadata allocation
    /// fails, or the controller is busy.
    pub fn try_reserve_items(
        &self,
        attribution: Attribution,
        items: usize,
    ) -> Result<ItemReservation, AdmissionError> {
        if items == 0 {
            return Err(AdmissionError::ZeroItemCount);
        }
        self.try_admit(attribution, 0, items)?;
        Ok(ItemReservation {
            inner: Arc::clone(&self.inner),
            attribution,
            items,
        })
    }

    /// Atomically reserve bytes and one or more work items before materializing
    /// the work. Both dimensions are admitted under the same process lock.
    ///
    /// # Errors
    ///
    /// Returns [`AdmissionError`] when the item count is zero, any byte or
    /// item cap is reached, accounting overflows, metadata allocation fails,
    /// or the controller is busy.
    pub fn try_reserve_work(
        &self,
        attribution: Attribution,
        bytes: usize,
        items: usize,
    ) -> Result<WorkReservation, AdmissionError> {
        if items == 0 {
            return Err(AdmissionError::ZeroItemCount);
        }
        self.try_admit(attribution, bytes, items)?;
        Ok(WorkReservation {
            memory: Reservation {
                inner: Arc::clone(&self.inner),
                attribution,
                bytes,
            },
            items: ItemReservation {
                inner: Arc::clone(&self.inner),
                attribution,
                items,
            },
        })
    }

    /// Return current reservation accounting for diagnostics and metrics.
    #[must_use]
    pub fn snapshot(&self) -> BudgetSnapshot {
        let state = self.lock_state();
        BudgetSnapshot {
            process_limit_bytes: self.inner.config.process_limit_bytes,
            headroom_bytes: self.inner.config.headroom_bytes,
            usable_limit_bytes: self.inner.config.usable_limit_bytes(),
            reserved_bytes: state.total_bytes,
            reserved_items: state.total_items,
            tenants: state
                .tenants
                .iter()
                .map(|(id, bytes)| (*id, *bytes))
                .collect(),
            workers: state
                .workers
                .iter()
                .map(|(key, bytes)| (key.tenant, key.worker, *bytes))
                .collect(),
            categories: state.categories,
            tenant_items: state
                .tenant_items
                .iter()
                .map(|(id, items)| (*id, *items))
                .collect(),
            worker_items: state
                .worker_items
                .iter()
                .map(|(key, items)| (key.tenant, key.worker, *items))
                .collect(),
            category_items: state.category_items,
        }
    }

    fn lock_state(&self) -> MutexGuard<'_, State> {
        self.inner
            .state
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner)
    }

    fn try_lock_state(&self) -> Result<MutexGuard<'_, State>, AdmissionError> {
        match self.inner.state.try_lock() {
            Ok(state) => Ok(state),
            Err(TryLockError::Poisoned(poisoned)) => Ok(poisoned.into_inner()),
            Err(TryLockError::WouldBlock) => Err(AdmissionError::ControllerBusy),
        }
    }

    fn try_admit(
        &self,
        attribution: Attribution,
        bytes: usize,
        items: usize,
    ) -> Result<(), AdmissionError> {
        if bytes == 0 && items == 0 {
            return Ok(());
        }
        let mut state = self.try_lock_state()?;
        let projected = validate_admission(&state, &self.inner.config, attribution, bytes, items)?;
        prepare_admission_accounts(&mut state, &self.inner.config, attribution, bytes, items)?;
        apply_admission(&mut state, attribution, bytes, items, projected);
        Ok(())
    }
}

/// A non-cloneable RAII claim on admitted bytes.
#[must_use = "a reservation releases its budget when dropped"]
pub struct Reservation {
    inner: Arc<Inner>,
    attribution: Attribution,
    bytes: usize,
}

impl Reservation {
    /// Number of bytes owned by this permit.
    #[must_use]
    pub const fn bytes(&self) -> usize {
        self.bytes
    }

    /// Current owner of this permit's accounting.
    #[must_use]
    pub const fn attribution(&self) -> Attribution {
        self.attribution
    }

    /// Move part of this permit to a new owner with the same attribution.
    /// Aggregate accounting is unchanged; only the ownership token is split.
    ///
    /// # Errors
    ///
    /// Returns [`SplitError`] when more bytes are requested than this permit
    /// owns.
    pub fn split(&mut self, bytes: usize) -> Result<Self, SplitError> {
        if bytes > self.bytes {
            return Err(SplitError {
                requested_bytes: bytes,
                available_bytes: self.bytes,
            });
        }
        self.bytes -= bytes;
        Ok(Self {
            inner: Arc::clone(&self.inner),
            attribution: self.attribution,
            bytes,
        })
    }

    /// Move part of this permit to another tenant, worker, or category.
    /// The transfer is atomic under the controller lock and never changes
    /// process total. Destination caps are checked before the source changes.
    /// A busy controller rejects the transfer without changing either owner.
    ///
    /// # Errors
    ///
    /// Returns [`TransferError`] when the source does not own enough bytes,
    /// destination admission fails, or the controller is busy.
    pub fn transfer(
        &mut self,
        bytes: usize,
        destination: Attribution,
    ) -> Result<Self, TransferError> {
        if bytes > self.bytes {
            return Err(TransferError::ExceedsReservation {
                requested_bytes: bytes,
                available_bytes: self.bytes,
            });
        }
        if bytes == 0 {
            return Ok(Self {
                inner: Arc::clone(&self.inner),
                attribution: destination,
                bytes: 0,
            });
        }
        if self.attribution == destination {
            return self
                .split(bytes)
                .map_err(|error| TransferError::ExceedsReservation {
                    requested_bytes: error.requested_bytes,
                    available_bytes: error.available_bytes,
                });
        }

        let source = self.attribution;
        let mut state = match self.inner.state.try_lock() {
            Ok(state) => state,
            Err(TryLockError::Poisoned(poisoned)) => poisoned.into_inner(),
            Err(TryLockError::WouldBlock) => {
                return Err(TransferError::Admission(AdmissionError::ControllerBusy));
            }
        };
        validate_transfer(&state, &self.inner.config, source, destination, bytes)?;
        prepare_transfer_accounts(&mut state, &self.inner.config, source, destination, bytes)?;
        apply_transfer(&mut state, source, destination, bytes);
        self.bytes -= bytes;

        Ok(Self {
            inner: Arc::clone(&self.inner),
            attribution: destination,
            bytes,
        })
    }
}

impl Drop for Reservation {
    fn drop(&mut self) {
        if self.bytes == 0 {
            return;
        }

        let mut state = self
            .inner
            .state
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner);
        debug_assert!(state.total_bytes >= self.bytes);
        state.total_bytes = state.total_bytes.saturating_sub(self.bytes);
        decrement_map(&mut state.tenants, &self.attribution.tenant, self.bytes);
        decrement_map(
            &mut state.workers,
            &WorkerKey {
                tenant: self.attribution.tenant,
                worker: self.attribution.worker,
            },
            self.bytes,
        );
        let category = &mut state.categories[self.attribution.category.index()];
        debug_assert!(*category >= self.bytes);
        *category = category.saturating_sub(self.bytes);
    }
}

/// A non-cloneable RAII claim on one or more admitted work items.
#[must_use = "an item reservation releases its admission slots when dropped"]
pub struct ItemReservation {
    inner: Arc<Inner>,
    attribution: Attribution,
    items: usize,
}

impl ItemReservation {
    /// Number of work items owned by this permit.
    #[must_use]
    pub const fn items(&self) -> usize {
        self.items
    }

    /// Current owner of this permit's accounting.
    #[must_use]
    pub const fn attribution(&self) -> Attribution {
        self.attribution
    }

    /// Move item slots to another owner without changing aggregate admission.
    ///
    /// # Errors
    ///
    /// Returns [`ItemSplitError`] when more items are requested than this
    /// permit owns.
    pub fn split(&mut self, items: usize) -> Result<Self, ItemSplitError> {
        if items > self.items {
            return Err(ItemSplitError {
                requested_items: items,
                available_items: self.items,
            });
        }
        self.items -= items;
        Ok(Self {
            inner: Arc::clone(&self.inner),
            attribution: self.attribution,
            items,
        })
    }

    /// Atomically reattribute item slots to another tenant, worker, or category.
    ///
    /// # Errors
    ///
    /// Returns [`ItemTransferError`] when the source owns too few items, a
    /// destination cap is reached, accounting metadata cannot grow, or the
    /// controller is busy. On error, this reservation remains unchanged.
    pub fn transfer(
        &mut self,
        items: usize,
        destination: Attribution,
    ) -> Result<Self, ItemTransferError> {
        if items > self.items {
            return Err(ItemTransferError::ExceedsReservation {
                requested_items: items,
                available_items: self.items,
            });
        }
        if items == 0 {
            return Ok(Self {
                inner: Arc::clone(&self.inner),
                attribution: destination,
                items: 0,
            });
        }
        if self.attribution == destination {
            return self
                .split(items)
                .map_err(|error| ItemTransferError::ExceedsReservation {
                    requested_items: error.requested_items,
                    available_items: error.available_items,
                });
        }

        let source = self.attribution;
        let mut state = match self.inner.state.try_lock() {
            Ok(state) => state,
            Err(TryLockError::Poisoned(poisoned)) => poisoned.into_inner(),
            Err(TryLockError::WouldBlock) => {
                return Err(ItemTransferError::Admission(AdmissionError::ControllerBusy));
            }
        };
        validate_item_transfer(&state, &self.inner.config, source, destination, items)?;
        prepare_item_transfer_accounts(&mut state, &self.inner.config, source, destination, items)?;
        apply_item_transfer(&mut state, source, destination, items);
        self.items -= items;

        Ok(Self {
            inner: Arc::clone(&self.inner),
            attribution: destination,
            items,
        })
    }
}

impl Drop for ItemReservation {
    fn drop(&mut self) {
        if self.items == 0 {
            return;
        }

        let mut state = self
            .inner
            .state
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner);
        debug_assert!(state.total_items >= self.items);
        state.total_items = state.total_items.saturating_sub(self.items);
        decrement_map(
            &mut state.tenant_items,
            &self.attribution.tenant,
            self.items,
        );
        decrement_map(
            &mut state.worker_items,
            &WorkerKey {
                tenant: self.attribution.tenant,
                worker: self.attribution.worker,
            },
            self.items,
        );
        let category = &mut state.category_items[self.attribution.category.index()];
        debug_assert!(*category >= self.items);
        *category = category.saturating_sub(self.items);
    }
}

/// A reservation that atomically owns both byte capacity and work-item slots.
#[must_use = "a work reservation releases bytes and item slots when dropped"]
pub struct WorkReservation {
    memory: Reservation,
    items: ItemReservation,
}

impl WorkReservation {
    /// Number of admitted bytes held for the work.
    #[must_use]
    pub const fn bytes(&self) -> usize {
        self.memory.bytes()
    }

    /// Number of admitted work items held by the permit.
    #[must_use]
    pub const fn items(&self) -> usize {
        self.items.items()
    }

    /// Accounting owner for both dimensions.
    #[must_use]
    pub const fn attribution(&self) -> Attribution {
        self.memory.attribution()
    }

    /// Borrow the byte reservation for ownership splitting or reattribution.
    pub const fn memory(&self) -> &Reservation {
        &self.memory
    }

    /// Borrow the item reservation for ownership splitting.
    pub const fn item_permit(&self) -> &ItemReservation {
        &self.items
    }

    /// Separate the byte and item tokens when their lifetimes diverge.
    pub fn into_parts(self) -> (Reservation, ItemReservation) {
        (self.memory, self.items)
    }
}

fn checked_projected(
    used: usize,
    removed: usize,
    added: usize,
    scope: LimitScope,
) -> Result<usize, AdmissionError> {
    used.checked_sub(removed)
        .and_then(|remaining| remaining.checked_add(added))
        .ok_or(AdmissionError::CounterOverflow { scope })
}

fn enforce_limit(
    scope: LimitScope,
    requested_bytes: usize,
    projected_bytes: usize,
    limit_bytes: usize,
) -> Result<(), AdmissionError> {
    if projected_bytes > limit_bytes {
        return Err(AdmissionError::LimitExceeded {
            scope,
            requested_bytes,
            used_bytes: projected_bytes - requested_bytes,
            limit_bytes,
        });
    }
    Ok(())
}

#[derive(Clone, Copy)]
struct AdmissionProjection {
    bytes: CounterProjection,
    items: CounterProjection,
}

#[derive(Clone, Copy)]
struct CounterProjection {
    process: usize,
    tenant: usize,
    worker: usize,
    category: usize,
}

fn validate_admission(
    state: &State,
    config: &BudgetConfig,
    attribution: Attribution,
    bytes: usize,
    items: usize,
) -> Result<AdmissionProjection, AdmissionError> {
    let worker = WorkerKey {
        tenant: attribution.tenant,
        worker: attribution.worker,
    };
    Ok(AdmissionProjection {
        bytes: validate_byte_limits(state, config, attribution, worker, bytes)?,
        items: validate_item_limits(state, config, attribution, worker, items)?,
    })
}

fn validate_byte_limits(
    state: &State,
    config: &BudgetConfig,
    attribution: Attribution,
    worker: WorkerKey,
    requested: usize,
) -> Result<CounterProjection, AdmissionError> {
    if requested == 0 {
        return Ok(CounterProjection {
            process: state.total_bytes,
            tenant: state.tenants.get(&attribution.tenant).copied().unwrap_or(0),
            worker: state.workers.get(&worker).copied().unwrap_or(0),
            category: state.categories[attribution.category.index()],
        });
    }
    let total = checked_projected(state.total_bytes, 0, requested, LimitScope::Process)?;
    let tenant = checked_projected(
        state.tenants.get(&attribution.tenant).copied().unwrap_or(0),
        0,
        requested,
        LimitScope::Tenant,
    )?;
    let worker = checked_projected(
        state.workers.get(&worker).copied().unwrap_or(0),
        0,
        requested,
        LimitScope::Worker,
    )?;
    let category = checked_projected(
        state.categories[attribution.category.index()],
        0,
        requested,
        LimitScope::Category,
    )?;
    enforce_limit(
        LimitScope::Process,
        requested,
        total,
        config.usable_limit_bytes(),
    )?;
    enforce_limit(
        LimitScope::Tenant,
        requested,
        tenant,
        config.tenant_limit_bytes,
    )?;
    enforce_limit(
        LimitScope::Worker,
        requested,
        worker,
        config.worker_limit_bytes,
    )?;
    enforce_limit(
        LimitScope::Category,
        requested,
        category,
        config.category_limit_bytes[attribution.category.index()],
    )?;
    Ok(CounterProjection {
        process: total,
        tenant,
        worker,
        category,
    })
}

fn validate_item_limits(
    state: &State,
    config: &BudgetConfig,
    attribution: Attribution,
    worker: WorkerKey,
    requested: usize,
) -> Result<CounterProjection, AdmissionError> {
    if requested == 0 {
        return Ok(CounterProjection {
            process: state.total_items,
            tenant: state
                .tenant_items
                .get(&attribution.tenant)
                .copied()
                .unwrap_or(0),
            worker: state.worker_items.get(&worker).copied().unwrap_or(0),
            category: state.category_items[attribution.category.index()],
        });
    }
    let total = checked_items(state.total_items, requested, ItemLimitScope::Process)?;
    let tenant = checked_items(
        state
            .tenant_items
            .get(&attribution.tenant)
            .copied()
            .unwrap_or(0),
        requested,
        ItemLimitScope::Tenant,
    )?;
    let worker = checked_items(
        state.worker_items.get(&worker).copied().unwrap_or(0),
        requested,
        ItemLimitScope::Worker,
    )?;
    let category = checked_items(
        state.category_items[attribution.category.index()],
        requested,
        ItemLimitScope::Category,
    )?;
    enforce_item_limit(
        ItemLimitScope::Process,
        requested,
        total,
        config.process_limit_items,
    )?;
    enforce_item_limit(
        ItemLimitScope::Tenant,
        requested,
        tenant,
        config.tenant_limit_items,
    )?;
    enforce_item_limit(
        ItemLimitScope::Worker,
        requested,
        worker,
        config.worker_limit_items,
    )?;
    enforce_item_limit(
        ItemLimitScope::Category,
        requested,
        category,
        config.category_limit_items[attribution.category.index()],
    )?;
    Ok(CounterProjection {
        process: total,
        tenant,
        worker,
        category,
    })
}

fn prepare_admission_accounts(
    state: &mut State,
    config: &BudgetConfig,
    attribution: Attribution,
    bytes: usize,
    items: usize,
) -> Result<(), AdmissionError> {
    let worker = WorkerKey {
        tenant: attribution.tenant,
        worker: attribution.worker,
    };
    if bytes > 0 {
        prepare_account(
            &mut state.tenants,
            &attribution.tenant,
            config.max_tenant_accounts,
            AccountScope::Tenant,
        )?;
        prepare_account(
            &mut state.workers,
            &worker,
            config.max_worker_accounts,
            AccountScope::Worker,
        )?;
    }
    if items > 0 {
        prepare_account(
            &mut state.tenant_items,
            &attribution.tenant,
            config.max_tenant_accounts,
            AccountScope::Tenant,
        )?;
        prepare_account(
            &mut state.worker_items,
            &worker,
            config.max_worker_accounts,
            AccountScope::Worker,
        )?;
    }
    Ok(())
}

fn prepare_account<K: Eq + Hash>(
    map: &mut HashMap<K, usize>,
    key: &K,
    limit: usize,
    scope: AccountScope,
) -> Result<(), AdmissionError> {
    if map.contains_key(key) {
        return Ok(());
    }
    if map.len() >= limit {
        return Err(AdmissionError::AccountLimitExceeded { scope, limit });
    }
    map.try_reserve(1)
        .map_err(|_| AdmissionError::AccountingAllocationFailed)
}

fn apply_admission(
    state: &mut State,
    attribution: Attribution,
    bytes: usize,
    items: usize,
    projected: AdmissionProjection,
) {
    if bytes > 0 {
        let worker = WorkerKey {
            tenant: attribution.tenant,
            worker: attribution.worker,
        };
        state.total_bytes = projected.bytes.process;
        state
            .tenants
            .insert(attribution.tenant, projected.bytes.tenant);
        state.workers.insert(worker, projected.bytes.worker);
        state.categories[attribution.category.index()] = projected.bytes.category;
    }
    if items > 0 {
        let worker = WorkerKey {
            tenant: attribution.tenant,
            worker: attribution.worker,
        };
        state.total_items = projected.items.process;
        state
            .tenant_items
            .insert(attribution.tenant, projected.items.tenant);
        state.worker_items.insert(worker, projected.items.worker);
        state.category_items[attribution.category.index()] = projected.items.category;
    }
}

fn checked_items(
    used: usize,
    requested: usize,
    scope: ItemLimitScope,
) -> Result<usize, AdmissionError> {
    used.checked_add(requested)
        .ok_or(AdmissionError::ItemCounterOverflow { scope })
}

fn enforce_item_limit(
    scope: ItemLimitScope,
    requested_items: usize,
    projected_items: usize,
    limit_items: usize,
) -> Result<(), AdmissionError> {
    if projected_items > limit_items {
        return Err(AdmissionError::ItemLimitExceeded {
            scope,
            requested_items,
            used_items: projected_items - requested_items,
            limit_items,
        });
    }
    Ok(())
}

fn validate_item_transfer(
    state: &State,
    config: &BudgetConfig,
    source: Attribution,
    destination: Attribution,
    items: usize,
) -> Result<(), ItemTransferError> {
    let source_worker = WorkerKey {
        tenant: source.tenant,
        worker: source.worker,
    };
    let destination_worker = WorkerKey {
        tenant: destination.tenant,
        worker: destination.worker,
    };
    let checks = [
        (
            ItemLimitScope::Tenant,
            state
                .tenant_items
                .get(&destination.tenant)
                .copied()
                .unwrap_or(0),
            if source.tenant == destination.tenant {
                items
            } else {
                0
            },
            config.tenant_limit_items,
        ),
        (
            ItemLimitScope::Worker,
            state
                .worker_items
                .get(&destination_worker)
                .copied()
                .unwrap_or(0),
            if source_worker == destination_worker {
                items
            } else {
                0
            },
            config.worker_limit_items,
        ),
        (
            ItemLimitScope::Category,
            state.category_items[destination.category.index()],
            if source.category == destination.category {
                items
            } else {
                0
            },
            config.category_limit_items[destination.category.index()],
        ),
    ];
    for (scope, used, removed, limit) in checks {
        let projected = checked_item_transfer(used, removed, items, scope)?;
        if projected > limit {
            return Err(ItemTransferError::Admission(
                AdmissionError::ItemLimitExceeded {
                    scope,
                    requested_items: items,
                    used_items: projected - items,
                    limit_items: limit,
                },
            ));
        }
    }
    Ok(())
}

fn prepare_item_transfer_accounts(
    state: &mut State,
    config: &BudgetConfig,
    source: Attribution,
    destination: Attribution,
    items: usize,
) -> Result<(), ItemTransferError> {
    let source_worker = WorkerKey {
        tenant: source.tenant,
        worker: source.worker,
    };
    let destination_worker = WorkerKey {
        tenant: destination.tenant,
        worker: destination.worker,
    };
    let destination_tenant_exists = state.tenant_items.contains_key(&destination.tenant);
    let source_tenant_freed = source.tenant != destination.tenant
        && state.tenant_items.get(&source.tenant).copied() == Some(items);
    if !destination_tenant_exists
        && state.tenant_items.len() - usize::from(source_tenant_freed) >= config.max_tenant_accounts
    {
        return Err(ItemTransferError::Admission(
            AdmissionError::AccountLimitExceeded {
                scope: AccountScope::Tenant,
                limit: config.max_tenant_accounts,
            },
        ));
    }

    let destination_worker_exists = state.worker_items.contains_key(&destination_worker);
    let source_worker_freed = source_worker != destination_worker
        && state.worker_items.get(&source_worker).copied() == Some(items);
    if !destination_worker_exists
        && state.worker_items.len() - usize::from(source_worker_freed) >= config.max_worker_accounts
    {
        return Err(ItemTransferError::Admission(
            AdmissionError::AccountLimitExceeded {
                scope: AccountScope::Worker,
                limit: config.max_worker_accounts,
            },
        ));
    }

    if !destination_tenant_exists {
        state.tenant_items.try_reserve(1).map_err(|_| {
            ItemTransferError::Admission(AdmissionError::AccountingAllocationFailed)
        })?;
    }
    if !destination_worker_exists {
        state.worker_items.try_reserve(1).map_err(|_| {
            ItemTransferError::Admission(AdmissionError::AccountingAllocationFailed)
        })?;
    }
    Ok(())
}

fn apply_item_transfer(
    state: &mut State,
    source: Attribution,
    destination: Attribution,
    items: usize,
) {
    let source_worker = WorkerKey {
        tenant: source.tenant,
        worker: source.worker,
    };
    let destination_worker = WorkerKey {
        tenant: destination.tenant,
        worker: destination.worker,
    };
    decrement_map(&mut state.tenant_items, &source.tenant, items);
    decrement_map(&mut state.worker_items, &source_worker, items);
    state.category_items[source.category.index()] -= items;
    let tenant_used = state
        .tenant_items
        .get(&destination.tenant)
        .copied()
        .unwrap_or(0);
    let worker_used = state
        .worker_items
        .get(&destination_worker)
        .copied()
        .unwrap_or(0);
    state
        .tenant_items
        .insert(destination.tenant, tenant_used + items);
    state
        .worker_items
        .insert(destination_worker, worker_used + items);
    state.category_items[destination.category.index()] += items;
}

fn checked_item_transfer(
    used: usize,
    removed: usize,
    added: usize,
    scope: ItemLimitScope,
) -> Result<usize, ItemTransferError> {
    used.checked_sub(removed)
        .and_then(|remaining| remaining.checked_add(added))
        .ok_or(ItemTransferError::Admission(
            AdmissionError::ItemCounterOverflow { scope },
        ))
}

fn validate_transfer(
    state: &State,
    config: &BudgetConfig,
    source: Attribution,
    destination: Attribution,
    bytes: usize,
) -> Result<(), TransferError> {
    let source_worker = WorkerKey {
        tenant: source.tenant,
        worker: source.worker,
    };
    let destination_worker = WorkerKey {
        tenant: destination.tenant,
        worker: destination.worker,
    };
    let checks = [
        (
            LimitScope::Tenant,
            state.tenants.get(&destination.tenant).copied().unwrap_or(0),
            if source.tenant == destination.tenant {
                bytes
            } else {
                0
            },
            config.tenant_limit_bytes,
        ),
        (
            LimitScope::Worker,
            state.workers.get(&destination_worker).copied().unwrap_or(0),
            if source_worker == destination_worker {
                bytes
            } else {
                0
            },
            config.worker_limit_bytes,
        ),
        (
            LimitScope::Category,
            state.categories[destination.category.index()],
            if source.category == destination.category {
                bytes
            } else {
                0
            },
            config.category_limit_bytes[destination.category.index()],
        ),
    ];
    for (scope, used, removed, limit) in checks {
        let projected =
            checked_projected(used, removed, bytes, scope).map_err(TransferError::Admission)?;
        if projected > limit {
            return Err(TransferError::Admission(AdmissionError::LimitExceeded {
                scope,
                requested_bytes: bytes,
                used_bytes: projected - bytes,
                limit_bytes: limit,
            }));
        }
    }
    Ok(())
}

fn prepare_transfer_accounts(
    state: &mut State,
    config: &BudgetConfig,
    source: Attribution,
    destination: Attribution,
    bytes: usize,
) -> Result<(), TransferError> {
    let source_worker = WorkerKey {
        tenant: source.tenant,
        worker: source.worker,
    };
    let destination_worker = WorkerKey {
        tenant: destination.tenant,
        worker: destination.worker,
    };
    let destination_tenant_exists = state.tenants.contains_key(&destination.tenant);
    let source_tenant_freed = source.tenant != destination.tenant
        && state.tenants.get(&source.tenant).copied() == Some(bytes);
    if !destination_tenant_exists
        && state.tenants.len() - usize::from(source_tenant_freed) >= config.max_tenant_accounts
    {
        return Err(TransferError::Admission(
            AdmissionError::AccountLimitExceeded {
                scope: AccountScope::Tenant,
                limit: config.max_tenant_accounts,
            },
        ));
    }

    let destination_worker_exists = state.workers.contains_key(&destination_worker);
    let source_worker_freed = source_worker != destination_worker
        && state.workers.get(&source_worker).copied() == Some(bytes);
    if !destination_worker_exists
        && state.workers.len() - usize::from(source_worker_freed) >= config.max_worker_accounts
    {
        return Err(TransferError::Admission(
            AdmissionError::AccountLimitExceeded {
                scope: AccountScope::Worker,
                limit: config.max_worker_accounts,
            },
        ));
    }

    if !destination_tenant_exists {
        state
            .tenants
            .try_reserve(1)
            .map_err(|_| TransferError::Admission(AdmissionError::AccountingAllocationFailed))?;
    }
    if !destination_worker_exists {
        state
            .workers
            .try_reserve(1)
            .map_err(|_| TransferError::Admission(AdmissionError::AccountingAllocationFailed))?;
    }
    Ok(())
}

fn apply_transfer(state: &mut State, source: Attribution, destination: Attribution, bytes: usize) {
    let source_worker = WorkerKey {
        tenant: source.tenant,
        worker: source.worker,
    };
    let destination_worker = WorkerKey {
        tenant: destination.tenant,
        worker: destination.worker,
    };
    decrement_map(&mut state.tenants, &source.tenant, bytes);
    decrement_map(&mut state.workers, &source_worker, bytes);
    state.categories[source.category.index()] -= bytes;
    let tenant_used = state.tenants.get(&destination.tenant).copied().unwrap_or(0);
    let worker_used = state.workers.get(&destination_worker).copied().unwrap_or(0);
    state
        .tenants
        .insert(destination.tenant, tenant_used + bytes);
    state
        .workers
        .insert(destination_worker, worker_used + bytes);
    state.categories[destination.category.index()] += bytes;
}

fn decrement_map<K: Eq + Hash>(map: &mut HashMap<K, usize>, key: &K, bytes: usize) {
    if let Some(used) = map.get_mut(key) {
        debug_assert!(*used >= bytes);
        if *used <= bytes {
            map.remove(key);
        } else {
            *used -= bytes;
        }
    } else {
        debug_assert!(false, "reservation accounting entry missing");
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::{Barrier, Mutex};
    use std::thread;

    fn tenant(value: u8) -> TenantId {
        TenantId::from_bytes([value; 16])
    }

    fn owner(tenant_id: u8, worker_id: u64, category: MemoryCategory) -> Attribution {
        Attribution {
            tenant: tenant(tenant_id),
            worker: WorkerId(worker_id),
            category,
        }
    }

    fn budget(limit: usize, headroom: usize) -> MemoryBudget {
        MemoryBudget::new(BudgetConfig::new(limit, headroom).expect("valid test config"))
    }

    #[test]
    fn admission_rejects_before_exceeding_process_limit_and_drop_releases() {
        let budget = budget(100, 20);
        let attribution = owner(1, 7, MemoryCategory::WriteBuffer);
        let first = budget
            .try_reserve(attribution, 80)
            .expect("usable capacity");
        assert!(matches!(
            budget.try_reserve(attribution, 1),
            Err(AdmissionError::LimitExceeded {
                scope: LimitScope::Process,
                ..
            })
        ));
        assert_eq!(budget.snapshot().reserved_bytes, 80);

        drop(first);
        let second = budget
            .try_reserve(attribution, 80)
            .expect("drop released bytes");
        assert_eq!(second.bytes(), 80);
        let snapshot = budget.snapshot();
        assert_eq!(snapshot.process_limit_bytes, 100);
        assert_eq!(snapshot.headroom_bytes, 20);
        assert_eq!(snapshot.usable_limit_bytes, 80);
        assert_eq!(snapshot.reserved_bytes, 80);
        assert_eq!(snapshot.categories[MemoryCategory::WriteBuffer.index()], 80);
    }

    #[test]
    fn accounting_addition_is_overflow_safe() {
        let budget = budget(usize::MAX, 0);
        let _full = budget
            .try_reserve(owner(1, 1, MemoryCategory::Other), usize::MAX)
            .expect("maximum representable reservation");
        assert!(matches!(
            budget.try_reserve(owner(1, 1, MemoryCategory::Other), 1),
            Err(AdmissionError::CounterOverflow {
                scope: LimitScope::Process,
            })
        ));

        let config = BudgetConfig::new(100, 0)
            .expect("valid test config")
            .with_item_limits(usize::MAX, usize::MAX, usize::MAX, usize::MAX);
        let item_budget = MemoryBudget::new(config);
        let attribution = owner(2, 2, MemoryCategory::Other);
        let _all_items = item_budget
            .try_reserve_items(attribution, usize::MAX)
            .expect("maximum representable item reservation");
        assert!(matches!(
            item_budget.try_reserve_items(attribution, 1),
            Err(AdmissionError::ItemCounterOverflow {
                scope: ItemLimitScope::Process,
            })
        ));
    }

    #[test]
    fn zero_byte_work_is_bounded_and_releases_item_slots() {
        let config = BudgetConfig::new(100, 0)
            .expect("valid test config")
            .with_item_limits(2, 2, 2, 2);
        let budget = MemoryBudget::new(config);
        let attribution = owner(1, 1, MemoryCategory::Query);

        let zero_bytes = budget.try_reserve(attribution, 0).expect("zero-byte no-op");
        assert_eq!(budget.snapshot().reserved_items, 0);
        drop(zero_bytes);

        let first = budget
            .try_reserve_items(attribution, 1)
            .expect("first work item");
        let second = budget
            .try_reserve_work(attribution, 0, 1)
            .expect("zero-byte work still consumes an item slot");
        assert_eq!(budget.snapshot().reserved_bytes, 0);
        assert_eq!(budget.snapshot().reserved_items, 2);
        assert!(matches!(
            budget.try_reserve_items(attribution, 1),
            Err(AdmissionError::ItemLimitExceeded {
                scope: ItemLimitScope::Process,
                ..
            })
        ));

        drop(first);
        let released = budget
            .try_reserve_items(attribution, 1)
            .expect("drop released item slot");
        assert_eq!(budget.snapshot().reserved_items, 2);
        drop(second);
        drop(released);
        let snapshot = budget.snapshot();
        assert_eq!(snapshot.reserved_items, 0);
        assert!(snapshot.tenant_items.is_empty());
        assert!(snapshot.worker_items.is_empty());
    }

    #[test]
    fn item_admission_enforces_tenant_worker_and_category_limits() {
        let tenant_budget = MemoryBudget::new(
            BudgetConfig::new(100, 0)
                .expect("valid test config")
                .with_item_limits(10, 2, 10, 10),
        );
        let _tenant_items = tenant_budget
            .try_reserve_items(owner(1, 1, MemoryCategory::Query), 2)
            .expect("tenant reaches item cap");
        assert!(matches!(
            tenant_budget.try_reserve_items(owner(1, 2, MemoryCategory::Query), 1),
            Err(AdmissionError::ItemLimitExceeded {
                scope: ItemLimitScope::Tenant,
                ..
            })
        ));

        let worker_budget = MemoryBudget::new(
            BudgetConfig::new(100, 0)
                .expect("valid test config")
                .with_item_limits(10, 10, 1, 10),
        );
        let _worker_item = worker_budget
            .try_reserve_items(owner(2, 2, MemoryCategory::Query), 1)
            .expect("worker reaches item cap");
        assert!(matches!(
            worker_budget.try_reserve_items(owner(2, 2, MemoryCategory::Query), 1),
            Err(AdmissionError::ItemLimitExceeded {
                scope: ItemLimitScope::Worker,
                ..
            })
        ));

        let category_budget = MemoryBudget::new(
            BudgetConfig::new(100, 0)
                .expect("valid test config")
                .with_item_limits(10, 10, 10, 10)
                .with_category_item_limit(MemoryCategory::Query, 1),
        );
        let _category_item = category_budget
            .try_reserve_items(owner(3, 3, MemoryCategory::Query), 1)
            .expect("category reaches item cap");
        assert!(matches!(
            category_budget.try_reserve_items(owner(4, 4, MemoryCategory::Query), 1),
            Err(AdmissionError::ItemLimitExceeded {
                scope: ItemLimitScope::Category,
                ..
            })
        ));
    }

    #[test]
    fn item_transfer_preserves_total_and_updates_attribution_atomically() {
        let config = BudgetConfig::new(100, 0)
            .expect("valid test config")
            .with_item_limits(10, 3, 3, 10);
        let budget = MemoryBudget::new(config);
        let mut source = budget
            .try_reserve_items(owner(1, 1, MemoryCategory::WriteBuffer), 2)
            .expect("source items");
        let moved = source
            .transfer(1, owner(2, 2, MemoryCategory::Replication))
            .expect("transfer item without increasing process usage");

        let snapshot = budget.snapshot();
        assert_eq!(snapshot.reserved_items, 2);
        assert_eq!(
            snapshot.category_items[MemoryCategory::WriteBuffer.index()],
            1
        );
        assert_eq!(
            snapshot.category_items[MemoryCategory::Replication.index()],
            1
        );
        assert_eq!(snapshot.tenant_items.len(), 2);
        drop(moved);
        drop(source);
        assert_eq!(budget.snapshot().reserved_items, 0);
    }

    #[test]
    fn combined_byte_and_item_admission_is_atomic() {
        let config = BudgetConfig::new(100, 0)
            .expect("valid test config")
            .with_item_limits(1, 1, 1, 1);
        let budget = MemoryBudget::new(config);
        let attribution = owner(1, 1, MemoryCategory::WriteBuffer);
        let item = budget
            .try_reserve_items(attribution, 1)
            .expect("occupy only item slot");
        assert!(matches!(
            budget.try_reserve_work(attribution, 10, 1),
            Err(AdmissionError::ItemLimitExceeded { .. })
        ));
        assert_eq!(budget.snapshot().reserved_bytes, 0);
        assert_eq!(budget.snapshot().reserved_items, 1);

        drop(item);
        let work = budget
            .try_reserve_work(attribution, 10, 1)
            .expect("atomic work reservation");
        assert_eq!(work.bytes(), 10);
        assert_eq!(work.items(), 1);
        assert_eq!(budget.snapshot().reserved_bytes, 10);
        assert_eq!(budget.snapshot().reserved_items, 1);
        drop(work);
        let snapshot = budget.snapshot();
        assert_eq!(snapshot.reserved_bytes, 0);
        assert_eq!(snapshot.reserved_items, 0);
    }

    #[test]
    fn zero_item_requests_are_rejected() {
        let budget = budget(100, 0);
        assert!(matches!(
            budget.try_reserve_items(owner(1, 1, MemoryCategory::Other), 0),
            Err(AdmissionError::ZeroItemCount)
        ));
        assert!(matches!(
            budget.try_reserve_work(owner(1, 1, MemoryCategory::Other), 1, 0),
            Err(AdmissionError::ZeroItemCount)
        ));
        assert_eq!(budget.snapshot().reserved_bytes, 0);
        assert_eq!(budget.snapshot().reserved_items, 0);
    }

    #[test]
    fn tenant_worker_and_category_caps_reject_unfair_admission() {
        let tenant_config = BudgetConfig::new(100, 0)
            .expect("valid test config")
            .with_tenant_limit_bytes(60);
        let tenant_budget = MemoryBudget::new(tenant_config);
        let _tenant_cap = tenant_budget
            .try_reserve(owner(1, 1, MemoryCategory::Other), 60)
            .expect("tenant may use its limit");
        assert!(matches!(
            tenant_budget.try_reserve(owner(1, 2, MemoryCategory::Other), 1),
            Err(AdmissionError::LimitExceeded {
                scope: LimitScope::Tenant,
                ..
            })
        ));

        let worker_config = BudgetConfig::new(100, 0)
            .expect("valid test config")
            .with_worker_limit_bytes(40);
        let worker_budget = MemoryBudget::new(worker_config);
        let _worker_cap = worker_budget
            .try_reserve(owner(2, 3, MemoryCategory::Other), 40)
            .expect("worker may use its limit");
        assert!(matches!(
            worker_budget.try_reserve(owner(2, 3, MemoryCategory::Other), 1),
            Err(AdmissionError::LimitExceeded {
                scope: LimitScope::Worker,
                ..
            })
        ));

        let category_config = BudgetConfig::new(100, 0)
            .expect("valid test config")
            .with_category_limit_bytes(MemoryCategory::Query, 30);
        let category_budget = MemoryBudget::new(category_config);
        let _category_cap = category_budget
            .try_reserve(owner(3, 4, MemoryCategory::Query), 30)
            .expect("category may use its limit");
        assert!(matches!(
            category_budget.try_reserve(owner(4, 5, MemoryCategory::Query), 1),
            Err(AdmissionError::LimitExceeded {
                scope: LimitScope::Category,
                ..
            })
        ));
    }

    #[test]
    fn split_and_transfer_preserve_total_and_release_exactly_once() {
        let budget = MemoryBudget::new(
            BudgetConfig::new(100, 0)
                .expect("valid test config")
                .with_item_limits(10, 10, 10, 10),
        );
        let mut source = budget
            .try_reserve(owner(1, 1, MemoryCategory::WriteBuffer), 60)
            .expect("initial reservation");
        let split = source.split(20).expect("split owned bytes");
        assert_eq!(source.bytes(), 40);
        assert_eq!(split.bytes(), 20);
        assert_eq!(budget.snapshot().reserved_bytes, 60);

        let moved = source
            .transfer(15, owner(2, 8, MemoryCategory::Replication))
            .expect("reattribute without a capacity gap");
        assert_eq!(source.bytes(), 25);
        assert_eq!(moved.attribution().tenant, tenant(2));
        let snapshot = budget.snapshot();
        assert_eq!(snapshot.reserved_bytes, 60);
        assert_eq!(
            snapshot
                .tenants
                .iter()
                .find(|(id, _)| *id == tenant(1))
                .unwrap()
                .1,
            45
        );
        assert_eq!(
            snapshot
                .tenants
                .iter()
                .find(|(id, _)| *id == tenant(2))
                .unwrap()
                .1,
            15
        );

        drop(split);
        drop(moved);
        assert_eq!(budget.snapshot().reserved_bytes, 25);
        drop(source);
        assert_eq!(budget.snapshot().reserved_bytes, 0);
        assert!(budget.snapshot().tenants.is_empty());
        assert!(budget.snapshot().workers.is_empty());
    }

    #[test]
    fn transfer_within_tenant_moves_worker_and_category_accounting() {
        let budget = budget(100, 0);
        let mut source = budget
            .try_reserve(owner(1, 1, MemoryCategory::WriteBuffer), 30)
            .expect("initial reservation");
        let moved = source
            .transfer(10, owner(1, 2, MemoryCategory::Query))
            .expect("transfer inside tenant");

        let snapshot = budget.snapshot();
        assert_eq!(snapshot.reserved_bytes, 30);
        assert_eq!(snapshot.tenants[0].1, 30);
        assert_eq!(snapshot.workers.len(), 2);
        assert_eq!(snapshot.categories[MemoryCategory::WriteBuffer.index()], 20);
        assert_eq!(snapshot.categories[MemoryCategory::Query.index()], 10);
        drop(moved);
        drop(source);
        assert_eq!(budget.snapshot().reserved_bytes, 0);
    }

    #[test]
    fn failed_transfer_keeps_source_unchanged() {
        let config = BudgetConfig::new(100, 0)
            .expect("valid test config")
            .with_tenant_limit_bytes(30);
        let budget = MemoryBudget::new(config);
        let mut source = budget
            .try_reserve(owner(1, 1, MemoryCategory::WriteBuffer), 20)
            .expect("source reservation");
        let _destination = budget
            .try_reserve(owner(2, 2, MemoryCategory::Query), 30)
            .expect("destination at cap");

        assert!(matches!(
            source.transfer(1, owner(2, 3, MemoryCategory::Replication)),
            Err(TransferError::Admission(AdmissionError::LimitExceeded {
                scope: LimitScope::Tenant,
                ..
            }))
        ));
        assert_eq!(source.bytes(), 20);
        assert_eq!(budget.snapshot().reserved_bytes, 50);
    }

    #[test]
    fn accounting_tables_bound_identifier_cardinality() {
        let config = BudgetConfig::new(100, 0)
            .expect("valid test config")
            .with_account_limits(1, 1);
        let budget = MemoryBudget::new(config);
        let _first = budget
            .try_reserve(owner(1, 1, MemoryCategory::Other), 1)
            .expect("first account");
        assert!(matches!(
            budget.try_reserve(owner(2, 2, MemoryCategory::Other), 1),
            Err(AdmissionError::AccountLimitExceeded {
                scope: AccountScope::Tenant,
                limit: 1,
            })
        ));
    }

    #[test]
    fn contended_admission_rejects_without_waiting_or_mutating_source() {
        let budget = budget(100, 0);
        let attribution = owner(1, 1, MemoryCategory::Query);
        let mut permit = budget
            .try_reserve(attribution, 10)
            .expect("initial reservation");
        let lock = budget.inner.state.lock().expect("controller lock");
        assert!(matches!(
            budget.try_reserve(attribution, 1),
            Err(AdmissionError::ControllerBusy)
        ));
        assert!(matches!(
            permit.transfer(1, owner(2, 2, MemoryCategory::Replication)),
            Err(TransferError::Admission(AdmissionError::ControllerBusy))
        ));
        assert_eq!(permit.bytes(), 10);
        drop(lock);
        assert_eq!(budget.snapshot().reserved_bytes, 10);
    }

    #[test]
    fn concurrent_reservations_never_cross_total_limit() {
        const THREADS: usize = 16;
        const PER_RESERVATION: usize = 10;
        let budget = MemoryBudget::new(
            BudgetConfig::new(100, 0)
                .expect("valid test config")
                .with_item_limits(10, 10, 10, 10),
        );
        let barrier = Arc::new(Barrier::new(THREADS));
        let held = Arc::new(Mutex::new(Vec::new()));
        let mut joins = Vec::with_capacity(THREADS);

        for index in 0..THREADS {
            let budget = budget.clone();
            let barrier = Arc::clone(&barrier);
            let held = Arc::clone(&held);
            joins.push(thread::spawn(move || {
                barrier.wait();
                if let Ok(permit) = budget.try_reserve_work(
                    owner(
                        u8::try_from(index).expect("bounded test index") + 1,
                        u64::try_from(index).expect("bounded test index"),
                        MemoryCategory::Query,
                    ),
                    PER_RESERVATION,
                    1,
                ) {
                    held.lock().expect("test lock").push(permit);
                }
            }));
        }

        for join in joins {
            join.join().expect("reservation thread");
        }
        let mut held_guard = held.lock().expect("test lock");
        assert!(held_guard.len() <= 10);
        let already_held = held_guard.len();
        assert_eq!(
            budget.snapshot().reserved_bytes,
            already_held * PER_RESERVATION
        );
        assert_eq!(budget.snapshot().reserved_items, already_held);
        for index in THREADS..(THREADS + 10 - already_held) {
            held_guard.push(
                budget
                    .try_reserve_work(
                        owner(
                            u8::try_from(index).expect("bounded test index") + 1,
                            u64::try_from(index).expect("bounded test index"),
                            MemoryCategory::Query,
                        ),
                        PER_RESERVATION,
                        1,
                    )
                    .expect("remaining capacity after contention"),
            );
        }
        assert_eq!(held_guard.len(), 10);
        drop(held_guard);
        assert_eq!(budget.snapshot().reserved_bytes, 100);
        assert_eq!(budget.snapshot().reserved_items, 10);
        held.lock().expect("test lock").clear();
        assert_eq!(budget.snapshot().reserved_bytes, 0);
        assert_eq!(budget.snapshot().reserved_items, 0);
    }
}
