//! Fail-closed production catalog bootstrap.
//!
//! This module binds the typed server configuration to one root-owned
//! [`terrabase::member::Member`] catalog and the cluster routing and
//! placement catalogs. It deliberately stops before transport, key, or
//! readiness setup: constructing this value proves only that storage and the
//! static production catalogs agree.

use std::collections::BTreeSet;
use std::fs;
use std::path::{Component, Path, PathBuf};
use std::time::Duration;

#[cfg(unix)]
use std::os::unix::fs::{MetadataExt, PermissionsExt};

use terrabase::member::{
    AdmissionLimits, DatabaseScope, DatabaseSpec, MAX_ADMISSION_PERMITS, Member, MemberError,
    MemberOpenError,
};
use terrabase_cluster::production::{
    DatabaseRouteScope, DatabaseRoutingCatalog, ProductionMemberError, ProductionPlacement,
    ProductionRegionSeats,
};

use crate::config::ProductionConfig;

/// A production dependency whose health is required before serving.
#[derive(Clone, Copy, Debug, Eq, Ord, PartialEq, PartialOrd)]
pub enum ProductionComponent {
    /// The durable membership/placement authority.
    Membership,
    /// The RF=2 WAL follower and cross-Region journal tailer.
    Replication,
    /// The complete Member-backed public API host.
    PublicApi,
    /// The private TBEP mTLS control host.
    ControlApi,
    /// Litestream backup and restore workers.
    Backup,
    /// Prometheus and lifecycle probe listener.
    Metrics,
    /// Structured process logging.
    Logging,
    /// Host clock discipline against OCI NTP.
    Chrony,
}

impl ProductionComponent {
    /// Returns the stable operator-facing component name.
    #[must_use]
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::Membership => "membership",
            Self::Replication => "replication",
            Self::PublicApi => "public-api",
            Self::ControlApi => "control-api",
            Self::Backup => "backup",
            Self::Metrics => "metrics",
            Self::Logging => "logging",
            Self::Chrony => "chrony",
        }
    }
}

/// A precise refusal from one missing production integration seam.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct ProductionComponentError {
    /// The component that could not be initialized.
    pub component: ProductionComponent,
    /// A bounded, non-secret explanation suitable for startup logs.
    pub detail: &'static str,
}

impl std::fmt::Display for ProductionComponentError {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(formatter, "{}: {}", self.component.as_str(), self.detail)
    }
}

impl std::error::Error for ProductionComponentError {}

/// Integration seam for dependencies that are owned by the Member runtime.
///
/// The catalog is opened before this callback and is transferred only after
/// the callback succeeds. Implementations must not claim readiness here;
/// callers do that only after their membership, transport, replication,
/// backup, metrics, logging, and clock observations are all active.
pub trait ProductionDependencyProvider: Send + Sync {
    /// Initializes the complete production serving composition.
    ///
    /// # Errors
    ///
    /// Returns the exact component that is unavailable. A provider must not
    /// collapse an individual missing integration into a generic production
    /// mode refusal.
    fn initialize(
        &self,
        config: &ProductionConfig,
        catalog: &ProductionCatalogParts,
    ) -> Result<(), ProductionComponentError>;
}

/// Default provider used by the binary until the real Member host adapter is
/// linked. It deliberately identifies the absent integration precisely.
#[derive(Clone, Copy, Debug, Default)]
pub struct MissingProductionDependencyProvider;

impl ProductionDependencyProvider for MissingProductionDependencyProvider {
    fn initialize(
        &self,
        _config: &ProductionConfig,
        _catalog: &ProductionCatalogParts,
    ) -> Result<(), ProductionComponentError> {
        Err(ProductionComponentError {
            component: ProductionComponent::Replication,
            detail: "the durable RF=2 data-plane and complete Member host adapter is not installed",
        })
    }
}

/// Opens the authoritative production catalog and invokes an injected
/// dependency provider before transferring ownership of its engines.
///
/// This function is the testable composition boundary used by the daemon and
/// by the local harness. It has no development fallback and performs no
/// listener bind when a required provider is absent.
pub fn prepare_production<P: ProductionDependencyProvider>(
    config: &ProductionConfig,
    provider: &P,
) -> Result<ProductionCatalogParts, ProductionStartupError> {
    let bootstrap =
        ProductionCatalogBootstrap::open(config).map_err(ProductionStartupError::Catalog)?;
    let parts = bootstrap.into_parts();
    provider.initialize(config, &parts).map_err(ProductionStartupError::Component)?;
    Ok(parts)
}

/// Failure before a production Member can make a serving claim.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum ProductionStartupError {
    /// The durable catalog could not be opened or validated.
    Catalog(ProductionCatalogError),
    /// The QUIC endpoint or its mTLS material could not be opened.
    Transport(String),
    /// The catalog and transport did not agree on the admitted Member.
    Member(terrabase_cluster::production::ProductionMemberError),
    /// One required integration seam is unavailable.
    Component(ProductionComponentError),
}

impl std::fmt::Display for ProductionStartupError {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::Catalog(error) => write!(formatter, "production catalog: {error}"),
            Self::Transport(error) => write!(formatter, "production transport: {error}"),
            Self::Member(error) => write!(formatter, "production Member: {error:?}"),
            Self::Component(error) => write!(formatter, "production component: {error}"),
        }
    }
}

impl std::error::Error for ProductionStartupError {}

/// A fully opened production storage catalog and its static cluster bindings.
///
/// This object contains no ready/serving claim. The embedded member's status
/// remains storage-only and reports `production_ready == false` until a later
/// orchestration layer proves transport, membership, and replication facts.
pub struct ProductionCatalogBootstrap {
    root: PathBuf,
    member: Option<Member>,
    specs: Vec<DatabaseSpec>,
    routes: DatabaseRoutingCatalog,
    placement: ProductionPlacement,
    limits: AdmissionLimits,
}

/// The owned storage and static catalogs handed to the production runtime.
///
/// The [`Member`] is moved into this value and is never cloned. Callers that
/// take ownership of this value also take responsibility for closing it via
/// [`Member::close`] after all runtime leases and data-plane work drain.
pub struct ProductionCatalogParts {
    /// The validated common durable root.
    pub root: PathBuf,
    /// The one root-owned durable storage catalog.
    pub member: Member,
    /// The relative, typed database specifications used to open the Member.
    pub specs: Vec<DatabaseSpec>,
    /// The typed route-to-database catalog.
    pub routes: DatabaseRoutingCatalog,
    /// The validated placement generation and replica-set epoch.
    pub placement: ProductionPlacement,
    /// The derived Member-wide admission limits.
    pub limits: AdmissionLimits,
}

impl ProductionCatalogBootstrap {
    /// Opens the enabled production databases beneath one derived root and
    /// proves agreement with the configured routing and placement catalogs.
    ///
    /// No network listener, key provider, or readiness state is opened here.
    /// Every path is checked for traversal and symlink components before a
    /// directory is created or an engine is opened.
    ///
    /// # Errors
    ///
    /// Returns a typed error whenever the catalog is empty, paths are unsafe,
    /// quotas cannot be represented without weakening them, storage refuses
    /// an entry, or placement does not cover every enabled database Region.
    pub fn open(config: &ProductionConfig) -> Result<Self, ProductionCatalogError> {
        let enabled =
            config.databases.iter().filter(|database| database.enabled).collect::<Vec<_>>();
        if enabled.is_empty() {
            return Err(ProductionCatalogError::EmptyCatalog);
        }

        let locations = enabled
            .iter()
            .map(|database| normalize_location(&database.location))
            .collect::<Result<Vec<_>, _>>()?;
        let root = derive_root(&locations)?;
        ensure_root(&root)?;

        let limits = derive_limits(&enabled)?;
        let mut specs = Vec::with_capacity(enabled.len());
        let mut route_scopes = Vec::with_capacity(enabled.len());
        for (database, location) in enabled.iter().zip(&locations) {
            let relative = location
                .strip_prefix(&root)
                .map_err(|_| ProductionCatalogError::LocationOutsideRoot(location.clone()))?;
            if relative.as_os_str().is_empty() {
                return Err(ProductionCatalogError::RootIsDatabaseLocation(root));
            }
            if relative.components().count() != 1 {
                return Err(ProductionCatalogError::LocationNotDirectChild(location.clone()));
            }
            let scope = DatabaseScope::try_new(
                config.environment.name.as_str(),
                database.tenant,
                database.project,
                database.database,
                database.region,
            )
            .map_err(|_| ProductionCatalogError::InvalidScope)?;
            specs.push(DatabaseSpec::scoped(scope, relative));
            route_scopes.push(DatabaseRouteScope::new(
                database.tenant,
                database.project,
                database.database,
            ));
        }

        let member = Member::open_production(&root, specs.clone(), limits)
            .map_err(ProductionCatalogError::MemberOpen)?;

        let routes = match DatabaseRoutingCatalog::try_new(route_scopes) {
            Ok(routes) => routes,
            Err(error) => return close_with(&member, ProductionCatalogError::Routing(error)),
        };
        let placement = match build_placement(config) {
            Ok(placement) => placement,
            Err(error) => return close_with(&member, error),
        };

        if enabled
            .iter()
            .any(|database| !placement.regions().any(|entry| entry.region() == database.region))
        {
            return close_with(&member, ProductionCatalogError::DatabaseRegionNotPlaced);
        }

        let member_databases = member.database_ids().into_iter().collect::<BTreeSet<_>>();
        let route_databases =
            routes.routes().map(|(_, database)| database.clone()).collect::<BTreeSet<_>>();
        if member_databases != route_databases || member.status().databases != routes.len() {
            return close_with(&member, ProductionCatalogError::CatalogMismatch);
        }

        Ok(Self { root, member: Some(member), specs, routes, placement, limits })
    }

    /// Returns the validated common durable root.
    #[must_use]
    pub fn root(&self) -> &Path {
        &self.root
    }

    /// Returns the opened root-owned storage member.
    #[must_use]
    pub fn member(&self) -> Option<&Member> {
        self.member.as_ref()
    }

    /// Returns the relative production database specifications.
    #[must_use]
    pub fn specs(&self) -> &[DatabaseSpec] {
        &self.specs
    }

    /// Returns the typed database routing catalog.
    #[must_use]
    pub fn routes(&self) -> &DatabaseRoutingCatalog {
        &self.routes
    }

    /// Returns the validated placement generation and replica-set epoch.
    #[must_use]
    pub fn placement(&self) -> &ProductionPlacement {
        &self.placement
    }

    /// Returns the derived Member-wide and per-database admission limits.
    #[must_use]
    pub const fn limits(&self) -> AdmissionLimits {
        self.limits
    }

    /// Closes the owned catalog within the caller-provided bounded deadline.
    ///
    /// The Member is relinquished only after close succeeds, so the bootstrap
    /// Drop path can retry bounded cleanup after a durable failure but cannot
    /// issue a second close after success. A durable drain or engine-shutdown
    /// failure is returned unchanged.
    ///
    /// # Errors
    ///
    /// Returns the durable [`MemberError`] reported by the root catalog.
    pub fn close(&mut self, timeout: Duration) -> Result<(), MemberError> {
        let member = self.member.as_ref().ok_or(MemberError::Draining)?;
        member.close(timeout)?;
        // Only relinquish ownership after a successful durable close. If the
        // close fails, Drop retains the Member and makes one bounded cleanup
        // attempt rather than silently abandoning it.
        self.member = None;
        Ok(())
    }

    /// Transfers every owned catalog component to the production runtime.
    ///
    /// This consumes the bootstrap and moves the exact opened [`Member`]; no
    /// clone or second storage authority is created. The returned parts own
    /// the Member and must eventually close it after runtime draining.
    ///
    /// # Panics
    ///
    /// Panics if the bootstrap was already closed and no longer owns its
    /// Member. A closed bootstrap cannot be transferred into a live runtime.
    #[must_use]
    pub fn into_parts(mut self) -> ProductionCatalogParts {
        let member =
            self.member.take().expect("a live production bootstrap always owns its Member");
        ProductionCatalogParts {
            root: std::mem::take(&mut self.root),
            member,
            specs: std::mem::take(&mut self.specs),
            routes: self.routes.clone(),
            placement: self.placement.clone(),
            limits: self.limits,
        }
    }
}

impl Drop for ProductionCatalogBootstrap {
    fn drop(&mut self) {
        // A bootstrap owns the opened engines. Keep destruction bounded even
        // when the caller forgets to perform an orderly shutdown.
        if let Some(member) = self.member.take() {
            let _ = member.close(Duration::from_secs(30));
        }
    }
}

/// A production catalog bootstrap refusal.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum ProductionCatalogError {
    /// No enabled database was present in the configuration.
    EmptyCatalog,
    /// A database location was not absolute.
    RelativeLocation(PathBuf),
    /// A database location contains `.` or `..` traversal.
    TraversalLocation(PathBuf),
    /// A path component is a symlink or could not be inspected safely.
    SymlinkComponent(PathBuf),
    /// A path component could not be created or validated as a directory.
    RootUnavailable(PathBuf),
    /// Enabled locations have no safe common root other than `/`.
    CommonRootIsFilesystemRoot,
    /// The derived root is itself one of the database locations.
    RootIsDatabaseLocation(PathBuf),
    /// A normalized database location is not strictly below the root.
    LocationOutsideRoot(PathBuf),
    /// Database locations must be direct children of the derived root.
    LocationNotDirectChild(PathBuf),
    /// A quota is zero or outside the Member admission bound.
    InvalidQuota,
    /// Different per-database quotas need a per-database limiter map.
    UnequalPerDatabaseQuotas,
    /// The global quota sum overflowed `usize` or the Member bound.
    QuotaOverflow,
    /// A typed database owner could not be constructed.
    InvalidScope,
    /// The storage Member refused to open.
    MemberOpen(MemberOpenError),
    /// The routing catalog refused the configured scopes.
    Routing(ProductionMemberError),
    /// The placement catalog refused the configured seats.
    Placement(ProductionMemberError),
    /// At least one enabled database Region is absent from placement.
    DatabaseRegionNotPlaced,
    /// The opened Member and routing catalog do not contain the same IDs.
    CatalogMismatch,
    /// Cleanup after a post-open refusal failed.
    Cleanup(MemberError),
}

impl std::fmt::Display for ProductionCatalogError {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        let message = match self {
            Self::EmptyCatalog => "no enabled production databases are configured",
            Self::RelativeLocation(_) => "production database locations must be absolute",
            Self::TraversalLocation(_) => "production database locations may not traverse",
            Self::SymlinkComponent(_) => "production database paths may not contain symlinks",
            Self::RootUnavailable(_) => "the production database root is unavailable",
            Self::CommonRootIsFilesystemRoot => {
                "database locations do not share a safe common root"
            }
            Self::RootIsDatabaseLocation(_) => "the production root may not be a database location",
            Self::LocationOutsideRoot(_) => "a database location is outside the production root",
            Self::LocationNotDirectChild(_) => {
                "production database locations must be direct root children"
            }
            Self::InvalidQuota => "a database admission quota is invalid",
            Self::UnequalPerDatabaseQuotas => {
                "differing database admission quotas are not representable by this Member"
            }
            Self::QuotaOverflow => "database admission quotas exceed the bounded Member capacity",
            Self::InvalidScope => "a production database scope is invalid",
            Self::MemberOpen(_) => "the production Member catalog could not be opened",
            Self::Routing(_) => "the production routing catalog is invalid",
            Self::Placement(_) => "the production placement is invalid",
            Self::DatabaseRegionNotPlaced => "an enabled database Region is absent from placement",
            Self::CatalogMismatch => "the production Member and routing catalogs disagree",
            Self::Cleanup(_) => "production catalog cleanup failed",
        };
        formatter.write_str(message)
    }
}

impl std::error::Error for ProductionCatalogError {}

fn normalize_location(path: &Path) -> Result<PathBuf, ProductionCatalogError> {
    if !path.is_absolute() {
        return Err(ProductionCatalogError::RelativeLocation(path.to_path_buf()));
    }
    let mut normalized = PathBuf::new();
    for component in path.components() {
        match component {
            Component::CurDir | Component::ParentDir => {
                return Err(ProductionCatalogError::TraversalLocation(path.to_path_buf()));
            }
            Component::RootDir => normalized.push(Path::new("/")),
            Component::Prefix(prefix) => normalized.push(prefix.as_os_str()),
            Component::Normal(component) => normalized.push(component),
        }
    }
    reject_symlinks(&normalized)?;
    Ok(normalized)
}

fn derive_root(locations: &[PathBuf]) -> Result<PathBuf, ProductionCatalogError> {
    let first = locations.first().ok_or(ProductionCatalogError::EmptyCatalog)?;
    // A database location names the engine directory, so the catalog root is
    // the common ancestor of the locations' parents. This also gives a
    // useful root for a one-database production catalog.
    let mut root = first
        .parent()
        .filter(|parent| !parent.as_os_str().is_empty())
        .ok_or(ProductionCatalogError::CommonRootIsFilesystemRoot)?
        .to_path_buf();
    for location in &locations[1..] {
        let parent = location
            .parent()
            .filter(|parent| !parent.as_os_str().is_empty())
            .ok_or(ProductionCatalogError::CommonRootIsFilesystemRoot)?;
        while !parent.starts_with(&root) {
            if !root.pop() {
                return Err(ProductionCatalogError::CommonRootIsFilesystemRoot);
            }
        }
    }
    if root == Path::new("/") {
        return Err(ProductionCatalogError::CommonRootIsFilesystemRoot);
    }
    if locations.iter().any(|location| location == &root) {
        return Err(ProductionCatalogError::RootIsDatabaseLocation(root));
    }
    if locations.iter().any(|location| !location.starts_with(&root)) {
        return Err(ProductionCatalogError::LocationOutsideRoot(root));
    }
    Ok(root)
}

fn ensure_root(root: &Path) -> Result<(), ProductionCatalogError> {
    reject_symlinks(root)?;
    let created = !root.exists();
    if created {
        fs::create_dir_all(root)
            .map_err(|_| ProductionCatalogError::RootUnavailable(root.to_path_buf()))?;
    }
    reject_symlinks(root)?;
    let mut metadata = fs::symlink_metadata(root)
        .map_err(|_| ProductionCatalogError::RootUnavailable(root.to_path_buf()))?;
    if !metadata.is_dir() {
        return Err(ProductionCatalogError::RootUnavailable(root.to_path_buf()));
    }
    #[cfg(unix)]
    {
        if created {
            fs::set_permissions(root, fs::Permissions::from_mode(0o700))
                .map_err(|_| ProductionCatalogError::RootUnavailable(root.to_path_buf()))?;
            metadata = fs::symlink_metadata(root)
                .map_err(|_| ProductionCatalogError::RootUnavailable(root.to_path_buf()))?;
        }
        let mode = metadata.mode() & 0o777;
        if mode != 0o700 {
            return Err(ProductionCatalogError::RootUnavailable(root.to_path_buf()));
        }
        #[cfg(target_os = "linux")]
        if current_uid().is_some_and(|uid| uid != metadata.uid()) {
            return Err(ProductionCatalogError::RootUnavailable(root.to_path_buf()));
        }
    }
    let canonical = fs::canonicalize(root)
        .map_err(|_| ProductionCatalogError::RootUnavailable(root.to_path_buf()))?;
    if canonical != root {
        return Err(ProductionCatalogError::SymlinkComponent(root.to_path_buf()));
    }
    Ok(())
}

#[cfg(target_os = "linux")]
fn current_uid() -> Option<u32> {
    fs::read_to_string("/proc/self/status")
        .ok()?
        .lines()
        .find_map(|line| line.strip_prefix("Uid:")?.split_whitespace().next())
        .and_then(|value| value.parse().ok())
}

fn reject_symlinks(path: &Path) -> Result<(), ProductionCatalogError> {
    let mut current = PathBuf::new();
    for component in path.components() {
        match component {
            Component::RootDir => current.push(Path::new("/")),
            Component::Prefix(prefix) => current.push(prefix.as_os_str()),
            Component::Normal(component) => current.push(component),
            Component::CurDir | Component::ParentDir => {
                return Err(ProductionCatalogError::TraversalLocation(path.to_path_buf()));
            }
        }
        match fs::symlink_metadata(&current) {
            Ok(metadata) if metadata.file_type().is_symlink() => {
                return Err(ProductionCatalogError::SymlinkComponent(current));
            }
            Ok(_) => {}
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => break,
            Err(_) => return Err(ProductionCatalogError::RootUnavailable(path.to_path_buf())),
        }
    }
    Ok(())
}

fn derive_limits(
    databases: &[&crate::config::ProductionDatabase],
) -> Result<AdmissionLimits, ProductionCatalogError> {
    let mut global = 0usize;
    let mut per_database = usize::MAX;
    let mut configured_per_database = None;
    for database in databases {
        let quota = database.quotas.max_inflight;
        if quota == 0 || quota > MAX_ADMISSION_PERMITS {
            return Err(ProductionCatalogError::InvalidQuota);
        }
        if let Some(previous) = configured_per_database {
            if previous != quota {
                return Err(ProductionCatalogError::UnequalPerDatabaseQuotas);
            }
        } else {
            configured_per_database = Some(quota);
        }
        global = global.checked_add(quota).ok_or(ProductionCatalogError::QuotaOverflow)?;
        per_database = per_database.min(quota);
    }
    if global == 0 || global > MAX_ADMISSION_PERMITS || per_database == 0 {
        return Err(ProductionCatalogError::QuotaOverflow);
    }
    Ok(AdmissionLimits::new(global, per_database))
}

fn build_placement(
    config: &ProductionConfig,
) -> Result<ProductionPlacement, ProductionCatalogError> {
    let regions = config
        .placement
        .regions
        .iter()
        .map(|(region, members)| {
            ProductionRegionSeats::try_new(*region, *members)
                .map_err(ProductionCatalogError::Placement)
        })
        .collect::<Result<Vec<_>, _>>()?;
    ProductionPlacement::try_new(
        config.environment.member,
        config.placement.generation,
        config.placement.epoch,
        regions,
    )
    .map_err(ProductionCatalogError::Placement)
}

fn close_with<T>(
    member: &Member,
    error: ProductionCatalogError,
) -> Result<T, ProductionCatalogError> {
    match member.close(Duration::from_secs(30)) {
        Ok(()) => Err(error),
        Err(close_error) => Err(ProductionCatalogError::Cleanup(close_error)),
    }
}

#[cfg(test)]
mod tests {
    use std::fs;
    use std::path::{Path, PathBuf};
    use std::time::Duration;

    #[cfg(unix)]
    use std::os::unix::fs::PermissionsExt as _;

    use terrabase::identity::{DatabaseUlid, MemberUlid, ProjectUlid, TenantUlid};
    use terrabase::member::{AdmissionLimits, DatabaseScope, DatabaseSpec, Member};
    use terrabase::regions::RegionCode;

    use super::{
        DatabaseRouteScope, DatabaseRoutingCatalog, ProductionCatalogBootstrap,
        ProductionCatalogError, ProductionComponent, ProductionComponentError, ProductionPlacement,
        ProductionRegionSeats, close_with, derive_root, normalize_location,
    };

    #[test]
    fn component_refusals_are_precise_and_do_not_use_the_old_global_error() {
        let error = ProductionComponentError {
            component: ProductionComponent::Replication,
            detail: "test seam is absent",
        };
        assert_eq!(error.component.as_str(), "replication");
        assert_eq!(error.to_string(), "replication: test seam is absent");
    }

    fn opened_bootstrap(label: &str) -> (ProductionCatalogBootstrap, PathBuf, DatabaseSpec) {
        let root = std::env::temp_dir()
            .join(format!("terrabase-server-production-{label}-{}", std::process::id()));
        let _ = fs::remove_dir_all(&root);
        fs::create_dir_all(&root).expect("test root");
        #[cfg(unix)]
        fs::set_permissions(&root, fs::Permissions::from_mode(0o700)).expect("root mode");

        let tenant = TenantUlid::generate();
        let project = ProjectUlid::generate();
        let database = DatabaseUlid::generate();
        let scope = DatabaseScope::new("production", tenant, project, database, RegionCode::GRU)
            .expect("scope");
        let spec = DatabaseSpec::scoped(scope, "db");
        let member = Member::open_production(&root, [spec.clone()], AdmissionLimits::new(2, 2))
            .expect("member");
        let routes =
            DatabaseRoutingCatalog::try_new([DatabaseRouteScope::new(tenant, project, database)])
                .expect("routes");
        let local_member = MemberUlid::generate();
        let peer = MemberUlid::generate();
        let placement = ProductionPlacement::try_new(
            local_member,
            1,
            1,
            [ProductionRegionSeats::try_new(RegionCode::GRU, [local_member, peer]).expect("seats")],
        )
        .expect("placement");
        let bootstrap = ProductionCatalogBootstrap {
            root: root.clone(),
            member: Some(member),
            specs: vec![spec.clone()],
            routes,
            placement,
            limits: AdmissionLimits::new(2, 2),
        };
        (bootstrap, root, spec)
    }

    fn reopen(root: &Path, spec: DatabaseSpec) {
        let member = Member::open_production(root, [spec], AdmissionLimits::new(2, 2))
            .expect("reopen after cleanup");
        member.close(Duration::from_secs(1)).expect("close reopened member");
    }

    #[test]
    fn derives_common_owner_root_for_siblings() {
        let root = derive_root(&[
            Path::new("/var/lib/terrabase/db-a").to_path_buf(),
            Path::new("/var/lib/terrabase/db-b").to_path_buf(),
        ])
        .expect("common root");
        assert_eq!(root, Path::new("/var/lib/terrabase"));
    }

    #[test]
    fn rejects_filesystem_root_and_traversal() {
        assert_eq!(
            derive_root(&[
                Path::new("/var/db-a").to_path_buf(),
                Path::new("/srv/db-b").to_path_buf(),
            ]),
            Err(ProductionCatalogError::CommonRootIsFilesystemRoot)
        );
        assert!(matches!(
            normalize_location(Path::new("/var/lib/terrabase/../db")),
            Err(ProductionCatalogError::TraversalLocation(_))
        ));
    }

    #[test]
    fn explicit_close_returns_and_releases_the_durable_member() {
        let (mut bootstrap, root, spec) = opened_bootstrap("explicit-close");
        bootstrap.close(Duration::from_secs(1)).expect("explicit close");
        assert!(bootstrap.member().is_none());
        reopen(&root, spec);
        let _ = fs::remove_dir_all(root);
    }

    #[test]
    fn ownership_transfer_does_not_close_before_runtime_takes_over() {
        let (bootstrap, root, spec) = opened_bootstrap("transfer");
        let parts = bootstrap.into_parts();
        assert_eq!(parts.member.status().lifecycle, terrabase::member::LifecycleState::Serving);
        parts.member.close(Duration::from_secs(1)).expect("runtime close");
        reopen(&root, spec);
        let _ = fs::remove_dir_all(root);
    }

    #[test]
    fn post_open_cleanup_closes_member_and_drop_is_bounded() {
        let (bootstrap, root, spec) = opened_bootstrap("post-open");
        let parts = bootstrap.into_parts();
        let result: Result<(), ProductionCatalogError> =
            close_with(&parts.member, ProductionCatalogError::CatalogMismatch);
        assert_eq!(result, Err(ProductionCatalogError::CatalogMismatch));
        reopen(&root, spec.clone());
        let _ = fs::remove_dir_all(&root);
        let (bootstrap, root, spec) = opened_bootstrap("drop");
        assert!(bootstrap.member().is_some());
        drop(bootstrap);
        reopen(&root, spec);
        let _ = fs::remove_dir_all(root);
    }
}
