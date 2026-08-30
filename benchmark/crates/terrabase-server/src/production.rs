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
            Err(error) => return close_with(member, ProductionCatalogError::Routing(error)),
        };
        let placement = match build_placement(config) {
            Ok(placement) => placement,
            Err(error) => return close_with(member, error),
        };

        if enabled
            .iter()
            .any(|database| !placement.regions().any(|entry| entry.region() == database.region))
        {
            return close_with(member, ProductionCatalogError::DatabaseRegionNotPlaced);
        }

        let member_databases = member.database_ids().cloned().collect::<BTreeSet<_>>();
        let route_databases =
            routes.routes().map(|(_, database)| database.clone()).collect::<BTreeSet<_>>();
        if member_databases != route_databases || member.status().databases != routes.len() {
            return close_with(member, ProductionCatalogError::CatalogMismatch);
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
    /// The Member is taken before close is attempted, so the bootstrap Drop
    /// path cannot issue a second close after this method returns. A durable
    /// drain or engine-shutdown failure is returned unchanged.
    ///
    /// # Errors
    ///
    /// Returns the durable [`MemberError`] reported by the root catalog.
    pub fn close(mut self, timeout: Duration) -> Result<(), MemberError> {
        let member = self.member.take().ok_or(MemberError::Draining)?;
        member.close(timeout)
    }

    /// Transfers every owned catalog component to the production runtime.
    ///
    /// This consumes the bootstrap and moves the exact opened [`Member`]; no
    /// clone or second storage authority is created. The returned parts own
    /// the Member and must eventually close it after runtime draining.
    #[must_use]
    pub fn into_parts(mut self) -> ProductionCatalogParts {
        let member = self
            .member
            .take()
            .expect("a live production bootstrap always owns its Member");
        ProductionCatalogParts {
            root: self.root,
            member,
            specs: self.specs,
            routes: self.routes,
            placement: self.placement,
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
    member: Member,
    error: ProductionCatalogError,
) -> Result<T, ProductionCatalogError> {
    match member.close(Duration::from_secs(30)) {
        Ok(()) => Err(error),
        Err(close_error) => Err(ProductionCatalogError::Cleanup(close_error)),
    }
}

#[cfg(test)]
mod tests {
    use std::path::Path;

    use super::{ProductionCatalogError, derive_root, normalize_location};

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
}
