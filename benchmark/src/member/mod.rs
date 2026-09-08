//! The root-owned Member catalog and lifecycle boundary.
//!
//! A [`Member`] owns one durable `Engine` for each configured
//! database.  Membership and replication remain injected authorities: the
//! catalog never implements either protocol, but can compose their health
//! opinions before advertising production readiness.

#![allow(clippy::all, reason = "member lifecycle is being integrated with production authorities")]
#![allow(
    clippy::pedantic,
    reason = "member lifecycle is being integrated with production authorities"
)]

use std::{
    collections::{BTreeMap, BTreeSet, VecDeque},
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
use crate::types::{OperationId, TenantId, TenantScope};
use crate::{Engine, EngineConfig, EngineOpenError, EngineShutdownError};

mod shard_registry;
mod shard_resources;
pub use shard_registry::{AdmittedShardParticipant, AdmittedShardReadCut};
mod split_runtime;
pub use split_runtime::{
    MemberSplitError, MemberSplitRuntime, MemberSplitStep, SplitAdmission, SplitAdmissionAuthority,
};

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
/// Absolute host clock offset at which new origin writes are refused (D-69).
pub const CLOCK_OFFSET_WRITE_REFUSAL_THRESHOLD_SECONDS: f64 = 0.5;

/// Health opinion supplied by a production authority.
///
/// Storage cannot infer membership or replication health.  These values are
/// deliberately small and transport-independent so the server and control
/// plane can adapt their own health registries without making the catalog
/// depend on TBEP or a particular cluster implementation.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum AuthorityHealth {
    /// No authoritative observation is available.
    Unknown,
    /// The authority has admitted this Member for serving.
    Healthy,
    /// The authority is serving but should not be used for readiness.
    Degraded,
    /// The authority has fenced this Member.
    Unhealthy,
}

impl AuthorityHealth {
    /// Returns whether this observation permits production readiness.
    #[must_use]
    pub const fn is_ready(self) -> bool {
        matches!(self, Self::Healthy)
    }
}

/// The membership authority owned by the cluster/control-plane layer.
pub trait MembershipAuthority: Send + Sync {
    /// Returns the latest authoritative membership opinion.
    fn health(&self) -> AuthorityHealth;
}

/// Descriptive alias used by integrations that name the health dimension.
pub use MembershipAuthority as MembershipHealthAuthority;

/// The replication authority owned by the replication/data-plane layer.
pub trait ReplicationHealthAuthority: Send + Sync {
    /// Returns the latest authoritative replication opinion.
    fn health(&self) -> AuthorityHealth;
}

/// Short alias for the replication health authority seam.
pub use ReplicationHealthAuthority as ReplicationAuthority;

/// A validated placement used by the pure routing decision helper.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct DatabasePlacement {
    regions: Vec<RegionCode>,
    residency: Option<String>,
}

impl DatabasePlacement {
    /// Creates a placement with one to twenty unique Regions.
    ///
    /// The placement table is supplied by the orchestrator.  This type does
    /// not accept a request's region text and never contains a filesystem
    /// path.
    pub fn try_new(
        regions: impl IntoIterator<Item = RegionCode>,
        residency: Option<String>,
    ) -> Result<Self, RoutingError> {
        let mut selected = Vec::new();
        for region in regions {
            if selected.contains(&region) || selected.len() >= crate::regions::REGION_CEILING {
                return Err(RoutingError::InvalidPlacement);
            }
            selected.push(region);
        }
        if selected.is_empty() {
            return Err(RoutingError::InvalidPlacement);
        }
        if residency.as_ref().is_some_and(|value| value.is_empty()) {
            return Err(RoutingError::InvalidPlacement);
        }
        Ok(Self { regions: selected, residency })
    }

    /// Returns Regions in orchestrator placement order.
    #[must_use]
    pub fn regions(&self) -> &[RegionCode] {
        &self.regions
    }

    /// Returns the optional residency constraint.
    #[must_use]
    pub fn residency(&self) -> Option<&str> {
        self.residency.as_deref()
    }
}

/// Operation class used by [`decide_routing`].
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum RoutingOperation {
    /// A read may execute on any hosted healthy replica.
    Read,
    /// A write must execute at the authoritative in-Region writer.
    Write,
}

/// A safe result of a placement lookup.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum RoutingDecision {
    /// The local member may serve this operation.
    ServeLocally,
    /// The request may be forwarded to this placement Region.
    Forward {
        /// Target placement Region hosting the authoritative writer.
        region: RegionCode,
    },
    /// The database exists, but this member's Region does not host it (421).
    Misdirected {
        /// Authoritative placement returned so the caller can redirect.
        placement: DatabasePlacement,
    },
    /// The database is not in the authoritative placement table (404).
    UnknownDatabase,
    /// A hosted write has no authoritative writer and must not be forwarded.
    WriterUnavailable,
}

/// Routing validation failures.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum RoutingError {
    /// A placement was empty, duplicated a Region, exceeded the architecture
    /// ceiling, or carried an empty residency value.
    InvalidPlacement,
}

/// Computes a forwarding or `421 Misdirected Request` decision without
/// opening a database file.  A non-hosting member always returns `421`; it
/// never forwards blindly.  Writes are forwarded only to a writer that is
/// present in the authoritative placement.
#[must_use]
pub fn decide_routing(
    local_region: RegionCode,
    hosted_here: bool,
    writer_region: Option<RegionCode>,
    placement: Option<&DatabasePlacement>,
    operation: RoutingOperation,
) -> RoutingDecision {
    let Some(placement) = placement else {
        return RoutingDecision::UnknownDatabase;
    };
    if !placement.regions.contains(&local_region) || !hosted_here {
        return RoutingDecision::Misdirected { placement: placement.clone() };
    }
    match operation {
        RoutingOperation::Read => RoutingDecision::ServeLocally,
        RoutingOperation::Write => match writer_region {
            Some(region) if placement.regions.contains(&region) && region == local_region => {
                RoutingDecision::ServeLocally
            }
            Some(region) if placement.regions.contains(&region) => {
                RoutingDecision::Forward { region }
            }
            _ => RoutingDecision::WriterUnavailable,
        },
    }
}

/// A bounded, generation-stamped cache for API-key → principal admissions.
///
/// The cache stores only a caller-provided digest, never credential bytes.
/// Callers stamp entries with their durable revocation generation; a stale
/// generation is a miss.  Shards have deterministic LRU order and their
/// aggregate capacity never exceeds the configured bound.
pub struct PrincipalCache<P> {
    shards: Vec<Mutex<PrincipalCacheShard<P>>>,
}

struct PrincipalCacheShard<P> {
    entries: BTreeMap<[u8; 32], (u64, P)>,
    order: VecDeque<[u8; 32]>,
    capacity: usize,
}

impl<P> PrincipalCache<P> {
    /// Hashes credential material for cache lookup without retaining it.
    /// This is the same digest width used by the server key store; callers
    /// should still perform credential shape validation before this helper.
    #[must_use]
    pub fn digest(credential: &str) -> [u8; 32] {
        *blake3::hash(credential.as_bytes()).as_bytes()
    }

    /// Creates a cache with a finite, exact aggregate capacity.
    pub fn try_new(capacity: usize) -> Result<Self, CacheError> {
        if capacity == 0 {
            return Err(CacheError::ZeroCapacity);
        }
        let shard_count = capacity.min(16);
        let base = capacity / shard_count;
        let remainder = capacity % shard_count;
        let shards = (0..shard_count)
            .map(|index| {
                Mutex::new(PrincipalCacheShard {
                    entries: BTreeMap::new(),
                    order: VecDeque::new(),
                    capacity: base + usize::from(index < remainder),
                })
            })
            .collect();
        Ok(Self { shards })
    }

    /// Creates a cache, panicking only for the programmer error of a zero
    /// capacity. Production configuration should use [`Self::try_new`].
    #[must_use]
    pub fn new(capacity: usize) -> Self {
        Self::try_new(capacity).expect("principal cache capacity must be non-zero")
    }

    fn shard(&self, digest: &[u8; 32]) -> &Mutex<PrincipalCacheShard<P>> {
        &self.shards[usize::from(digest[0]) % self.shards.len()]
    }

    /// Looks up a principal at the required revocation generation.
    pub fn get(&self, digest: &[u8; 32], generation: u64) -> Option<P>
    where
        P: Clone,
    {
        let mut shard = lock_unpoisoned(self.shard(digest));
        let Some((seen_generation, principal)) = shard.entries.get(digest) else {
            return None;
        };
        if *seen_generation != generation {
            shard.remove(digest);
            return None;
        }
        let principal = principal.clone();
        shard.touch(digest);
        Some(principal)
    }

    /// Inserts or replaces a principal at a revocation generation.
    pub fn insert(&self, digest: [u8; 32], generation: u64, principal: P) {
        let mut shard = lock_unpoisoned(self.shard(&digest));
        shard.entries.insert(digest, (generation, principal));
        shard.touch(&digest);
        while shard.entries.len() > shard.capacity {
            let Some(oldest) = shard.order.pop_front() else { break };
            shard.entries.remove(&oldest);
        }
    }

    /// Removes one digest after a registration or revocation update.
    pub fn invalidate(&self, digest: &[u8; 32]) {
        lock_unpoisoned(self.shard(digest)).remove(digest);
    }

    /// Removes every cached principal.
    pub fn clear(&self) {
        for shard in &self.shards {
            let mut shard = lock_unpoisoned(shard);
            shard.entries.clear();
            shard.order.clear();
        }
    }

    /// Returns the number of cached principals.
    #[must_use]
    pub fn len(&self) -> usize {
        self.shards.iter().map(|shard| lock_unpoisoned(shard).entries.len()).sum()
    }

    /// Returns whether the cache has no entries.
    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.len() == 0
    }
}

impl<P> PrincipalCacheShard<P> {
    fn touch(&mut self, digest: &[u8; 32]) {
        self.order.retain(|queued| queued != digest);
        self.order.push_back(*digest);
    }

    fn remove(&mut self, digest: &[u8; 32]) {
        self.entries.remove(digest);
        self.order.retain(|queued| queued != digest);
    }
}

/// Cache construction failures.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum CacheError {
    /// A zero-sized cache would silently disable authentication caching.
    ZeroCapacity,
}

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
    /// A required production key lease is unavailable or belongs to another scope.
    EncryptionUnavailable,
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
    /// Production placement is pending or assigned to physical replication.
    DatabaseNotWriter(DatabaseId),
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
    /// Durable shard serving state cannot be admitted safely.
    ShardServingUnavailable(DatabaseId),
    /// The bounded drain deadline elapsed while work remained active.
    DrainTimeout,
    /// The host clock is unknown or outside the safe write bound.
    ClockOffsetUnsafe,
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

/// A storage and authority status snapshot. `production_ready` is true only
/// when storage, clock safety, membership, and replication all agree.
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
    /// True only when all required authorities are attached and healthy.
    pub production_ready: bool,
    /// Latest membership authority opinion, if attached.
    pub membership_health: Option<AuthorityHealth>,
    /// Latest replication authority opinion, if attached.
    pub replication_health: Option<AuthorityHealth>,
    /// Whether new origin writes are currently permitted.
    pub writes_allowed: bool,
}

/// A role accepted by the authenticated membership/control-plane authority.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum DatabaseRole {
    /// Owns SQLite and may originate writes.
    Writer,
    /// Physical replication owns the database files; SQLite must remain closed.
    Follower,
}

/// Accepted placement evidence supplied by the production authority.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct AcceptedDatabaseRole {
    /// Monotonically increasing control-plane assignment epoch.
    pub epoch: u64,
    /// The accepted local storage role.
    pub role: DatabaseRole,
}

/// Adapter over verified control-plane acceptance, never raw configuration.
/// Implementations must return writer promotion only after physical workers drain.
pub trait DatabaseRoleAuthority: Send + Sync {
    /// Returns verified, currently accepted assignment evidence for this database.
    fn accepted_role(&self, database: &DatabaseId) -> Option<AcceptedDatabaseRole>;
}

struct DatabaseEntry {
    production: bool,
    accepted_role: Option<AcceptedDatabaseRole>,
    storage_keys: Option<Arc<dyn crate::encryption::DatabaseKeySource>>,
    location: PathBuf,
    scope: Option<DatabaseScope>,
    engine: ResidentEngine,
    /// The last request time. LRU ordering uses the separate monotonic stamp
    /// so ties are deterministic even when the platform clock is coarse.
    last_used: Instant,
    access_stamp: u64,
    admissions: Arc<AdmissionCounter>,
    /// Whether the backup stream was paused by a successful close.
    backup_paused: bool,
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
    backup_hooks: Mutex<Option<BackupHooks>>,
    membership_authority: Mutex<Option<Arc<dyn MembershipAuthority>>>,
    replication_authority: Mutex<Option<Arc<dyn ReplicationHealthAuthority>>>,
    clock_offset_seconds: Mutex<Option<f64>>,
    restore_paths: Arc<Mutex<BTreeSet<PathBuf>>>,
}

/// Callback invoked immediately before a resident database is closed. The
/// backup owner should pause its per-file stream before dropping the SQLite
/// file, then resume it after a later acquire reopens the database.
pub type BackupPauseCallback = Arc<dyn Fn(&DatabaseId) + Send + Sync + 'static>;

/// Callback invoked after a paused database is opened again.
pub type BackupResumeCallback = Arc<dyn Fn(&DatabaseId) + Send + Sync + 'static>;

/// Explicit lifecycle hooks for a per-file backup owner.
#[derive(Clone)]
pub struct BackupHooks {
    pause: BackupPauseCallback,
    resume: BackupResumeCallback,
}

impl BackupHooks {
    /// Creates hooks called outside every catalog lock.
    #[must_use]
    pub fn new(
        pause: impl Fn(&DatabaseId) + Send + Sync + 'static,
        resume: impl Fn(&DatabaseId) + Send + Sync + 'static,
    ) -> Self {
        Self { pause: Arc::new(pause), resume: Arc::new(resume) }
    }
}

/// One opened, isolated Member catalog.
#[derive(Clone)]
pub struct Member {
    inner: Arc<MemberInner>,
}

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
        Self::open_with_keys(root, specifications, limits, BTreeMap::new(), false)
    }

    fn open_with_keys(
        root: impl AsRef<Path>,
        specifications: impl IntoIterator<Item = DatabaseSpec>,
        limits: AdmissionLimits,
        keys: BTreeMap<DatabaseId, Arc<dyn crate::encryption::DatabaseKeySource>>,
        production: bool,
    ) -> Result<Self, MemberOpenError> {
        let root = validate_root(root.as_ref()).map_err(MemberOpenError::Validation)?;
        validate_limits(limits).map_err(MemberOpenError::Validation)?;
        let specs = collect_and_validate_specs(&root, specifications)
            .map_err(MemberOpenError::Validation)?;

        for (id, location, scope) in specs.iter().filter(|_| !production) {
            let config = EngineConfig::durable(location.clone())
                .map(|config| {
                    scope.as_ref().map_or(config.clone(), |scope| {
                        config.with_owner(scope.owner().clone()).with_replication_origin(
                            crate::types::RegionId::try_new(scope.owner().region().as_str())
                                .expect("registered Region"),
                        )
                    })
                })
                .map_err(|error| MemberOpenError::EngineConfig { database: id.clone(), error });
            let config = match config {
                Ok(config) => config,
                Err(error) => return Err(error),
            };
            let config = keys
                .get(id)
                .map_or(config.clone(), |keys| config.with_storage_keys(Arc::clone(keys)));
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
                    production,
                    accepted_role: None,
                    storage_keys: keys.get(&id).cloned(),
                    location,
                    scope,
                    engine: ResidentEngine::Closed,
                    last_used: Instant::now(),
                    access_stamp: 0,
                    admissions: Arc::new(AdmissionCounter::new(limits.per_database)),
                    backup_paused: false,
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
                backup_hooks: Mutex::new(None),
                membership_authority: Mutex::new(None),
                replication_authority: Mutex::new(None),
                clock_offset_seconds: Mutex::new(Some(0.0)),
                restore_paths: Arc::new(Mutex::new(BTreeSet::new())),
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
        keys: BTreeMap<DatabaseId, Arc<dyn crate::encryption::DatabaseKeySource>>,
    ) -> Result<Self, MemberOpenError> {
        let specifications: Vec<_> = specifications.into_iter().collect();
        if specifications.iter().any(|spec| spec.scope.is_none()) {
            return Err(MemberOpenError::Validation(CatalogValidationError::UnscopedDatabase));
        }
        for specification in &specifications {
            let scope = specification.scope.as_ref().expect("scope checked");
            let source = keys.get(&DatabaseId::from(scope.database())).ok_or(
                MemberOpenError::Validation(CatalogValidationError::EncryptionUnavailable),
            )?;
            let lease = source.current_lease().map_err(|_| {
                MemberOpenError::Validation(CatalogValidationError::EncryptionUnavailable)
            })?;
            let metadata = lease.metadata().scope();
            if metadata.environment() != scope.environment().as_str()
                || metadata.tenant() != scope.tenant()
                || metadata.project() != Some(scope.project())
                || metadata.database() != scope.database()
                || metadata.purpose() != crate::encryption::KeyPurpose::Data
                || lease.provider() != crate::encryption::KeyProviderIdentity::OciVault
                || lease.is_expired()
            {
                return Err(MemberOpenError::Validation(
                    CatalogValidationError::EncryptionUnavailable,
                ));
            }
        }
        Self::open_with_keys(root, specifications, limits, keys, true)
    }

    /// Adds a scoped storage entry without granting placement or write authority.
    /// Identical retries succeed; identity or location conflicts are refused.
    ///
    /// # Errors
    /// Returns a validation refusal for unsafe, conflicting, or unscoped input.
    pub fn add_production_database(
        &self,
        specification: DatabaseSpec,
        per_database: usize,
        keys: Arc<dyn crate::encryption::DatabaseKeySource>,
    ) -> Result<(), CatalogValidationError> {
        if specification.scope.is_none() {
            return Err(CatalogValidationError::UnscopedDatabase);
        }
        if per_database == 0 || per_database > MAX_ADMISSION_PERMITS {
            return Err(CatalogValidationError::InvalidAdmissionLimits);
        }
        let owner = specification.scope.as_ref().expect("scope checked").owner();
        let lease =
            keys.current_lease().map_err(|_| CatalogValidationError::EncryptionUnavailable)?;
        let key_scope = lease.metadata().scope();
        if key_scope.environment() != owner.environment().as_str()
            || key_scope.tenant() != owner.tenant()
            || key_scope.project() != Some(owner.project())
            || key_scope.database() != owner.database()
            || key_scope.purpose() != crate::encryption::KeyPurpose::Data
            || lease.provider() != crate::encryption::KeyProviderIdentity::OciVault
            || lease.is_expired()
        {
            return Err(CatalogValidationError::EncryptionUnavailable);
        }
        let mut validated = collect_and_validate_specs(&self.inner.root, [specification])?;
        let (id, location, scope) = validated.pop().expect("one validated specification");
        let mut catalog = lock_unpoisoned(&self.inner.catalog);
        if *lock_unpoisoned(&self.inner.lifecycle) != LifecycleState::Serving {
            return Err(CatalogValidationError::InvalidRoot);
        }
        if let Some(existing) = catalog.databases.get(&id) {
            return if existing.location == location && existing.scope == scope {
                Ok(())
            } else {
                Err(CatalogValidationError::DuplicateDatabaseIdentity)
            };
        }
        if catalog.databases.len() >= MAX_DATABASES {
            return Err(CatalogValidationError::CatalogTooLarge);
        }
        if catalog.databases.values().any(|entry| entry.location == location) {
            return Err(CatalogValidationError::DuplicateDatabaseLocation);
        }
        catalog.databases.insert(
            id,
            DatabaseEntry {
                production: true,
                accepted_role: None,
                storage_keys: Some(keys),
                location,
                scope,
                engine: ResidentEngine::Closed,
                last_used: Instant::now(),
                access_stamp: 0,
                admissions: Arc::new(AdmissionCounter::new(per_database)),
                backup_paused: false,
            },
        );
        Ok(())
    }

    /// Installs verified role evidence while all SQLite leases and physical
    /// replication workers are drained. Epochs survive process restarts; a
    /// restarted catalog remains pending until acceptance is verified again.
    ///
    /// # Errors
    /// Refuses stale/conflicting evidence, busy engines, and persistence failures.
    pub fn accept_database_role(
        &self,
        database: &DatabaseId,
        authority: &dyn DatabaseRoleAuthority,
    ) -> Result<(), MemberError> {
        let accepted = authority
            .accepted_role(database)
            .filter(|accepted| accepted.epoch > 0)
            .ok_or_else(|| MemberError::DatabaseBusy(database.clone()))?;
        let mut catalog = lock_unpoisoned(&self.inner.catalog);
        let entry = catalog
            .databases
            .get_mut(database)
            .ok_or_else(|| MemberError::UnknownDatabase(database.clone()))?;
        if entry.accepted_role == Some(accepted) {
            return Ok(());
        }
        if entry.admissions.active() != 0 || !matches!(entry.engine, ResidentEngine::Closed) {
            return Err(MemberError::DatabaseBusy(database.clone()));
        }
        let path = self.inner.root.join(format!(".role-{}", database.as_str()));
        let role = match accepted.role {
            DatabaseRole::Writer => "writer",
            DatabaseRole::Follower => "follower",
        };
        let contents = format!("{} {}\n", accepted.epoch, role);
        match fs::read_to_string(&path) {
            Ok(previous) => {
                let mut fields = previous.split_whitespace();
                let epoch = fields
                    .next()
                    .and_then(|value| value.parse::<u64>().ok())
                    .ok_or_else(|| MemberError::DatabaseBusy(database.clone()))?;
                let previous_role = fields.next();
                if fields.next().is_some()
                    || !matches!(previous_role, Some("writer" | "follower"))
                    || accepted.epoch < epoch
                    || (accepted.epoch == epoch && previous != contents)
                {
                    return Err(MemberError::DatabaseBusy(database.clone()));
                }
            }
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => {}
            Err(_) => return Err(MemberError::DatabaseBusy(database.clone())),
        }
        let persist = || -> std::io::Result<()> {
            use std::io::Write;
            let temporary = path.with_extension("pending");
            let mut file = fs::File::create(&temporary)?;
            file.write_all(contents.as_bytes())?;
            file.sync_all()?;
            fs::rename(&temporary, &path)?;
            fs::File::open(&self.inner.root)?.sync_all()
        };
        persist().map_err(|_| MemberError::DatabaseBusy(database.clone()))?;
        entry.accepted_role = Some(accepted);
        Ok(())
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

    /// Return the admitted key source so runtime assignments reopen with the
    /// same provider identity and lease cache as member acquisition.
    pub fn storage_key_source(
        &self,
        database: &DatabaseId,
    ) -> Option<Arc<dyn crate::encryption::DatabaseKeySource>> {
        lock_unpoisoned(&self.inner.catalog)
            .databases
            .get(database)
            .and_then(|entry| entry.storage_keys.clone())
    }

    /// Returns storage-only status.  Production readiness remains false.
    #[must_use]
    pub fn status(&self) -> MemberStatus {
        let lifecycle = *lock_unpoisoned(&self.inner.lifecycle);
        let (databases, open_databases, resident_rss_bytes, metrics, active_operations) = {
            let catalog = lock_unpoisoned(&self.inner.catalog);
            (
                catalog.databases.len(),
                catalog.open_databases,
                catalog.resident_rss_bytes,
                (catalog.metrics.opens, catalog.metrics.evictions, catalog.metrics.closes),
                self.inner.global_admissions.active(),
            )
        };
        let membership_health = lock_unpoisoned(&self.inner.membership_authority)
            .as_ref()
            .map(|authority| authority.health());
        let replication_health = lock_unpoisoned(&self.inner.replication_authority)
            .as_ref()
            .map(|authority| authority.health());
        let clock_offset_seconds = *lock_unpoisoned(&self.inner.clock_offset_seconds);
        let writes_allowed = clock_offset_seconds.is_some_and(|offset| {
            offset.is_finite() && offset.abs() <= CLOCK_OFFSET_WRITE_REFUSAL_THRESHOLD_SECONDS
        });
        let storage = match lifecycle {
            LifecycleState::Serving => StorageHealth::Healthy,
            LifecycleState::Draining => StorageHealth::Draining,
            LifecycleState::Stopped | LifecycleState::Starting => StorageHealth::Stopped,
        };
        MemberStatus {
            lifecycle,
            storage,
            databases,
            open_databases,
            resident_rss_bytes,
            rss_budget_bytes: self.inner.rss_budget_bytes,
            sqlite_connection_budget: self.sqlite_connection_budget(),
            database_opens: metrics.0,
            database_evictions: metrics.1,
            database_closes: metrics.2,
            active_operations,
            production_ready: lifecycle == LifecycleState::Serving
                && storage == StorageHealth::Healthy
                && writes_allowed
                && membership_health.is_some_and(AuthorityHealth::is_ready)
                && replication_health.is_some_and(AuthorityHealth::is_ready),
            membership_health,
            replication_health,
            writes_allowed,
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
        self.acquire_with_policy(database, true, None)
    }

    /// Acquires a lease only from an already materialized durable location.
    /// This path never creates a directory, ownership record, or SQLite main
    /// database file. SQLite may still create sidecar files for an existing
    /// database while opening it.
    pub fn acquire_existing(&self, database: &DatabaseId) -> Result<DatabaseLease, MemberError> {
        self.acquire_with_policy(database, false, None)
    }

    /// Reacquires a database while its owner still holds an exclusive restore
    /// reservation. The reservation remains held until publication completes.
    pub fn acquire_existing_for_restore(
        &self,
        database: &DatabaseId,
        permit: &RestorePermit,
    ) -> Result<DatabaseLease, MemberError> {
        if permit.database != *database {
            return Err(MemberError::DatabaseBusy(database.clone()));
        }
        self.acquire_with_policy(database, false, Some(permit))
    }

    fn acquire_with_policy(
        &self,
        database: &DatabaseId,
        allow_create: bool,
        restore: Option<&RestorePermit>,
    ) -> Result<DatabaseLease, MemberError> {
        if *lock_unpoisoned(&self.inner.lifecycle) != LifecycleState::Serving {
            return Err(MemberError::Draining);
        }
        let global = self.inner.global_admissions.try_acquire(AdmissionScope::Global)?;
        let (local, location, writer_epoch) = {
            let catalog = lock_unpoisoned(&self.inner.catalog);
            let entry = catalog
                .databases
                .get(database)
                .ok_or_else(|| MemberError::UnknownDatabase(database.clone()))?;
            let restore_paths = lock_unpoisoned(&self.inner.restore_paths);
            let restoring = restore_paths.contains(&entry.location);
            if restoring
                && !restore.is_some_and(|permit| {
                    permit.database == *database
                        && permit.location == entry.location
                        && Arc::ptr_eq(&permit.path_guard, &self.inner.restore_paths)
                })
            {
                return Err(MemberError::DatabaseBusy(database.clone()));
            }
            if entry.production
                && !entry
                    .accepted_role
                    .is_some_and(|accepted| accepted.role == DatabaseRole::Writer)
            {
                return Err(MemberError::DatabaseNotWriter(database.clone()));
            }
            let local = match entry.admissions.try_acquire(AdmissionScope::Database) {
                Ok(permit) => permit,
                Err(error) => {
                    drop(global);
                    return Err(error);
                }
            };
            // `drain` may have started after the first lifecycle check but
            // before permits were taken. Do not let that race admit new work.
            if *lock_unpoisoned(&self.inner.lifecycle) != LifecycleState::Serving {
                drop(local);
                drop(global);
                return Err(MemberError::Draining);
            }
            let writer_epoch = entry
                .accepted_role
                .filter(|accepted| accepted.role == DatabaseRole::Writer)
                .map(|accepted| accepted.epoch);
            (local, entry.location.clone(), writer_epoch)
        };
        let engine = match open_database_engine(&self.inner, database, &location, allow_create) {
            Ok(engine) => engine,
            Err(error) => {
                drop(local);
                drop(global);
                return Err(error);
            }
        };
        if let Some(epoch) = writer_epoch {
            if engine.activate_origin_epoch(epoch).is_err() {
                drop(local);
                drop(global);
                return Err(MemberError::EngineOpen {
                    database: database.clone(),
                    error: EngineOpenError::StorageUnavailable,
                });
            }
        }
        let tenant = match TenantId::try_new(&engine.database_owner().tenant().to_text()) {
            Ok(tenant) => tenant,
            Err(_) => {
                drop(local);
                drop(global);
                return Err(MemberError::ShardServingUnavailable(database.clone()));
            }
        };
        let scope = engine.scope_for(&tenant);
        let route_registry =
            match shard_registry::RouteParticipantRegistry::load(Arc::clone(&engine), &scope) {
                Ok(registry) => registry,
                Err(_) => {
                    drop(local);
                    drop(global);
                    return Err(MemberError::ShardServingUnavailable(database.clone()));
                }
            };
        Ok(DatabaseLease {
            database: database.clone(),
            location,
            engine,
            route_registry,
            _global: Arc::new(global),
            _local: Arc::new(local),
            restore_paths: Arc::clone(&self.inner.restore_paths),
        })
    }

    /// Acquires a lease for an origin write. The engine repeats this check at
    /// commit time, so an offset update racing this call still fails closed.
    pub fn acquire_write(&self, database: &DatabaseId) -> Result<DatabaseLease, MemberError> {
        let offset = *lock_unpoisoned(&self.inner.clock_offset_seconds);
        if !offset.is_some_and(|value| {
            value.is_finite() && value.abs() <= CLOCK_OFFSET_WRITE_REFUSAL_THRESHOLD_SECONDS
        }) {
            return Err(MemberError::ClockOffsetUnsafe);
        }
        self.acquire(database)
    }

    /// Attaches the authoritative membership health source. Replacing an
    /// existing source is allowed so control-plane epoch changes can swap a
    /// snapshot without rebuilding the storage catalog.
    pub fn set_membership_authority(&self, authority: Option<Arc<dyn MembershipAuthority>>) {
        *lock_unpoisoned(&self.inner.membership_authority) = authority;
    }

    /// Attaches the authoritative replication health source.
    pub fn set_replication_authority(
        &self,
        authority: Option<Arc<dyn ReplicationHealthAuthority>>,
    ) {
        *lock_unpoisoned(&self.inner.replication_authority) = authority;
    }

    /// Returns whether storage and both attached authorities permit serving.
    #[must_use]
    pub fn production_ready(&self) -> bool {
        self.status().production_ready
    }

    /// Feeds the chrony/NTP observation to the Member and every resident
    /// Engine. Future lazy opens receive the same observation before serving.
    pub fn set_clock_offset_seconds(&self, offset_seconds: f64) {
        let offset = offset_seconds.is_finite().then_some(offset_seconds);
        *lock_unpoisoned(&self.inner.clock_offset_seconds) = offset;
        let engines = lock_unpoisoned(&self.inner.catalog)
            .databases
            .values()
            .filter_map(|entry| match &entry.engine {
                ResidentEngine::Open(engine) => Some(Arc::clone(engine)),
                _ => None,
            })
            .collect::<Vec<_>>();
        for engine in engines {
            match offset {
                Some(value) => engine.set_clock_offset_seconds(value),
                None => engine.clear_clock_offset_seconds(),
            }
        }
    }

    /// Marks the host clock observation stale or unavailable. This refuses
    /// new origin writes until a finite safe sample is supplied.
    pub fn clear_clock_offset_seconds(&self) {
        self.set_clock_offset_seconds(f64::NAN);
    }

    /// Returns the latest host clock observation.
    #[must_use]
    pub fn clock_offset_seconds(&self) -> Option<f64> {
        *lock_unpoisoned(&self.inner.clock_offset_seconds)
    }

    /// Installs the legacy pause-only callback. Prefer
    /// [`Self::set_backup_hooks`] so a stream can be resumed after reopen.
    pub fn set_backup_pause_callback(&self, callback: Option<BackupPauseCallback>) {
        *lock_unpoisoned(&self.inner.backup_hooks) =
            callback.map(|pause| BackupHooks { pause, resume: Arc::new(|_| {}) });
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

    /// Installs explicit pause and resume hooks for idle/LRU database close.
    /// Hooks run outside catalog and lifecycle locks.
    pub fn set_backup_hooks(&self, hooks: Option<BackupHooks>) {
        *lock_unpoisoned(&self.inner.backup_hooks) = hooks;
    }

    /// Convenience builder for explicit backup lifecycle hooks.
    #[must_use]
    pub fn with_backup_hooks(self, hooks: BackupHooks) -> Self {
        self.set_backup_hooks(Some(hooks));
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
    route_registry: Arc<shard_registry::RouteParticipantRegistry>,
    _global: Arc<AdmissionPermit>,
    _local: Arc<AdmissionPermit>,
    restore_paths: Arc<Mutex<BTreeSet<PathBuf>>>,
}

/// Exclusive per-path exclusion held while a restore replaces database bytes.
pub struct RestorePermit {
    database: DatabaseId,
    location: PathBuf,
    path_guard: Arc<Mutex<BTreeSet<PathBuf>>>,
}

impl RestorePermit {
    /// Returns the database identity protected by this permit.
    #[must_use]
    pub fn database_id(&self) -> &DatabaseId {
        &self.database
    }

    /// Returns the protected durable location.
    #[must_use]
    pub fn location(&self) -> &Path {
        &self.location
    }
}

impl Drop for RestorePermit {
    fn drop(&mut self) {
        if let Ok(mut paths) = self.path_guard.lock() {
            paths.remove(&self.location);
        }
    }
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

    pub(crate) fn shared_engine(&self) -> Arc<Engine> {
        Arc::clone(&self.engine)
    }

    /// Replaces a stale route snapshot while sharing its existing admission permits.
    pub fn refreshed_serving_lease(&self) -> Result<Option<Self>, crate::EngineError> {
        if self.route_registry.is_current()? {
            return Ok(None);
        }
        let tenant = self.route_registry.tenant_id();
        let scope = self.engine.scope_for(tenant);
        let route_registry =
            shard_registry::RouteParticipantRegistry::load(self.engine.clone(), &scope)
                .map_err(|_| crate::EngineError::StorageFailure)?;
        if !route_registry.is_current()? {
            return Err(crate::EngineError::Conflict);
        }
        Ok(Some(Self {
            database: self.database.clone(),
            location: self.location.clone(),
            engine: self.engine.clone(),
            route_registry,
            _global: self._global.clone(),
            _local: self._local.clone(),
            restore_paths: self.restore_paths.clone(),
        }))
    }

    /// Captures owned admitted lanes and MVCC snapshots under the database publication gate.
    /// Returned snapshots retain this lease's counted admission until all clones drop.
    pub fn admitted_shard_read_cut(&self) -> Result<AdmittedShardReadCut, crate::EngineError> {
        let mut cut = self.route_registry.read_cut()?;
        cut.admission = Some((self._global.clone(), self._local.clone()));
        for participant in &mut cut.participants {
            participant.admission = Some((self._global.clone(), self._local.clone()));
        }
        Ok(cut)
    }

    /// Selects the physical participant shared by all supplied document IDs.
    pub fn document_participant(
        &self,
        collection: &crate::types::CollectionName,
        ids: &[crate::types::DocumentId],
    ) -> Result<&Engine, crate::EngineError> {
        let keys = ids
            .iter()
            .map(|id| crate::shard_split::canonical_shard_key(collection, id))
            .collect::<Vec<_>>();
        self.route_registry.participant(&keys)
    }

    /// Begins an origin transaction through the lease-owned durable route.
    /// Callers cannot supply a router or filesystem location.
    pub fn begin_routed_document(
        &self,
        scope: &TenantScope,
        collection: &crate::types::CollectionName,
        id: &crate::types::DocumentId,
        operation: OperationId,
    ) -> Result<crate::engine::Transaction<'_>, crate::engine::EngineError> {
        let key = crate::shard_split::canonical_shard_key(collection, id);
        self.route_registry.begin(scope, &key, operation)
    }

    /// Begins one transaction for document IDs that all route to one
    /// admitted shard participant.
    pub fn begin_routed_batch(
        &self,
        scope: &TenantScope,
        collection: &crate::types::CollectionName,
        ids: &[crate::types::DocumentId],
        operation: OperationId,
    ) -> Result<crate::engine::Transaction<'_>, crate::engine::EngineError> {
        let keys = ids
            .iter()
            .map(|id| crate::shard_split::canonical_shard_key(collection, id))
            .collect::<Vec<_>>();
        self.route_registry.begin_batch(scope, &keys, operation)
    }

    /// Begins a routed batch from precomputed canonical keys.
    ///
    /// This lower-level form is reserved for serving adapters that already
    /// own the canonical key encoding; ordinary callers should use
    /// [`Self::begin_routed_batch`].
    pub fn begin_routed_batch_keys(
        &self,
        scope: &TenantScope,
        keys: &[Vec<u8>],
        operation: OperationId,
    ) -> Result<crate::engine::Transaction<'_>, crate::engine::EngineError> {
        self.route_registry.begin_batch(scope, keys, operation)
    }

    /// Resolves an idempotent document request on its admitted participant.
    pub fn resolve_routed_document_request(
        &self,
        scope: &TenantScope,
        collection: &crate::types::CollectionName,
        id: &crate::types::DocumentId,
        operation: OperationId,
        fingerprint: [u8; 32],
    ) -> Result<
        Option<(Arc<Engine>, crate::CommitReceipt, Option<String>)>,
        crate::engine::EngineError,
    > {
        let key = crate::shard_split::canonical_shard_key(collection, id);
        self.route_registry.resolve_document_request(scope, &key, operation, fingerprint)
    }

    /// Resolves batch provenance on its original admitted file.
    pub fn resolve_routed_batch_request(
        &self,
        scope: &TenantScope,
        collection: &crate::types::CollectionName,
        ids: &[crate::types::DocumentId],
        operation: OperationId,
        fingerprint: [u8; 32],
    ) -> Result<Option<(Arc<Engine>, crate::CommitReceipt, usize)>, crate::EngineError> {
        let keys = ids
            .iter()
            .map(|id| crate::shard_split::canonical_shard_key(collection, id))
            .collect::<Vec<_>>();
        self.route_registry.resolve_batch_request(scope, &keys, operation, fingerprint)
    }

    /// Converts this sole admitted lease into an exclusive restore permit.
    /// The engine and admission permits are dropped before the permit returns.
    pub fn reserve_restore(self) -> Result<RestorePermit, (DatabaseLease, MemberError)> {
        let location = self.location.clone();
        let database = self.database.clone();
        let path_guard = Arc::clone(&self.restore_paths);
        let restore_paths = Arc::clone(&self.restore_paths);
        let mut paths = match restore_paths.lock() {
            Ok(paths) => paths,
            Err(_) => return Err((self, MemberError::DatabaseBusy(database.clone()))),
        };
        if self._local.counter.active() != 1
            || Arc::strong_count(&self._local) != 1
            || !paths.insert(location.clone())
        {
            drop(paths);
            return Err((self, MemberError::DatabaseBusy(database)));
        }
        drop(paths);
        drop(self);
        Ok(RestorePermit { database, location, path_guard })
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
    let callback =
        lock_unpoisoned(&inner.backup_hooks).as_ref().map(|hooks| Arc::clone(&hooks.pause));
    if let Some(callback) = callback {
        callback(database);
    }
}

fn resume_backup(inner: &MemberInner, database: &DatabaseId) {
    let callback =
        lock_unpoisoned(&inner.backup_hooks).as_ref().map(|hooks| Arc::clone(&hooks.resume));
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
                entry.backup_paused = true;
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
                entry.backup_paused = false;
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
    loop {
        let mut catalog = lock_unpoisoned(&inner.catalog);
        let entry = catalog
            .databases
            .get(database)
            .ok_or_else(|| MemberError::UnknownDatabase(database.clone()))?;
        if entry.admissions.active() != 0 {
            return Err(MemberError::DatabaseBusy(database.clone()));
        }
        if matches!(entry.engine, ResidentEngine::Opening | ResidentEngine::Closing) {
            drop(wait_catalog(inner, catalog));
            continue;
        }
        let Some(close) = take_close_work(&mut catalog, database, kind) else {
            return Ok(());
        };
        drop(catalog);
        return perform_close(inner, close);
    }
}

fn open_database_engine(
    inner: &Arc<MemberInner>,
    database: &DatabaseId,
    location: &Path,
    allow_create: bool,
) -> Result<Arc<Engine>, MemberError> {
    loop {
        let mut catalog = lock_unpoisoned(&inner.catalog);
        let scope = catalog
            .databases
            .get(database)
            .ok_or_else(|| MemberError::UnknownDatabase(database.clone()))?
            .scope
            .clone();
        let storage_keys =
            catalog.databases.get(database).and_then(|entry| entry.storage_keys.clone());
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

        let result = location
            .strip_prefix(&inner.root)
            .map_err(|_| CatalogValidationError::InvalidDatabaseLocation)
            .and_then(|relative| validate_location(&inner.root, relative))
            .map_err(MemberError::Validation)
            .and_then(|validated| {
                if validated != location {
                    return Err(MemberError::Validation(
                        CatalogValidationError::InvalidDatabaseLocation,
                    ));
                }
                let config = if allow_create {
                    EngineConfig::durable(location)
                } else {
                    EngineConfig::existing_durable(location)
                }
                .map_err(|error| MemberError::EngineConfig { database: database.clone(), error })?;
                // Production entries stay closed until writer acceptance. Their
                // first acquisition creates the directory, then checks it again
                // before SQLite can open any files.
                revalidate_created_location(&inner.root, location)
                    .map_err(MemberError::Validation)?;
                let config = scope.as_ref().map_or(config.clone(), |scope| {
                    config.with_owner(scope.owner().clone()).with_replication_origin(
                        crate::types::RegionId::try_new(scope.owner().region().as_str())
                            .expect("registered Region"),
                    )
                });
                let config = storage_keys
                    .as_ref()
                    .map_or(config.clone(), |keys| config.with_storage_keys(Arc::clone(keys)));
                let engine = Engine::open(config).map(Arc::new).map_err(|error| {
                    MemberError::EngineOpen { database: database.clone(), error }
                })?;
                engine
                    .install_shard_resource_factory(shard_resources::factory(
                        inner,
                        database.clone(),
                    ))
                    .map_err(|_| MemberError::EngineOpen {
                        database: database.clone(),
                        error: EngineOpenError::StorageUnavailable,
                    })?;
                match *lock_unpoisoned(&inner.clock_offset_seconds) {
                    Some(offset) => engine.set_clock_offset_seconds(offset),
                    None => engine.clear_clock_offset_seconds(),
                }
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
                let resume =
                    catalog.databases.get(database).is_some_and(|entry| entry.backup_paused);
                if let Some(entry) = catalog.databases.get_mut(database) {
                    entry.engine = ResidentEngine::Open(Arc::clone(&engine));
                    entry.last_used = Instant::now();
                    entry.access_stamp = stamp;
                    entry.backup_paused = false;
                }
                catalog.metrics.opens = catalog.metrics.opens.saturating_add(1);
                inner.catalog_changed.notify_all();
                drop(catalog);
                if resume {
                    resume_backup(inner, database);
                }
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

    struct TestAuthority(AuthorityHealth);

    impl MembershipAuthority for TestAuthority {
        fn health(&self) -> AuthorityHealth {
            self.0
        }
    }

    impl ReplicationHealthAuthority for TestAuthority {
        fn health(&self) -> AuthorityHealth {
            self.0
        }
    }

    fn root(label: &str) -> PathBuf {
        let path = std::env::temp_dir()
            .join(format!("terrabase-member-owner-{label}-{}", std::process::id()));
        let _ = fs::remove_dir_all(&path);
        fs::create_dir_all(&path).expect("root");
        path
    }

    #[derive(Debug)]
    struct TestStorageKeys(crate::encryption::KeyVersionMetadata);
    impl crate::encryption::DatabaseKeySource for TestStorageKeys {
        fn current_lease(
            &self,
        ) -> Result<Arc<crate::encryption::DatabaseKeyLease>, crate::encryption::EncryptionError>
        {
            self.lease(self.0.version())
        }
        fn lease(
            &self,
            version: crate::encryption::DatabaseKeyVersion,
        ) -> Result<Arc<crate::encryption::DatabaseKeyLease>, crate::encryption::EncryptionError>
        {
            if version != self.0.version() {
                return Err(crate::encryption::EncryptionError::InvalidKeyMetadata);
            }
            crate::encryption::DatabaseKeyLease::test_oci_vault(
                self.0.clone(),
                crate::encryption::DatabaseKey::from_bytes([29; 32]),
                Duration::from_secs(60),
            )
            .map(Arc::new)
        }
    }
    fn storage_keys(
        scope: &DatabaseScope,
    ) -> BTreeMap<DatabaseId, Arc<dyn crate::encryption::DatabaseKeySource>> {
        let metadata = crate::encryption::KeyVersionMetadata::new(
            crate::encryption::KeyScope::new_with_project(
                scope.owner().environment().as_str(),
                scope.tenant(),
                scope.project(),
                scope.database(),
                crate::encryption::KeyPurpose::Data,
            )
            .unwrap(),
            crate::encryption::DatabaseKeyVersion::from_text("01ARZ3NDEKTSV4RRFFQ69G5FB0").unwrap(),
        );
        BTreeMap::from([(
            scope.database().into(),
            Arc::new(TestStorageKeys(metadata)) as Arc<dyn crate::encryption::DatabaseKeySource>,
        )])
    }

    struct TestRole(AcceptedDatabaseRole);
    impl DatabaseRoleAuthority for TestRole {
        fn accepted_role(&self, _: &DatabaseId) -> Option<AcceptedDatabaseRole> {
            Some(self.0)
        }
    }
    fn accept_writer(member: &Member, id: &DatabaseId) {
        member
            .accept_database_role(
                id,
                &TestRole(AcceptedDatabaseRole { epoch: 1, role: DatabaseRole::Writer }),
            )
            .unwrap();
    }

    #[test]
    fn existing_acquisition_never_materializes_missing_production_storage() {
        let root = root("existing-only");
        let scope = DatabaseScope::new(
            "production",
            TenantUlid::generate(),
            ProjectUlid::generate(),
            DatabaseUlid::generate(),
            RegionCode::GRU,
        )
        .unwrap();
        let id = DatabaseId::from(scope.database());
        let member = Member::open_production(
            &root,
            [DatabaseSpec::scoped(scope.clone(), "db")],
            AdmissionLimits::new(2, 1),
            storage_keys(&scope),
        )
        .unwrap();
        assert!(matches!(member.acquire_existing(&id), Err(MemberError::DatabaseNotWriter(_))));
        accept_writer(&member, &id);
        assert!(member.acquire_existing(&id).is_err());
        assert!(!root.join("db").exists());
        assert_eq!(member.open_database_count(), 0);

        // Even an existing empty directory must not gain an owner or database.
        fs::create_dir(root.join("db")).unwrap();
        assert!(member.acquire_existing(&id).is_err());
        assert_eq!(fs::read_dir(root.join("db")).unwrap().count(), 0);
        assert_eq!(member.open_database_count(), 0);
        member.close(Duration::from_secs(1)).unwrap();
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn follower_and_pending_never_open_sqlite_and_promotion_requires_new_epoch() {
        let root = root("roles");
        let scope = DatabaseScope::new(
            "production",
            TenantUlid::generate(),
            ProjectUlid::generate(),
            DatabaseUlid::generate(),
            RegionCode::GRU,
        )
        .unwrap();
        let spec = DatabaseSpec::scoped(scope.clone(), "db");
        let id = DatabaseId::from(scope.database());
        let open = || {
            Member::open_production(
                &root,
                [spec.clone()],
                AdmissionLimits::new(2, 1),
                storage_keys(&scope),
            )
            .unwrap()
        };
        let member = open();
        assert!(matches!(member.acquire(&id), Err(MemberError::DatabaseNotWriter(_))));
        assert!(!root.join("db").exists());
        let follower = TestRole(AcceptedDatabaseRole { epoch: 2, role: DatabaseRole::Follower });
        member.accept_database_role(&id, &follower).unwrap();
        assert!(matches!(member.acquire(&id), Err(MemberError::DatabaseNotWriter(_))));
        member.close(Duration::from_secs(1)).unwrap();
        let member = open();
        assert!(matches!(member.acquire(&id), Err(MemberError::DatabaseNotWriter(_))));
        assert!(
            member
                .accept_database_role(
                    &id,
                    &TestRole(AcceptedDatabaseRole { epoch: 1, role: DatabaseRole::Writer })
                )
                .is_err()
        );
        assert!(
            member
                .accept_database_role(
                    &id,
                    &TestRole(AcceptedDatabaseRole { epoch: 2, role: DatabaseRole::Writer })
                )
                .is_err()
        );
        member.accept_database_role(&id, &follower).unwrap();
        member
            .accept_database_role(
                &id,
                &TestRole(AcceptedDatabaseRole { epoch: 3, role: DatabaseRole::Writer }),
            )
            .unwrap();
        let lease = member.acquire(&id).unwrap();
        assert_eq!(lease.shared_engine().origin_epoch().unwrap(), (3, false));
        member
            .accept_database_role(
                &id,
                &TestRole(AcceptedDatabaseRole { epoch: 3, role: DatabaseRole::Writer }),
            )
            .expect("identical accepted assignment may replay with an active lease");
        assert!(
            member
                .accept_database_role(
                    &id,
                    &TestRole(AcceptedDatabaseRole { epoch: 4, role: DatabaseRole::Follower })
                )
                .is_err()
        );
        drop(lease);
        member.close(Duration::from_secs(1)).unwrap();
        let _ = fs::remove_dir_all(root);
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
        let member = Member::open_production(
            &root,
            [spec.clone()],
            AdmissionLimits::new(2, 1),
            storage_keys(&scope),
        )
        .expect("production member");
        accept_writer(&member, &id);
        let lease = member.acquire(&id).expect("lease");
        assert_eq!(lease.database_owner().database(), scope.database());
        drop(lease);
        member.close(Duration::from_secs(1)).expect("close");
        let reopened = Member::open_production(
            &root,
            [spec],
            AdmissionLimits::new(2, 1),
            storage_keys(&scope),
        )
        .expect("restart");
        accept_writer(&reopened, &id);
        let lease = reopened.acquire(&id).expect("lease");
        assert_eq!(lease.database_owner(), scope.owner());
        assert_eq!(lease.shared_engine().origin_epoch().unwrap(), (1, false));
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
        let member = Member::open_production(
            &root,
            [spec],
            AdmissionLimits::new(4, 2),
            storage_keys(&scope),
        )
        .expect("production member");
        accept_writer(&member, &id);
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
        let member = Member::open_production(
            &root,
            [spec],
            AdmissionLimits::new(4, 2),
            storage_keys(&scope),
        )
        .expect("production member");
        accept_writer(&member, &id);
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
                AdmissionLimits::default(),
                BTreeMap::new()
            ),
            Err(MemberOpenError::Validation(CatalogValidationError::UnscopedDatabase))
        ));
        let _ = fs::remove_dir_all(root);
    }

    #[test]
    fn physical_shard_open_reserves_member_budget_once_and_releases_with_cache() {
        use crate::shard_split::{ShardSplit, SplitContext, SplitEngineAdapter, SplitStateStore};
        for budget in [1, 2] {
            let root = root(&format!("shard-resident-budget-{budget}"));
            let scope = DatabaseScope::new(
                "production",
                TenantUlid::generate(),
                ProjectUlid::generate(),
                DatabaseUlid::generate(),
                RegionCode::GRU,
            )
            .unwrap();
            let id = DatabaseId::from(scope.database());
            let member = Member::open_production(
                &root,
                [DatabaseSpec::scoped(scope.clone(), "db")],
                AdmissionLimits::new(4, 2)
                    .with_rss_budget_bytes(budget)
                    .with_database_rss_bytes(1)
                    .with_max_open_databases(budget)
                    .with_max_concurrent_opens(1),
                storage_keys(&scope),
            )
            .unwrap();
            accept_writer(&member, &id);
            let lease = member.acquire(&id).unwrap();
            let engine = lease.shared_engine();
            let tenant = TenantId::try_new(&scope.tenant().to_text()).unwrap();
            let engine_scope = engine.scope_for(&tenant);
            let context = SplitContext::new(
                192,
                u128::from_be_bytes(engine.database_id().as_bytes()),
                1,
                2,
                1,
                0,
            );
            let split = ShardSplit::plan_with_context(None, b"m".to_vec(), context).unwrap();
            let mut adapter =
                crate::engine::shard_split::ScopedShardEngine::new(engine.clone(), engine_scope)
                    .unwrap();
            adapter.save(&split.durable_state()).unwrap();
            if budget == 1 {
                assert!(adapter.create_split_target(&context).is_err());
                assert!(
                    !root.join("db/shards/0000000000000002.sqlite3").exists(),
                    "budget refusal precedes SQLite creation"
                );
                assert_eq!(member.open_database_count(), 1);
            } else {
                adapter.create_split_target(&context).unwrap();
                let target = engine.open_shard_participant(2).unwrap().unwrap();
                for _ in 0..8 {
                    assert!(Arc::ptr_eq(
                        &target,
                        &engine.open_shard_participant(2).unwrap().unwrap()
                    ));
                    assert_eq!(member.open_database_count(), 2);
                    assert_eq!(member.status().resident_rss_bytes, 2);
                }
                drop(target);
            }
            assert_eq!(member.status().resident_rss_bytes, budget);
            drop(adapter);
            drop(engine);
            drop(lease);
            member.close(Duration::from_secs(5)).unwrap();
            assert_eq!(member.open_database_count(), 0);
            assert_eq!(member.status().resident_rss_bytes, 0);
            fs::remove_dir_all(root).unwrap();
        }
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

    #[test]
    fn backup_hooks_pause_then_resume_only_after_reopen() {
        let root = root("backup-hooks");
        let member =
            Member::open(&root, [DatabaseSpec::new("db", "db")], AdmissionLimits::new(4, 2))
                .expect("member");
        let events = Arc::new(Mutex::new(Vec::<&'static str>::new()));
        let pause_events = Arc::clone(&events);
        let resume_events = Arc::clone(&events);
        member.set_backup_hooks(Some(BackupHooks::new(
            move |_| lock_unpoisoned(&pause_events).push("pause"),
            move |_| lock_unpoisoned(&resume_events).push("resume"),
        )));
        let id = DatabaseId::try_new("db").expect("id");
        drop(member.acquire(&id).expect("open"));
        assert_eq!(
            member.close_idle_at(Duration::ZERO, Instant::now() + Duration::from_secs(1)),
            1
        );
        assert_eq!(*lock_unpoisoned(&events), ["pause"]);
        drop(member.acquire(&id).expect("reopen"));
        assert_eq!(*lock_unpoisoned(&events), ["pause", "resume"]);
        member.close(Duration::from_secs(5)).expect("close");
        let _ = fs::remove_dir_all(root);
    }

    #[test]
    fn restore_permit_fences_generic_acquire_until_authorized_reopen() {
        let root = root("restore-permit");
        let member =
            Member::open(&root, [DatabaseSpec::new("db", "db")], AdmissionLimits::new(4, 2))
                .expect("member");
        let id = DatabaseId::try_new("db").expect("id");
        let lease = member.acquire(&id).expect("lease");
        let competing = member.acquire(&id).expect("competing lease");
        let (lease, error) = match lease.reserve_restore() {
            Err(error) => error,
            Ok(_) => panic!("restore requires sole lease"),
        };
        assert!(matches!(error, MemberError::DatabaseBusy(_)));
        drop(competing);
        let permit = match lease.reserve_restore() {
            Ok(permit) => permit,
            Err(_) => panic!("exclusive restore permit"),
        };
        assert!(matches!(member.acquire(&id), Err(MemberError::DatabaseBusy(_))));
        assert!(matches!(member.acquire_existing(&id), Err(MemberError::DatabaseBusy(_))));
        let reopened =
            member.acquire_existing_for_restore(&id, &permit).expect("authorized reopen");
        drop(reopened);
        drop(permit);
        assert!(member.acquire_existing(&id).is_ok());
        member.close(Duration::from_secs(5)).expect("close");
        let _ = fs::remove_dir_all(root);
    }

    #[test]
    fn health_authorities_and_clock_gate_fail_closed() {
        let root = root("authority-health");
        let member =
            Member::open(&root, [DatabaseSpec::new("db", "db")], AdmissionLimits::new(4, 2))
                .expect("member");
        assert!(!member.production_ready());
        member.set_membership_authority(Some(Arc::new(TestAuthority(AuthorityHealth::Healthy))));
        member.set_replication_authority(Some(Arc::new(TestAuthority(AuthorityHealth::Healthy))));
        assert!(member.production_ready());
        member.set_clock_offset_seconds(0.500_001);
        assert!(!member.status().writes_allowed);
        assert!(matches!(
            member.acquire_write(&DatabaseId::try_new("db").expect("id")),
            Err(MemberError::ClockOffsetUnsafe)
        ));
        member.set_clock_offset_seconds(-0.5);
        assert!(member.status().writes_allowed);
        member.close(Duration::from_secs(5)).expect("close");
        let _ = fs::remove_dir_all(root);
    }

    #[test]
    fn routing_never_forwards_non_hosted_databases() {
        let placement = DatabasePlacement::try_new(
            [RegionCode::GRU, RegionCode::FRA],
            Some("br-only".to_owned()),
        )
        .expect("placement");
        assert_eq!(
            decide_routing(
                RegionCode::SFO,
                false,
                Some(RegionCode::GRU),
                Some(&placement),
                RoutingOperation::Write,
            ),
            RoutingDecision::Misdirected { placement: placement.clone() }
        );
        assert_eq!(
            decide_routing(
                RegionCode::GRU,
                true,
                Some(RegionCode::FRA),
                Some(&placement),
                RoutingOperation::Write,
            ),
            RoutingDecision::Forward { region: RegionCode::FRA }
        );
        assert_eq!(
            decide_routing(RegionCode::GRU, true, None, None, RoutingOperation::Read),
            RoutingDecision::UnknownDatabase
        );
    }

    #[test]
    fn principal_cache_is_generation_stamped_and_exactly_bounded() {
        let cache = PrincipalCache::new(3);
        cache.insert([1; 32], 4, "one");
        cache.insert([2; 32], 4, "two");
        cache.insert([3; 32], 4, "three");
        assert_eq!(cache.get(&[1; 32], 4), Some("one"));
        assert_eq!(cache.get(&[1; 32], 3), None);
        cache.insert([4; 32], 4, "four");
        assert!(cache.len() <= 3);
        assert!(cache.get(&[1; 32], 4).is_none());
        cache.clear();
        assert!(cache.is_empty());
    }
}
