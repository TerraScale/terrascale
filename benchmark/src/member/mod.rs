//! The root-owned Member catalog and lifecycle boundary.
//!
//! A [`Member`] owns one durable `Engine` for each configured
//! database.  This module deliberately knows nothing about TBEP, membership,
//! or replication: it reports storage health only.  A higher layer must add
//! those authorities before advertising production readiness.

#![allow(clippy::all, reason = "member lifecycle is being integrated with production authorities")]
#![allow(
    clippy::pedantic,
    reason = "member lifecycle is being integrated with production authorities"
)]

use std::{
    collections::{BTreeMap, BTreeSet},
    fs,
    ops::Deref,
    path::{Component, Path, PathBuf},
    sync::{Arc, Condvar, Mutex},
    time::{Duration, Instant},
};

use crate::engine::EngineConfigError;
use crate::identity::{DatabaseUlid, ProjectUlid, TenantUlid};
use crate::regions::RegionCode;
pub use crate::storage::{DatabaseOwner, EnvironmentName};
use crate::{Engine, EngineConfig, EngineOpenError, EngineShutdownError};

/// The maximum UTF-8 byte length of a database identity.
pub const MAX_DATABASE_ID_BYTES: usize = 26;
/// The maximum number of databases in one Member catalog.
pub const MAX_DATABASES: usize = 1_024;
/// Default resident RSS charge for one open SQLite database.
pub const DEFAULT_DATABASE_RSS_BYTES: usize = 16 * 1024 * 1024;
/// Default RSS budget for all resident SQLite databases.
pub const DEFAULT_RSS_BUDGET_BYTES: usize = 1 * 1024 * 1024 * 1024;
/// Default upper bound for simultaneous engine opens.
pub const DEFAULT_MAX_CONCURRENT_OPENS: usize = 4;
/// SQLite's one writer plus four fixed reader connections per open database.
pub const SQLITE_CONNECTIONS_PER_OPEN_DATABASE: usize = 5;
/// The maximum byte length accepted for a database location specification.
pub const MAX_LOCATION_BYTES: usize = 4_096;
/// The maximum admission capacity accepted by one Member.
pub const MAX_ADMISSION_PERMITS: usize = 1_000_000;

/// A zero-cost catalog view over the canonical ADR 0027 database identity.
///
/// The wrapped [`DatabaseUlid`] is the only database entity identity.  This
/// name exists solely to keep the storage catalog's API explicit.
#[derive(Clone, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub struct DatabaseId {
    text: String,
    typed: Option<DatabaseUlid>,
}

impl DatabaseId {
    /// Creates a database identity accepted by the Member catalog.
    ///
    /// Canonical ULID text is used for scoped production entries. Other
    /// bounded safe text is retained only for the explicit local-development
    /// compatibility constructor. The identity is never used directly as a
    /// filesystem path.
    ///
    /// # Errors
    ///
    /// Returns [`CatalogValidationError::InvalidDatabaseIdentity`] for empty,
    /// oversized, non-ASCII, or unsafe text.
    pub fn try_new(value: impl AsRef<str>) -> Result<Self, CatalogValidationError> {
        let value = value.as_ref();
        if value.is_empty() || value.len() > MAX_DATABASE_ID_BYTES || !value.is_ascii() {
            return Err(CatalogValidationError::InvalidDatabaseIdentity);
        }
        let typed = DatabaseUlid::from_text(value).ok();
        if typed.is_none()
            && !value
                .bytes()
                .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'-' | b'_' | b'.'))
        {
            return Err(CatalogValidationError::InvalidDatabaseIdentity);
        }
        Ok(Self { text: value.to_owned(), typed })
    }

    /// Returns the canonical identity text.
    #[must_use]
    pub fn as_str(&self) -> String {
        self.text.clone()
    }

    /// Returns the wrapped ADR 0027 identity.
    #[must_use]
    pub const fn value(&self) -> Option<DatabaseUlid> {
        self.typed
    }
}

impl From<DatabaseUlid> for DatabaseId {
    fn from(value: DatabaseUlid) -> Self {
        Self { text: value.to_text(), typed: Some(value) }
    }
}

impl std::fmt::Display for DatabaseId {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        formatter.write_str(&self.text)
    }
}

/// The typed environment and ownership scope supplied by production Member.
#[derive(Clone, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub struct DatabaseScope(DatabaseOwner);

impl DatabaseScope {
    /// Creates one canonical production database scope.
    ///
    /// # Errors
    ///
    /// Returns [`CatalogValidationError::InvalidEnvironment`] when the
    /// environment is empty, oversized, or contains unsafe bytes.
    pub fn try_new(
        environment: impl AsRef<str>,
        tenant: TenantUlid,
        project: ProjectUlid,
        database: DatabaseUlid,
        region: RegionCode,
    ) -> Result<Self, CatalogValidationError> {
        let environment = EnvironmentName::try_new(environment)
            .map_err(|_| CatalogValidationError::InvalidEnvironment)?;
        Ok(Self(DatabaseOwner::new(environment, tenant, project, database, region)))
    }

    /// Alias for [`Self::try_new`] for callers constructing a typed catalog.
    ///
    /// # Errors
    ///
    /// Returns the same error as [`Self::try_new`].
    pub fn new(
        environment: impl AsRef<str>,
        tenant: TenantUlid,
        project: ProjectUlid,
        database: DatabaseUlid,
        region: RegionCode,
    ) -> Result<Self, CatalogValidationError> {
        Self::try_new(environment, tenant, project, database, region)
    }

    /// Returns the durable owner tuple.
    #[must_use]
    pub fn owner(&self) -> &DatabaseOwner {
        &self.0
    }

    /// Returns the canonical database identity.
    #[must_use]
    pub const fn database(&self) -> DatabaseUlid {
        self.0.database()
    }

    /// Returns the owning tenant identity.
    #[must_use]
    pub const fn tenant(&self) -> TenantUlid {
        self.0.tenant()
    }

    /// Returns the owning Project identity.
    #[must_use]
    pub const fn project(&self) -> ProjectUlid {
        self.0.project()
    }

    /// Returns the environment identity.
    #[must_use]
    pub fn environment(&self) -> &EnvironmentName {
        self.0.environment()
    }

    /// Returns the home Region.
    #[must_use]
    pub const fn region(&self) -> RegionCode {
        self.0.region()
    }
}

/// One database's logical identity and relative durable location.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct DatabaseSpec {
    /// The logical database identity.
    pub id: String,
    /// A relative location beneath the Member root.
    pub location: PathBuf,
    scope: Option<DatabaseScope>,
}

impl DatabaseSpec {
    /// Creates a database specification.  [`Member::open`] repeats all
    /// validation so callers cannot bypass the catalog boundary by using a
    /// struct literal.
    #[must_use]
    pub fn new(id: impl Into<String>, location: impl Into<PathBuf>) -> Self {
        Self { id: id.into(), location: location.into(), scope: None }
    }

    /// Creates a production-scoped specification. The database identity in
    /// the scope, rather than request text, is the catalog identity.
    #[must_use]
    pub fn scoped(scope: DatabaseScope, location: impl Into<PathBuf>) -> Self {
        Self { id: scope.database().to_text(), location: location.into(), scope: Some(scope) }
    }

    /// Returns the optional production owner scope.
    #[must_use]
    pub fn scope(&self) -> Option<&DatabaseScope> {
        self.scope.as_ref()
    }
}

/// Admission capacities for one Member.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct AdmissionLimits {
    /// Maximum operations admitted across the whole Member.
    pub global: usize,
    /// Maximum operations admitted for one database.
    pub per_database: usize,
    /// Maximum resident RSS charge across open database engines.
    pub rss_budget_bytes: usize,
    /// Conservative RSS charge reserved for one open database.
    pub database_rss_bytes: usize,
    /// Optional count cap retained as a secondary guard to the RSS budget.
    pub max_open_databases: usize,
    /// Maximum concurrent lazy engine opens.
    pub max_concurrent_opens: usize,
}

impl AdmissionLimits {
    /// Creates admission limits.  Zero and excessively large limits are
    /// rejected when the catalog is opened.
    #[must_use]
    pub const fn new(global: usize, per_database: usize) -> Self {
        Self {
            global,
            per_database,
            rss_budget_bytes: DEFAULT_RSS_BUDGET_BYTES,
            database_rss_bytes: DEFAULT_DATABASE_RSS_BYTES,
            max_open_databases: MAX_DATABASES,
            max_concurrent_opens: DEFAULT_MAX_CONCURRENT_OPENS,
        }
    }

    /// Sets the hard resident RSS budget. The byte budget, not an engine
    /// count, is the authority used for eviction.
    #[must_use]
    pub const fn with_rss_budget_bytes(mut self, bytes: usize) -> Self {
        self.rss_budget_bytes = bytes;
        self
    }

    /// Alias for [`Self::with_rss_budget_bytes`] used by callers that name
    /// the resident set directly.
    #[must_use]
    pub const fn with_resident_rss_budget(mut self, bytes: usize) -> Self {
        self.rss_budget_bytes = bytes;
        self
    }

    /// Sets the conservative RSS charge reserved for each open database.
    #[must_use]
    pub const fn with_database_rss_bytes(mut self, bytes: usize) -> Self {
        self.database_rss_bytes = bytes;
        self
    }

    /// Alias for [`Self::with_database_rss_bytes`].
    #[must_use]
    pub const fn with_database_rss_charge(mut self, bytes: usize) -> Self {
        self.database_rss_bytes = bytes;
        self
    }

    /// Sets the maximum number of resident databases as a compatibility cap.
    /// RSS remains the primary bound, so this cannot make the byte budget grow.
    #[must_use]
    pub const fn with_max_open_databases(mut self, count: usize) -> Self {
        self.max_open_databases = count;
        self.max_concurrent_opens = if self.max_concurrent_opens < count {
            self.max_concurrent_opens
        } else if count == 0 {
            0
        } else {
            count
        };
        self
    }

    /// Sets the bound on simultaneous engine opens.
    #[must_use]
    pub const fn with_max_concurrent_opens(mut self, count: usize) -> Self {
        self.max_concurrent_opens = count;
        self
    }
}

impl Default for AdmissionLimits {
    fn default() -> Self {
        Self::new(256, 64)
    }
}

/// Catalog validation failures that do not open any engine.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum CatalogValidationError {
    /// The configured root must be an existing, non-symlink directory.
    InvalidRoot,
    /// The root or a database location contains a symlink component.
    SymlinkComponent,
    /// A database identity is empty, oversized, or contains unsafe bytes.
    InvalidDatabaseIdentity,
    /// A database location is empty, absolute, or contains traversal.
    InvalidDatabaseLocation,
    /// No database entries were supplied.
    EmptyCatalog,
    /// A location specification exceeds the bounded path size.
    LocationTooLong,
    /// More than [`MAX_DATABASES`] entries were supplied.
    CatalogTooLarge,
    /// Two entries use the same database identity.
    DuplicateDatabaseIdentity,
    /// Two entries resolve to the same durable location.
    DuplicateDatabaseLocation,
    /// Two entries carry the same typed production scope.
    DuplicateDatabaseScope,
    /// A production catalog entry has no typed owner scope.
    UnscopedDatabase,
    /// The environment identity is malformed.
    InvalidEnvironment,
    /// Admission limits are zero or outside the accepted bound.
    InvalidAdmissionLimits,
}

/// Failures while opening the validated Member catalog.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum MemberOpenError {
    /// The catalog did not pass validation.
    Validation(CatalogValidationError),
    /// Engine configuration could not be constructed for a database.
    EngineConfig {
        /// The database whose configuration failed.
        database: DatabaseId,
        /// The engine configuration refusal.
        error: EngineConfigError,
    },
    /// A database engine failed to open.  No Member is returned.
    EngineOpen {
        /// The database whose engine failed to open.
        database: DatabaseId,
        /// The durable engine open failure.
        error: EngineOpenError,
    },
    /// A validation probe failed its durable shutdown.
    EngineShutdown {
        /// The database whose probe could not flush.
        database: DatabaseId,
        /// The durable shutdown failure.
        error: EngineShutdownError,
    },
}

/// Errors returned when acquiring or draining the Member.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum MemberError {
    /// No database with this identity is configured.
    UnknownDatabase(DatabaseId),
    /// The Member is draining or has stopped.
    Draining,
    /// The requested operation exceeded a global or database capacity.
    Overloaded(AdmissionScope),
    /// The database engine is no longer available.
    StorageUnavailable(DatabaseId),
    /// The RSS/count resident budget has no idle database available for
    /// eviction; active leases pin every resident engine.
    OpenDatabaseBudgetExhausted {
        /// The configured RSS budget in bytes.
        rss_budget_bytes: usize,
    },
    /// A resident database cannot be closed while an operation pins it.
    DatabaseBusy(DatabaseId),
    /// A lazy-open path failed catalog validation.
    Validation(CatalogValidationError),
    /// A lazy-open path could not construct engine configuration.
    EngineConfig {
        /// The database whose configuration failed.
        database: DatabaseId,
        /// The configuration failure.
        error: EngineConfigError,
    },
    /// A lazy-open path could not open the engine.
    EngineOpen {
        /// The database whose engine failed.
        database: DatabaseId,
        /// The engine failure.
        error: EngineOpenError,
    },
    /// The bounded drain deadline elapsed while work remained active.
    DrainTimeout,
    /// An engine failed its final durable flush.
    Shutdown {
        /// The database whose engine failed to close.
        database: DatabaseId,
        /// The durable engine shutdown failure.
        error: EngineShutdownError,
    },
}

/// Identifies which admission limit rejected an operation.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum AdmissionScope {
    /// The Member-wide limit was reached.
    Global,
    /// The selected database's limit was reached.
    Database,
}

/// Member lifecycle state.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum LifecycleState {
    /// The catalog is opening.  This state is observable only conceptually;
    /// [`Member::open`] publishes no handle until opening completes.
    Starting,
    /// Storage is open and new work may be admitted.
    Serving,
    /// New work is refused while existing work drains.
    Draining,
    /// All engines have been flushed and closed.
    Stopped,
}

/// Storage-only health exposed to an upper composition layer.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum StorageHealth {
    /// Every configured database opened successfully.
    Healthy,
    /// Storage is flushing while the Member drains.
    Draining,
    /// Storage has been closed.
    Stopped,
}

/// A storage status snapshot.  `production_ready` is always false here:
/// membership and replication health belong to the upper Member composition.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct MemberStatus {
    /// Current lifecycle state.
    pub lifecycle: LifecycleState,
    /// Current storage-only health.
    pub storage: StorageHealth,
    /// Number of configured databases.
    pub databases: usize,
    /// Number of resident database engines.
    pub open_databases: usize,
    /// Current conservative resident RSS charge.
    pub resident_rss_bytes: usize,
    /// Configured resident RSS budget.
    pub rss_budget_bytes: usize,
    /// Maximum SQLite connections implied by both resident bounds.
    pub sqlite_connection_budget: usize,
    /// Successful lazy engine opens since Member construction.
    pub database_opens: u64,
    /// Successful idle LRU evictions since Member construction.
    pub database_evictions: u64,
    /// Successful explicit/member closes since Member construction.
    pub database_closes: u64,
    /// Number of admitted operations currently holding permits.
    pub active_operations: usize,
    /// Always false until an upper layer supplies membership and replication.
    pub production_ready: bool,
}

struct DatabaseEntry {
    location: PathBuf,
    scope: Option<DatabaseScope>,
    engine: ResidentEngine,
    /// The last request time. LRU ordering uses the separate monotonic stamp
    /// so ties are deterministic even when the platform clock is coarse.
    last_used: Instant,
    access_stamp: u64,
    admissions: Arc<AdmissionCounter>,
}

enum ResidentEngine {
    Closed,
    Opening,
    Open(Arc<Engine>),
    Closing,
}

#[derive(Default)]
struct CatalogMetrics {
    opens: u64,
    evictions: u64,
    closes: u64,
}

struct CatalogState {
    databases: BTreeMap<DatabaseId, DatabaseEntry>,
    open_databases: usize,
    resident_rss_bytes: usize,
    opening_databases: usize,
    access_clock: u64,
    metrics: CatalogMetrics,
}

impl CatalogState {
    fn next_access(&mut self) -> u64 {
        self.access_clock = self.access_clock.saturating_add(1);
        self.access_clock
    }
}

struct MemberInner {
    root: PathBuf,
    catalog: Mutex<CatalogState>,
    catalog_changed: Condvar,
    global_admissions: Arc<AdmissionCounter>,
    lifecycle: Mutex<LifecycleState>,
    rss_budget_bytes: usize,
    database_rss_bytes: usize,
    max_open_databases: usize,
    max_concurrent_opens: usize,
    backup_pause: Mutex<Option<BackupPauseCallback>>,
}

/// Callback invoked immediately before a resident database is closed. The
/// backup owner should pause its per-file stream before dropping the SQLite
/// file, then resume it after a later acquire reopens the database.
pub type BackupPauseCallback = Arc<dyn Fn(&DatabaseId) + Send + Sync + 'static>;

/// One opened, isolated Member catalog.
#[derive(Clone)]
pub struct Member {
    inner: Arc<MemberInner>,
}

/// Compatibility name for the root-owned database catalog.
pub type MemberCatalog = Member;

impl Member {
    /// Validates the complete catalog, probing each durable engine once, then
    /// publishes only closed entries. Engines are reopened lazily and kept in
    /// an RSS-budgeted LRU, so the catalog itself never retains one engine per
    /// configured database.
    ///
    /// The post-creation recheck closes the ordinary symlink/escape gap.  The
    /// standard library does not expose descriptor-relative no-follow
    /// directory creation, so an untrusted process racing this code can still
    /// create a TOCTOU window; the configured root must therefore be writable
    /// only by the trusted Member process.
    ///
    /// # Errors
    ///
    /// Returns [`MemberOpenError::Validation`] for invalid catalog input or a
    /// typed engine error when a location cannot be opened.
    pub fn open(
        root: impl AsRef<Path>,
        specifications: impl IntoIterator<Item = DatabaseSpec>,
        limits: AdmissionLimits,
    ) -> Result<Self, MemberOpenError> {
        let root = validate_root(root.as_ref()).map_err(MemberOpenError::Validation)?;
        validate_limits(limits).map_err(MemberOpenError::Validation)?;
        let specs = collect_and_validate_specs(&root, specifications)
            .map_err(MemberOpenError::Validation)?;

        for (id, location, scope) in &specs {
            let config = EngineConfig::durable(location.clone())
                .map(|config| {
                    scope
                        .as_ref()
                        .map_or(config.clone(), |scope| config.with_owner(scope.owner().clone()))
                })
                .map_err(|error| MemberOpenError::EngineConfig { database: id.clone(), error });
            let config = match config {
                Ok(config) => config,
                Err(error) => return Err(error),
            };
            if let Err(error) = revalidate_created_location(&root, &location) {
                return Err(MemberOpenError::Validation(error));
            }
            let engine = Engine::open(config)
                .map_err(|error| MemberOpenError::EngineOpen { database: id.clone(), error })?;
            if let Err(error) = engine.shutdown() {
                return Err(MemberOpenError::EngineShutdown { database: id.clone(), error });
            }
        }

        let databases = specs
            .into_iter()
            .map(|(id, location, scope)| {
                let entry = DatabaseEntry {
                    location,
                    scope,
                    engine: ResidentEngine::Closed,
                    last_used: Instant::now(),
                    access_stamp: 0,
                    admissions: Arc::new(AdmissionCounter::new(limits.per_database)),
                };
                (id, entry)
            })
            .collect();
        Ok(Self {
            inner: Arc::new(MemberInner {
                root,
                catalog: Mutex::new(CatalogState {
                    databases,
                    open_databases: 0,
                    resident_rss_bytes: 0,
                    opening_databases: 0,
                    access_clock: 0,
                    metrics: CatalogMetrics::default(),
                }),
                catalog_changed: Condvar::new(),
                global_admissions: Arc::new(AdmissionCounter::new(limits.global)),
                lifecycle: Mutex::new(LifecycleState::Serving),
                rss_budget_bytes: limits.rss_budget_bytes,
                database_rss_bytes: limits.database_rss_bytes,
                max_open_databases: limits.max_open_databases,
                max_concurrent_opens: limits.max_concurrent_opens,
                backup_pause: Mutex::new(None),
            }),
        })
    }

    /// Opens a production catalog. Every specification must carry a typed
    /// [`DatabaseScope`]; compatibility string specifications are refused.
    ///
    /// # Errors
    ///
    /// Returns [`MemberOpenError::Validation`] when any entry is unscoped or
    /// fails catalog validation.
    pub fn open_production(
        root: impl AsRef<Path>,
        specifications: impl IntoIterator<Item = DatabaseSpec>,
        limits: AdmissionLimits,
    ) -> Result<Self, MemberOpenError> {
        let specifications: Vec<_> = specifications.into_iter().collect();
        if specifications.iter().any(|spec| spec.scope.is_none()) {
            return Err(MemberOpenError::Validation(CatalogValidationError::UnscopedDatabase));
        }
        Self::open(root, specifications, limits)
    }

    /// Returns the configured root as validated at startup.
    #[must_use]
    pub fn root(&self) -> &Path {
        &self.inner.root
    }

    /// Returns a sorted iterator over configured database identities.
    pub fn database_ids(&self) -> std::vec::IntoIter<DatabaseId> {
        lock_unpoisoned(&self.inner.catalog)
            .databases
            .keys()
            .cloned()
            .collect::<Vec<_>>()
            .into_iter()
    }

    /// Returns storage-only status.  Production readiness remains false.
    #[must_use]
    pub fn status(&self) -> MemberStatus {
        let lifecycle = *lock_unpoisoned(&self.inner.lifecycle);
        let catalog = lock_unpoisoned(&self.inner.catalog);
        let storage = match lifecycle {
            LifecycleState::Serving => StorageHealth::Healthy,
            LifecycleState::Draining => StorageHealth::Draining,
            LifecycleState::Stopped | LifecycleState::Starting => StorageHealth::Stopped,
        };
        MemberStatus {
            lifecycle,
            storage,
            databases: catalog.databases.len(),
            open_databases: catalog.open_databases,
            resident_rss_bytes: catalog.resident_rss_bytes,
            rss_budget_bytes: self.inner.rss_budget_bytes,
            sqlite_connection_budget: self.sqlite_connection_budget(),
            database_opens: catalog.metrics.opens,
            database_evictions: catalog.metrics.evictions,
            database_closes: catalog.metrics.closes,
            active_operations: self.inner.global_admissions.active(),
            production_ready: false,
        }
    }

    /// Attempts to admit work for one database.
    ///
    /// The returned lease keeps both global and per-database permits until it
    /// is dropped, and dereferences to that database's Engine.
    ///
    /// # Errors
    ///
    /// Returns [`MemberError::Draining`] when the Member is not serving,
    /// [`MemberError::UnknownDatabase`] for an unconfigured identity, or a
    /// typed capacity/storage refusal.
    pub fn acquire(&self, database: &DatabaseId) -> Result<DatabaseLease, MemberError> {
        if *lock_unpoisoned(&self.inner.lifecycle) != LifecycleState::Serving {
            return Err(MemberError::Draining);
        }
        let global = self.inner.global_admissions.try_acquire(AdmissionScope::Global)?;
        let (local, location) = {
            let catalog = lock_unpoisoned(&self.inner.catalog);
            let entry = catalog
                .databases
                .get(database)
                .ok_or_else(|| MemberError::UnknownDatabase(database.clone()))?;
            let local = match entry.admissions.try_acquire(AdmissionScope::Database) {
                Ok(permit) => permit,
                Err(error) => {
                    drop(global);
                    return Err(error);
                }
            };
            (local, entry.location.clone())
        };
        let engine = match open_database_engine(&self.inner, database, &location) {
            Ok(engine) => engine,
            Err(error) => {
                drop(local);
                drop(global);
                return Err(error);
            }
        };
        Ok(DatabaseLease {
            database: database.clone(),
            location,
            engine,
            _global: global,
            _local: local,
        })
    }

    /// Installs the callback used to pause a database's backup stream before
    /// idle/LRU eviction. The callback is never called while the catalog lock
    /// is held.
    pub fn set_backup_pause_callback(&self, callback: Option<BackupPauseCallback>) {
        *lock_unpoisoned(&self.inner.backup_pause) = callback;
    }

    /// Convenience builder for wiring the per-file backup pause callback.
    #[must_use]
    pub fn with_backup_pause_callback(
        self,
        callback: impl Fn(&DatabaseId) + Send + Sync + 'static,
    ) -> Self {
        self.set_backup_pause_callback(Some(Arc::new(callback)));
        self
    }

    /// Closes every open database that has had no request for at least
    /// `idle`. The entries remain in the catalog and reopen on demand.
    #[must_use]
    pub fn close_idle(&self, idle: Duration) -> usize {
        self.close_idle_at(idle, Instant::now())
    }

    /// Deterministic variant of [`Self::close_idle`] for a caller that owns a
    /// test clock. LRU order is always the monotonic access stamp, never the
    /// platform clock's resolution.
    #[must_use]
    pub fn close_idle_at(&self, idle: Duration, now: Instant) -> usize {
        if *lock_unpoisoned(&self.inner.lifecycle) != LifecycleState::Serving {
            return 0;
        }
        let databases = {
            let catalog = lock_unpoisoned(&self.inner.catalog);
            catalog
                .databases
                .iter()
                .filter(|(_, entry)| {
                    matches!(entry.engine, ResidentEngine::Open(_))
                        && now.saturating_duration_since(entry.last_used) >= idle
                        && entry.admissions.active() == 0
                })
                .map(|(database, _)| database.clone())
                .collect::<Vec<_>>()
        };
        let mut closed = 0;
        for database in databases {
            if close_database_engine(&self.inner, &database, CloseKind::Eviction).is_ok() {
                closed += 1;
            }
        }
        closed
    }

    /// Returns the number of resident database engines.
    #[must_use]
    pub fn open_database_count(&self) -> usize {
        lock_unpoisoned(&self.inner.catalog).open_databases
    }

    /// Returns the secondary count guard for resident engines.
    #[must_use]
    pub fn max_open_databases(&self) -> usize {
        self.inner.max_open_databases
    }

    /// Returns the configured RSS budget.
    #[must_use]
    pub fn rss_budget_bytes(&self) -> usize {
        self.inner.rss_budget_bytes
    }

    /// Returns the configured SQLite connection budget implied by the
    /// resident RSS budget and fixed per-engine pool width.
    #[must_use]
    pub fn sqlite_connection_budget(&self) -> usize {
        self.inner
            .rss_budget_bytes
            .saturating_div(self.inner.database_rss_bytes.max(1))
            .min(self.inner.max_open_databases)
            .saturating_mul(SQLITE_CONNECTIONS_PER_OPEN_DATABASE)
    }

    /// Explicitly closes one resident database while retaining its catalog
    /// entry. A lease pins it and returns `DatabaseBusy`.
    pub fn close_database(&self, database: &DatabaseId) -> Result<(), MemberError> {
        close_database_engine(&self.inner, database, CloseKind::Explicit)
    }

    /// Changes the Member to draining and waits for admitted work to finish.
    ///
    /// # Errors
    ///
    /// Returns [`MemberError::DrainTimeout`] when work remains at the
    /// deadline, or [`MemberError::Draining`] after shutdown.
    pub fn drain(&self, timeout: Duration) -> Result<(), MemberError> {
        self.begin_draining()?;
        if self.inner.global_admissions.wait_for_zero(timeout) {
            Ok(())
        } else {
            Err(MemberError::DrainTimeout)
        }
    }

    /// Drains and closes every durable engine within `timeout`.
    ///
    /// # Errors
    ///
    /// Returns [`MemberError::DrainTimeout`] if work remains or
    /// [`MemberError::Shutdown`] when a final flush fails.
    pub fn close(&self, timeout: Duration) -> Result<(), MemberError> {
        self.begin_draining()?;
        if !self.inner.global_admissions.wait_for_zero(timeout) {
            return Err(MemberError::DrainTimeout);
        }
        let databases =
            lock_unpoisoned(&self.inner.catalog).databases.keys().cloned().collect::<Vec<_>>();
        let mut first_error = None;
        for database in databases {
            if let Err(error) = close_database_engine(&self.inner, &database, CloseKind::Member) {
                if first_error.is_none() {
                    first_error = Some(error);
                }
            }
        }
        *lock_unpoisoned(&self.inner.lifecycle) = LifecycleState::Stopped;
        first_error.map_or(Ok(()), Err)
    }

    fn begin_draining(&self) -> Result<(), MemberError> {
        let mut state = lock_unpoisoned(&self.inner.lifecycle);
        match *state {
            LifecycleState::Serving => {
                *state = LifecycleState::Draining;
                Ok(())
            }
            LifecycleState::Draining => Ok(()),
            LifecycleState::Starting | LifecycleState::Stopped => Err(MemberError::Draining),
        }
    }
}

/// An admitted operation's isolated database engine handle.
pub struct DatabaseLease {
    database: DatabaseId,
    location: PathBuf,
    engine: Arc<Engine>,
    _global: AdmissionPermit,
    _local: AdmissionPermit,
}

impl DatabaseLease {
    /// Returns the database identity for this lease.
    #[must_use]
    pub fn database_id(&self) -> &DatabaseId {
        &self.database
    }

    /// Returns the validated durable location.  Routing layers should use the
    /// identity, not this path, when handling requests.
    #[must_use]
    pub fn location(&self) -> &Path {
        &self.location
    }

    /// Returns the durable owner tuple recovered for this database.
    #[must_use]
    pub fn database_owner(&self) -> &DatabaseOwner {
        self.engine.database_owner()
    }
}

impl Deref for DatabaseLease {
    type Target = Engine;

    fn deref(&self) -> &Self::Target {
        &self.engine
    }
}

fn validate_limits(limits: AdmissionLimits) -> Result<(), CatalogValidationError> {
    if limits.global == 0
        || limits.per_database == 0
        || limits.rss_budget_bytes == 0
        || limits.database_rss_bytes == 0
        || limits.database_rss_bytes > limits.rss_budget_bytes
        || limits.max_open_databases == 0
        || limits.max_open_databases > MAX_DATABASES
        || limits.max_concurrent_opens == 0
        || limits.max_concurrent_opens > limits.max_open_databases
        || limits.global > MAX_ADMISSION_PERMITS
        || limits.per_database > MAX_ADMISSION_PERMITS
    {
        Err(CatalogValidationError::InvalidAdmissionLimits)
    } else {
        Ok(())
    }
}

fn validate_root(root: &Path) -> Result<PathBuf, CatalogValidationError> {
    if !root.is_absolute() || !root.is_dir() {
        return Err(CatalogValidationError::InvalidRoot);
    }
    reject_symlink_components(root)?;
    fs::canonicalize(root).map_err(|_| CatalogValidationError::InvalidRoot)
}

fn collect_and_validate_specs(
    root: &Path,
    specifications: impl IntoIterator<Item = DatabaseSpec>,
) -> Result<Vec<(DatabaseId, PathBuf, Option<DatabaseScope>)>, CatalogValidationError> {
    let mut ids = BTreeSet::new();
    let mut locations = BTreeSet::new();
    let mut scopes = BTreeSet::new();
    let mut validated = Vec::new();
    for (index, spec) in specifications.into_iter().enumerate() {
        if index >= MAX_DATABASES {
            return Err(CatalogValidationError::CatalogTooLarge);
        }
        if spec.location.as_os_str().is_empty() {
            return Err(CatalogValidationError::InvalidDatabaseLocation);
        }
        if spec.location.as_os_str().len() > MAX_LOCATION_BYTES {
            return Err(CatalogValidationError::LocationTooLong);
        }
        let scope = spec.scope;
        let id = DatabaseId::try_new(spec.id)?;
        if let Some(scope) = &scope {
            if id.value() != Some(scope.database()) {
                return Err(CatalogValidationError::InvalidDatabaseIdentity);
            }
            if !scopes.insert(scope.clone()) {
                return Err(CatalogValidationError::DuplicateDatabaseScope);
            }
        }
        let location = validate_location(root, &spec.location)?;
        if !ids.insert(id.clone()) {
            return Err(CatalogValidationError::DuplicateDatabaseIdentity);
        }
        if !locations.insert(location.clone()) {
            return Err(CatalogValidationError::DuplicateDatabaseLocation);
        }
        validated.push((id, location, scope));
    }
    if validated.is_empty() {
        return Err(CatalogValidationError::EmptyCatalog);
    }
    Ok(validated)
}

fn validate_location(root: &Path, relative: &Path) -> Result<PathBuf, CatalogValidationError> {
    if relative.is_absolute()
        || relative.components().any(|component| {
            matches!(
                component,
                Component::ParentDir
                    | Component::CurDir
                    | Component::RootDir
                    | Component::Prefix(_)
            )
        })
    {
        return Err(CatalogValidationError::InvalidDatabaseLocation);
    }
    let candidate = root.join(relative);
    reject_symlink_components(&candidate)?;
    let (existing, missing) = split_existing_prefix(&candidate);
    if !existing.is_dir() {
        return Err(CatalogValidationError::InvalidDatabaseLocation);
    }
    let mut canonical =
        fs::canonicalize(existing).map_err(|_| CatalogValidationError::InvalidDatabaseLocation)?;
    for component in missing.iter().rev() {
        canonical.push(component);
    }
    if !canonical.starts_with(root) {
        return Err(CatalogValidationError::InvalidDatabaseLocation);
    }
    Ok(canonical)
}

fn revalidate_created_location(root: &Path, location: &Path) -> Result<(), CatalogValidationError> {
    reject_symlink_components(location)?;
    let canonical =
        fs::canonicalize(location).map_err(|_| CatalogValidationError::InvalidDatabaseLocation)?;
    if !canonical.starts_with(root) || canonical != location || !canonical.is_dir() {
        return Err(CatalogValidationError::InvalidDatabaseLocation);
    }
    Ok(())
}

fn split_existing_prefix(path: &Path) -> (PathBuf, Vec<std::ffi::OsString>) {
    let mut existing = path.to_path_buf();
    let mut missing = Vec::new();
    while !existing.exists() {
        if let Some(name) = existing.file_name() {
            missing.push(name.to_owned());
        }
        if !existing.pop() {
            break;
        }
    }
    (existing, missing)
}

fn reject_symlink_components(path: &Path) -> Result<(), CatalogValidationError> {
    let mut current = PathBuf::new();
    for component in path.components() {
        current.push(component.as_os_str());
        match fs::symlink_metadata(&current) {
            Ok(metadata) if metadata.file_type().is_symlink() => {
                return Err(CatalogValidationError::SymlinkComponent);
            }
            Ok(_) => {}
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => break,
            Err(_) => return Err(CatalogValidationError::InvalidDatabaseLocation),
        }
    }
    Ok(())
}

#[derive(Clone, Copy, Eq, PartialEq)]
enum CloseKind {
    Eviction,
    Explicit,
    Member,
}

struct CloseWork {
    database: DatabaseId,
    engine: Arc<Engine>,
    kind: CloseKind,
}

fn take_lru_eviction(catalog: &mut CatalogState) -> Option<CloseWork> {
    let database = catalog
        .databases
        .iter()
        .filter(|(_, entry)| {
            entry.admissions.active() == 0 && matches!(entry.engine, ResidentEngine::Open(_))
        })
        .min_by_key(|(_, entry)| (entry.last_used, entry.access_stamp))
        .map(|(database, _)| database.clone())?;
    take_close_work(catalog, &database, CloseKind::Eviction)
}

fn take_close_work(
    catalog: &mut CatalogState,
    database: &DatabaseId,
    kind: CloseKind,
) -> Option<CloseWork> {
    let entry = catalog.databases.get_mut(database)?;
    let resident = std::mem::replace(&mut entry.engine, ResidentEngine::Closing);
    match resident {
        ResidentEngine::Open(engine) => {
            Some(CloseWork { database: database.clone(), engine, kind })
        }
        other => {
            entry.engine = other;
            None
        }
    }
}

fn pause_backup(inner: &MemberInner, database: &DatabaseId) {
    let callback = lock_unpoisoned(&inner.backup_pause).clone();
    if let Some(callback) = callback {
        callback(database);
    }
}

fn perform_close(inner: &MemberInner, close: CloseWork) -> Result<(), MemberError> {
    let engine = match Arc::try_unwrap(close.engine) {
        Ok(engine) => engine,
        Err(engine) => {
            restore_close(inner, &close.database, engine);
            return Err(MemberError::DatabaseBusy(close.database));
        }
    };
    // Pause only after proving that no lease retained the engine. This keeps
    // a failed/pinned eviction from stopping a healthy backup stream.
    pause_backup(inner, &close.database);
    let result = engine
        .shutdown()
        .map_err(|error| MemberError::Shutdown { database: close.database.clone(), error });
    let mut catalog = lock_unpoisoned(&inner.catalog);
    if !catalog.databases.contains_key(&close.database) {
        return result;
    }
    match result {
        Ok(()) => {
            if let Some(entry) = catalog.databases.get_mut(&close.database) {
                entry.engine = ResidentEngine::Closed;
            }
            catalog.open_databases = catalog.open_databases.saturating_sub(1);
            catalog.resident_rss_bytes =
                catalog.resident_rss_bytes.saturating_sub(inner.database_rss_bytes);
            if close.kind == CloseKind::Eviction {
                catalog.metrics.evictions = catalog.metrics.evictions.saturating_add(1);
            } else if close.kind == CloseKind::Explicit {
                catalog.metrics.closes = catalog.metrics.closes.saturating_add(1);
            }
            inner.catalog_changed.notify_all();
            Ok(())
        }
        Err(error) => {
            // `Engine::shutdown` consumes its handle even on an error. Keep
            // the catalog coherent and release the reservation; a later
            // request performs a normal validated reopen.
            if let Some(entry) = catalog.databases.get_mut(&close.database) {
                entry.engine = ResidentEngine::Closed;
            }
            catalog.open_databases = catalog.open_databases.saturating_sub(1);
            catalog.resident_rss_bytes =
                catalog.resident_rss_bytes.saturating_sub(inner.database_rss_bytes);
            inner.catalog_changed.notify_all();
            Err(error)
        }
    }
}

fn restore_close(inner: &MemberInner, database: &DatabaseId, engine: Arc<Engine>) {
    let mut catalog = lock_unpoisoned(&inner.catalog);
    if let Some(entry) = catalog.databases.get_mut(database) {
        if matches!(entry.engine, ResidentEngine::Closing) {
            entry.engine = ResidentEngine::Open(engine);
        }
    }
    inner.catalog_changed.notify_all();
}

fn close_database_engine(
    inner: &Arc<MemberInner>,
    database: &DatabaseId,
    kind: CloseKind,
) -> Result<(), MemberError> {
    let mut catalog = lock_unpoisoned(&inner.catalog);
    let entry = catalog
        .databases
        .get(database)
        .ok_or_else(|| MemberError::UnknownDatabase(database.clone()))?;
    if entry.admissions.active() != 0 {
        return Err(MemberError::DatabaseBusy(database.clone()));
    }
    let Some(close) = take_close_work(&mut catalog, database, kind) else {
        return Ok(());
    };
    drop(catalog);
    perform_close(inner, close)
}

fn open_database_engine(
    inner: &Arc<MemberInner>,
    database: &DatabaseId,
    location: &Path,
) -> Result<Arc<Engine>, MemberError> {
    loop {
        let mut catalog = lock_unpoisoned(&inner.catalog);
        let scope = catalog
            .databases
            .get(database)
            .ok_or_else(|| MemberError::UnknownDatabase(database.clone()))?
            .scope
            .clone();
        let open_engine = catalog.databases.get(database).and_then(|entry| {
            if let ResidentEngine::Open(engine) = &entry.engine {
                Some(Arc::clone(engine))
            } else {
                None
            }
        });
        if let Some(engine) = open_engine {
            let stamp = catalog.next_access();
            let entry = catalog.databases.get_mut(database).expect("database remains catalogued");
            entry.last_used = Instant::now();
            entry.access_stamp = stamp;
            return Ok(engine);
        }
        if catalog.databases.get(database).is_some_and(|entry| {
            matches!(entry.engine, ResidentEngine::Opening | ResidentEngine::Closing)
        }) {
            drop(wait_catalog(inner, catalog));
            continue;
        }
        let can_open = catalog.open_databases < inner.max_open_databases
            && catalog.resident_rss_bytes.saturating_add(inner.database_rss_bytes)
                <= inner.rss_budget_bytes
            && catalog.opening_databases < inner.max_concurrent_opens;
        if !can_open {
            if let Some(close) = take_lru_eviction(&mut catalog) {
                drop(catalog);
                perform_close(inner, close)?;
                continue;
            }
            if catalog.opening_databases > 0 {
                drop(wait_catalog(inner, catalog));
                continue;
            }
            return Err(MemberError::OpenDatabaseBudgetExhausted {
                rss_budget_bytes: inner.rss_budget_bytes,
            });
        }
        if let Some(entry) = catalog.databases.get_mut(database) {
            entry.engine = ResidentEngine::Opening;
        }
        catalog.open_databases += 1;
        catalog.opening_databases += 1;
        catalog.resident_rss_bytes =
            catalog.resident_rss_bytes.saturating_add(inner.database_rss_bytes);
        let stamp = catalog.next_access();
        let scope = scope;
        drop(catalog);

        let result = revalidate_created_location(&inner.root, location)
            .map_err(MemberError::Validation)
            .and_then(|()| {
                let config = EngineConfig::durable(location).map_err(|error| {
                    MemberError::EngineConfig { database: database.clone(), error }
                })?;
                let config = scope
                    .as_ref()
                    .map_or(config.clone(), |scope| config.with_owner(scope.owner().clone()));
                let engine = Engine::open(config).map(Arc::new).map_err(|error| {
                    MemberError::EngineOpen { database: database.clone(), error }
                })?;
                if let Err(validation) = revalidate_created_location(&inner.root, location) {
                    let shutdown =
                        Arc::try_unwrap(engine).ok().and_then(|engine| engine.shutdown().err());
                    return Err(shutdown.map_or(MemberError::Validation(validation), |error| {
                        MemberError::Shutdown { database: database.clone(), error }
                    }));
                }
                Ok(engine)
            });
        let mut catalog = lock_unpoisoned(&inner.catalog);
        catalog.opening_databases = catalog.opening_databases.saturating_sub(1);
        match result {
            Ok(engine) => {
                if let Some(entry) = catalog.databases.get_mut(database) {
                    entry.engine = ResidentEngine::Open(Arc::clone(&engine));
                    entry.last_used = Instant::now();
                    entry.access_stamp = stamp;
                }
                catalog.metrics.opens = catalog.metrics.opens.saturating_add(1);
                inner.catalog_changed.notify_all();
                return Ok(engine);
            }
            Err(error) => {
                if let Some(entry) = catalog.databases.get_mut(database) {
                    entry.engine = ResidentEngine::Closed;
                }
                catalog.open_databases = catalog.open_databases.saturating_sub(1);
                catalog.resident_rss_bytes =
                    catalog.resident_rss_bytes.saturating_sub(inner.database_rss_bytes);
                inner.catalog_changed.notify_all();
                return Err(error);
            }
        }
    }
}

fn wait_catalog<'a>(
    inner: &MemberInner,
    catalog: std::sync::MutexGuard<'a, CatalogState>,
) -> std::sync::MutexGuard<'a, CatalogState> {
    inner.catalog_changed.wait(catalog).unwrap_or_else(std::sync::PoisonError::into_inner)
}

struct AdmissionCounter {
    limit: usize,
    state: Mutex<usize>,
    changed: Condvar,
}

impl AdmissionCounter {
    fn new(limit: usize) -> Self {
        Self { limit, state: Mutex::new(0), changed: Condvar::new() }
    }

    fn active(&self) -> usize {
        *lock_unpoisoned(&self.state)
    }

    fn try_acquire(
        self: &Arc<Self>,
        scope: AdmissionScope,
    ) -> Result<AdmissionPermit, MemberError> {
        let mut active = lock_unpoisoned(&self.state);
        if *active >= self.limit {
            return Err(MemberError::Overloaded(scope));
        }
        *active += 1;
        Ok(AdmissionPermit { counter: Arc::clone(self) })
    }

    fn wait_for_zero(&self, timeout: Duration) -> bool {
        let deadline = Instant::now().checked_add(timeout);
        let mut active = lock_unpoisoned(&self.state);
        while *active != 0 {
            let Some(deadline) = deadline else { return false };
            let Some(remaining) = deadline.checked_duration_since(Instant::now()) else {
                return false;
            };
            let result = self.changed.wait_timeout(active, remaining);
            let (next, timed_out) = match result {
                Ok((guard, result)) => (guard, result.timed_out()),
                Err(poisoned) => {
                    let (guard, result) = poisoned.into_inner();
                    (guard, result.timed_out())
                }
            };
            active = next;
            if timed_out && *active != 0 {
                return false;
            }
        }
        true
    }
}

struct AdmissionPermit {
    counter: Arc<AdmissionCounter>,
}

impl Drop for AdmissionPermit {
    fn drop(&mut self) {
        let mut active = lock_unpoisoned(&self.counter.state);
        *active = active.saturating_sub(1);
        self.counter.changed.notify_all();
    }
}

fn lock_unpoisoned<T>(mutex: &Mutex<T>) -> std::sync::MutexGuard<'_, T> {
    match mutex.lock() {
        Ok(guard) => guard,
        Err(poisoned) => poisoned.into_inner(),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::atomic::{AtomicUsize, Ordering};

    fn root(label: &str) -> PathBuf {
        let path = std::env::temp_dir()
            .join(format!("terrabase-member-owner-{label}-{}", std::process::id()));
        let _ = fs::remove_dir_all(&path);
        fs::create_dir_all(&path).expect("root");
        path
    }

    #[test]
    fn production_scope_is_bound_and_survives_member_restart() {
        let root = root("restart");
        let scope = DatabaseScope::new(
            "production",
            TenantUlid::generate(),
            ProjectUlid::generate(),
            DatabaseUlid::generate(),
            RegionCode::GRU,
        )
        .expect("scope");
        let spec = DatabaseSpec::scoped(scope.clone(), "db");
        let id = DatabaseId::from(scope.database());
        let member = Member::open_production(&root, [spec.clone()], AdmissionLimits::new(2, 1))
            .expect("production member");
        let lease = member.acquire(&id).expect("lease");
        assert_eq!(lease.database_owner().database(), scope.database());
        drop(lease);
        member.close(Duration::from_secs(1)).expect("close");
        let reopened =
            Member::open_production(&root, [spec], AdmissionLimits::new(2, 1)).expect("restart");
        let lease = reopened.acquire(&id).expect("lease");
        assert_eq!(lease.database_owner(), scope.owner());
        drop(lease);
        reopened.close(Duration::from_secs(1)).expect("close");
        let _ = fs::remove_dir_all(root);
    }

    #[test]
    fn idle_close_evicts_and_next_acquire_reopens() {
        let root = root("idle");
        let scope = DatabaseScope::new(
            "production",
            TenantUlid::generate(),
            ProjectUlid::generate(),
            DatabaseUlid::generate(),
            RegionCode::GRU,
        )
        .expect("scope");
        let spec = DatabaseSpec::scoped(scope.clone(), "db");
        let id = DatabaseId::from(scope.database());
        let member = Member::open_production(&root, [spec], AdmissionLimits::new(4, 2))
            .expect("production member");
        // A just-used database is never idle.
        drop(member.acquire(&id).expect("initial open"));
        assert_eq!(member.close_idle(Duration::from_mins(1)), 0);
        // Past the window with no requests, the engine closes flush...
        std::thread::sleep(Duration::from_millis(5));
        assert_eq!(member.close_idle(Duration::from_millis(1)), 1);
        // ...an already-closed database is not reported again...
        assert_eq!(member.close_idle(Duration::from_millis(1)), 0);
        // ...and the next request transparently reopens the same database.
        let lease = member.acquire(&id).expect("reopen");
        assert_eq!(lease.database_owner().database(), scope.database());
        drop(lease);
        member.close(Duration::from_secs(5)).expect("close");
        let _ = fs::remove_dir_all(root);
    }

    #[test]
    fn idle_close_keeps_databases_with_outstanding_leases() {
        let root = root("idle-lease");
        let scope = DatabaseScope::new(
            "production",
            TenantUlid::generate(),
            ProjectUlid::generate(),
            DatabaseUlid::generate(),
            RegionCode::GRU,
        )
        .expect("scope");
        let spec = DatabaseSpec::scoped(scope.clone(), "db");
        let id = DatabaseId::from(scope.database());
        let member = Member::open_production(&root, [spec], AdmissionLimits::new(4, 2))
            .expect("production member");
        let lease = member.acquire(&id).expect("lease");
        std::thread::sleep(Duration::from_millis(5));
        // The outstanding lease pins the engine: nothing is evicted and the
        // lease keeps serving.
        assert_eq!(member.close_idle(Duration::from_millis(1)), 0);
        assert_eq!(lease.database_owner().database(), scope.database());
        drop(lease);
        member.close(Duration::from_secs(5)).expect("close");
        let _ = fs::remove_dir_all(root);
    }

    #[test]
    fn production_open_refuses_compatibility_specification() {
        let root = root("unscoped");
        assert!(matches!(
            Member::open_production(
                &root,
                [DatabaseSpec::new("local", "db")],
                AdmissionLimits::default()
            ),
            Err(MemberOpenError::Validation(CatalogValidationError::UnscopedDatabase))
        ));
        let _ = fs::remove_dir_all(root);
    }

    #[test]
    fn rss_budget_evicts_idle_lru_and_never_exceeds_bytes() {
        let root = root("rss-lru");
        let limits = AdmissionLimits::new(4, 2)
            .with_rss_budget_bytes(2)
            .with_database_rss_bytes(1)
            .with_max_open_databases(4)
            .with_max_concurrent_opens(2);
        let member = Member::open(
            &root,
            [
                DatabaseSpec::new("one", "one"),
                DatabaseSpec::new("two", "two"),
                DatabaseSpec::new("three", "three"),
            ],
            limits,
        )
        .expect("member");
        for id in ["one", "two", "three"] {
            let id = DatabaseId::try_new(id).expect("id");
            let lease = member.acquire(&id).expect("acquire");
            drop(lease);
            assert!(member.status().resident_rss_bytes <= 2);
        }
        assert_eq!(member.open_database_count(), 2);
        assert_eq!(member.status().database_evictions, 1);
        member.close(Duration::from_secs(5)).expect("close");
        let _ = fs::remove_dir_all(root);
    }

    #[test]
    fn fake_clock_idle_close_pauses_backup_and_active_lease_pins() {
        let root = root("fake-clock");
        let member = Member::open(
            &root,
            [DatabaseSpec::new("db", "db")],
            AdmissionLimits::new(4, 2)
                .with_rss_budget_bytes(DEFAULT_DATABASE_RSS_BYTES)
                .with_database_rss_bytes(DEFAULT_DATABASE_RSS_BYTES),
        )
        .expect("member");
        let pauses = Arc::new(AtomicUsize::new(0));
        let observed = Arc::clone(&pauses);
        member.set_backup_pause_callback(Some(Arc::new(move |_| {
            observed.fetch_add(1, Ordering::SeqCst);
        })));
        let id = DatabaseId::try_new("db").expect("id");
        let lease = member.acquire(&id).expect("acquire");
        let fake_now = Instant::now() + Duration::from_secs(1);
        assert_eq!(member.close_idle_at(Duration::ZERO, fake_now), 0);
        drop(lease);
        assert_eq!(member.close_idle_at(Duration::ZERO, fake_now), 1);
        assert_eq!(pauses.load(Ordering::SeqCst), 1);
        let reopened = member.acquire(&id).expect("reopen");
        assert_eq!(reopened.location(), root.join("db").as_path());
        drop(reopened);
        member.close(Duration::from_secs(5)).expect("close");
        let _ = fs::remove_dir_all(root);
    }
}
