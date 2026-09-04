//! The root-owned Member catalog and lifecycle boundary.
//!
//! A [`Member`] owns one durable `Engine` for each configured
//! database.  This module deliberately knows nothing about TBEP, membership,
//! or replication: it reports storage health only.  A higher layer must add
//! those authorities before advertising production readiness.

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
}

impl AdmissionLimits {
    /// Creates admission limits.  Zero and excessively large limits are
    /// rejected when the catalog is opened.
    #[must_use]
    pub const fn new(global: usize, per_database: usize) -> Self {
        Self { global, per_database }
    }
}

impl Default for AdmissionLimits {
    fn default() -> Self {
        Self { global: 256, per_database: 64 }
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
    /// Number of admitted operations currently holding permits.
    pub active_operations: usize,
    /// Always false until an upper layer supplies membership and replication.
    pub production_ready: bool,
}

struct DatabaseEntry {
    id: DatabaseId,
    location: PathBuf,
    scope: Option<DatabaseScope>,
    engine: Mutex<Option<Arc<Engine>>>,
    /// The last time an acquire handed out this database's engine. The
    /// idle-close clock (D-44): a database with no request for the
    /// configured window is evicted, and the next acquire reopens it.
    last_used: Mutex<Instant>,
    admissions: Arc<AdmissionCounter>,
}

struct MemberInner {
    root: PathBuf,
    databases: BTreeMap<DatabaseId, Arc<DatabaseEntry>>,
    global_admissions: Arc<AdmissionCounter>,
    lifecycle: Mutex<LifecycleState>,
}

/// One opened, isolated Member catalog.
#[derive(Clone)]
pub struct Member {
    inner: Arc<MemberInner>,
}

/// Compatibility name for the root-owned database catalog.
pub type MemberCatalog = Member;

impl Member {
    /// Validates the complete catalog, then opens every durable engine.
    ///
    /// Engines are opened into a temporary collection first.  If any open or
    /// final validation fails, already-opened engines are synchronously
    /// flushed and no partially populated Member is published.
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

        let mut opened = Vec::with_capacity(specs.len());
        for (id, location, scope) in specs {
            let config = EngineConfig::durable(&location)
                .map(|config| {
                    scope
                        .as_ref()
                        .map_or(config.clone(), |scope| config.with_owner(scope.owner().clone()))
                })
                .map_err(|error| MemberOpenError::EngineConfig { database: id.clone(), error });
            let config = match config {
                Ok(config) => config,
                Err(error) => {
                    close_opened(opened);
                    return Err(error);
                }
            };
            if let Err(error) = revalidate_created_location(&root, &location) {
                close_opened(opened);
                return Err(MemberOpenError::Validation(error));
            }
            let engine = match Engine::open(config) {
                Ok(engine) => Arc::new(engine),
                Err(error) => {
                    close_opened(opened);
                    return Err(MemberOpenError::EngineOpen { database: id, error });
                }
            };
            opened.push((id, location, scope, engine));
        }

        let databases = opened
            .into_iter()
            .map(|(id, location, scope, engine)| {
                let entry = Arc::new(DatabaseEntry {
                    id: id.clone(),
                    location,
                    scope,
                    engine: Mutex::new(Some(engine)),
                    last_used: Mutex::new(Instant::now()),
                    admissions: Arc::new(AdmissionCounter::new(limits.per_database)),
                });
                (id, entry)
            })
            .collect();
        Ok(Self {
            inner: Arc::new(MemberInner {
                root,
                databases,
                global_admissions: Arc::new(AdmissionCounter::new(limits.global)),
                lifecycle: Mutex::new(LifecycleState::Serving),
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
    pub fn database_ids(&self) -> impl Iterator<Item = &DatabaseId> {
        self.inner.databases.keys()
    }

    /// Returns storage-only status.  Production readiness remains false.
    #[must_use]
    pub fn status(&self) -> MemberStatus {
        let lifecycle = *lock_unpoisoned(&self.inner.lifecycle);
        let storage = match lifecycle {
            LifecycleState::Serving => StorageHealth::Healthy,
            LifecycleState::Draining => StorageHealth::Draining,
            LifecycleState::Stopped | LifecycleState::Starting => StorageHealth::Stopped,
        };
        MemberStatus {
            lifecycle,
            storage,
            databases: self.inner.databases.len(),
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
        let state = *lock_unpoisoned(&self.inner.lifecycle);
        if state != LifecycleState::Serving {
            return Err(MemberError::Draining);
        }
        let entry = self
            .inner
            .databases
            .get(database)
            .cloned()
            .ok_or_else(|| MemberError::UnknownDatabase(database.clone()))?;
        let global = self.inner.global_admissions.try_acquire(AdmissionScope::Global)?;
        let local = match entry.admissions.try_acquire(AdmissionScope::Database) {
            Ok(permit) => permit,
            Err(error) => {
                drop(global);
                return Err(error);
            }
        };
        let engine = {
            let mut guard = lock_unpoisoned(&entry.engine);
            if guard.is_none() {
                // Idle-close evicted this database; reopen it from its
                // validated location and scope (D-44 lazy reopen) while
                // holding the entry lock, so a concurrent eviction sweep
                // cannot take the engine mid-open.
                *guard = Some(reopen_database_engine(&entry)?);
            }
            let Some(engine) = guard.clone() else {
                return Err(MemberError::StorageUnavailable(database.clone()));
            };
            *lock_unpoisoned(&entry.last_used) = Instant::now();
            engine
        };
        Ok(DatabaseLease { entry, engine, _global: global, _local: local })
    }

    /// Closes every open database engine with no admitted request for at
    /// least `idle` (D-44 idle close), returning how many were evicted.
    ///
    /// Eviction flushes through [`Engine::shutdown`] (retention sweep plus
    /// WAL fold-back), never touches a database with an outstanding lease,
    /// and is transparent: the next [`Member::acquire`] reopens the
    /// database from its validated location and scope. A closed (already
    /// evicted) database is not reported again. While the Member is not
    /// serving, nothing is evicted.
    #[must_use]
    pub fn close_idle(&self, idle: Duration) -> usize {
        if *lock_unpoisoned(&self.inner.lifecycle) != LifecycleState::Serving {
            return 0;
        }
        let mut evicted = 0;
        for entry in self.inner.databases.values() {
            if lock_unpoisoned(&entry.last_used).elapsed() < idle {
                continue;
            }
            let mut guard = lock_unpoisoned(&entry.engine);
            let Some(engine) = guard.take() else { continue };
            // Re-check under the entry lock: an acquire may have reopened
            // and used the database after the first check.
            if lock_unpoisoned(&entry.last_used).elapsed() < idle {
                *guard = Some(engine);
                continue;
            }
            match Arc::try_unwrap(engine) {
                Ok(engine) => {
                    let _ = engine.shutdown();
                    evicted += 1;
                }
                Err(shared) => {
                    // A lease is outstanding; keep serving it.
                    *guard = Some(shared);
                }
            }
        }
        evicted
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
        let mut first_error = None;
        for entry in self.inner.databases.values() {
            let engine = lock_unpoisoned(&entry.engine).take();
            let Some(engine) = engine else { continue };
            match Arc::try_unwrap(engine) {
                Ok(engine) => {
                    if let Err(error) = engine.shutdown()
                        && first_error.is_none()
                    {
                        first_error =
                            Some(MemberError::Shutdown { database: entry.id.clone(), error });
                    }
                }
                Err(_) => {
                    if first_error.is_none() {
                        first_error = Some(MemberError::DrainTimeout);
                    }
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
    entry: Arc<DatabaseEntry>,
    engine: Arc<Engine>,
    _global: AdmissionPermit,
    _local: AdmissionPermit,
}

impl DatabaseLease {
    /// Returns the database identity for this lease.
    #[must_use]
    pub fn database_id(&self) -> &DatabaseId {
        &self.entry.id
    }

    /// Returns the validated durable location.  Routing layers should use the
    /// identity, not this path, when handling requests.
    #[must_use]
    pub fn location(&self) -> &Path {
        &self.entry.location
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

/// Reopens one database engine from its validated location and scope.
///
/// The idle-close path funnels through here so an evicted database returns
/// with the identical ownership binding [`Member::open`] established. Any
/// failure to rebuild the configuration or reopen the engine surfaces as
/// the same typed unavailability an evicted database reports.
fn reopen_database_engine(entry: &DatabaseEntry) -> Result<Arc<Engine>, MemberError> {
    let config = EngineConfig::durable(&entry.location)
        .map(|config| {
            entry
                .scope
                .as_ref()
                .map_or(config.clone(), |scope| config.with_owner(scope.owner().clone()))
        })
        .map_err(|_| MemberError::StorageUnavailable(entry.id.clone()))?;
    Engine::open(config)
        .map(Arc::new)
        .map_err(|_| MemberError::StorageUnavailable(entry.id.clone()))
}

fn close_opened(opened: Vec<(DatabaseId, PathBuf, Option<DatabaseScope>, Arc<Engine>)>) {
    for (_, _, _, engine) in opened {
        if let Ok(engine) = Arc::try_unwrap(engine) {
            let _ = engine.shutdown();
        }
    }
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
}
