//! Fail-closed production catalog bootstrap.
//!
//! This module binds the typed server configuration to one root-owned
//! [`terrabase::member::Member`] catalog and the cluster routing and
//! placement catalogs. It deliberately stops before transport, key, or
//! readiness setup: constructing this value proves only that storage and the
//! static production catalogs agree.

use std::collections::{BTreeMap, BTreeSet};
use std::fs;
use std::path::{Component, Path, PathBuf};
use std::sync::Arc;
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

use crate::config::{ProductionConfig, ProductionDatabase};

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
    /// Startup failed and the opened catalog could not be durably closed.
    Cleanup {
        /// The original startup refusal.
        startup: Box<ProductionStartupError>,
        /// The durable storage cleanup failure.
        storage: MemberError,
    },
}

impl std::fmt::Display for ProductionStartupError {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::Catalog(error) => write!(formatter, "production catalog: {error}"),
            Self::Transport(error) => write!(formatter, "production transport: {error}"),
            Self::Member(error) => write!(formatter, "production Member: {error:?}"),
            Self::Component(error) => write!(formatter, "production component: {error}"),
            Self::Cleanup { startup, storage } => {
                write!(formatter, "{startup}; production catalog cleanup failed: {storage:?}")
            }
        }
    }
}

impl std::error::Error for ProductionStartupError {}

impl ProductionStartupError {
    /// Keeps both the startup refusal and any durable shutdown failure.
    #[must_use]
    pub(crate) fn with_storage_cleanup(self, cleanup: Result<(), MemberError>) -> Self {
        match cleanup {
            Ok(()) => self,
            Err(storage) => Self::Cleanup { startup: Box::new(self), storage },
        }
    }
}

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
        let CatalogInputs { enabled, locations, root, limits } = catalog_inputs(config)?;
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

        let key_config =
            config.keys.as_ref().ok_or(ProductionCatalogError::EncryptionUnavailable)?;
        let provider = terrabase::encryption::OciVaultKeyDelivery::new(
            key_config.socket.clone(),
            key_config.orchestrator_uid,
            key_config.socket_gid,
        )
        .map_err(|_| ProductionCatalogError::EncryptionUnavailable)?;
        let mut keys: BTreeMap<
            terrabase::member::DatabaseId,
            Arc<dyn terrabase::encryption::DatabaseKeySource>,
        > = BTreeMap::new();
        for database in config.databases.iter().filter(|database| database.enabled) {
            let scope = terrabase::encryption::KeyScope::new_with_project(
                config.environment.name.as_str(),
                database.tenant,
                database.project,
                database.database,
                terrabase::encryption::KeyPurpose::Data,
            )
            .map_err(|_| ProductionCatalogError::EncryptionUnavailable)?;
            let metadata =
                terrabase::encryption::KeyVersionMetadata::new(scope, database.encryption_key);
            let source = terrabase::encryption::OciVaultDatabaseKeys::new(
                provider.clone(),
                metadata,
                database.region,
            )
            .map_err(|_| ProductionCatalogError::EncryptionUnavailable)?;
            keys.insert(database.database.into(), Arc::new(source));
        }
        let member = Member::open_production(&root, specs.clone(), limits, keys)
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

        let member_databases = member.database_ids().collect::<BTreeSet<_>>();
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

struct CatalogInputs<'a> {
    enabled: Vec<&'a ProductionDatabase>,
    locations: Vec<PathBuf>,
    root: PathBuf,
    limits: AdmissionLimits,
}

fn catalog_inputs(config: &ProductionConfig) -> Result<CatalogInputs<'_>, ProductionCatalogError> {
    let enabled = config.databases.iter().filter(|database| database.enabled).collect::<Vec<_>>();
    if enabled.is_empty() && config.member_admission.is_none() {
        return Err(ProductionCatalogError::EmptyCatalog);
    }
    let locations = enabled
        .iter()
        .map(|database| normalize_location(&database.location))
        .collect::<Result<Vec<_>, _>>()?;
    let root = if let Some(admission) = &config.member_admission {
        normalize_location(&admission.data_root)?
    } else {
        derive_root(&locations)?
    };
    ensure_root(&root)?;
    let limits = if enabled.is_empty() {
        let bound = config.transport.max_inflight;
        if bound == 0 || bound > MAX_ADMISSION_PERMITS {
            return Err(ProductionCatalogError::InvalidQuota);
        }
        AdmissionLimits::new(bound, bound)
    } else {
        derive_limits(&enabled)?
    };
    Ok(CatalogInputs { enabled, locations, root, limits })
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
    /// Required database key delivery or authorization is unavailable.
    EncryptionUnavailable,
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
            Self::EncryptionUnavailable => {
                "production database encryption key delivery is unavailable"
            }
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
pub(crate) mod tests {
    use std::fs;
    use std::path::{Path, PathBuf};
    use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering};
    use std::sync::{Arc, Mutex};
    use std::time::Duration;

    #[cfg(unix)]
    use std::os::unix::fs::PermissionsExt as _;

    use rcgen::{
        BasicConstraints, CertificateParams, ExtendedKeyUsagePurpose, Ia5String, IsCa, KeyPair,
        KeyUsagePurpose, SanType,
    };
    use rustls::pki_types::{CertificateDer, PrivateKeyDer, PrivatePkcs8KeyDer};
    use std::net::{Ipv4Addr, UdpSocket};
    use terrabase::identity::{ClusterUlid, DatabaseUlid, MemberUlid, ProjectUlid, TenantUlid};
    use terrabase::member::{
        AcceptedDatabaseRole, AdmissionLimits, DatabaseRole, DatabaseRoleAuthority, DatabaseScope,
        DatabaseSpec, Member,
    };
    use terrabase::regions::RegionCode;
    use terrabase_cluster::production::{
        MemberWorkloadIdentity, ProductionDataPlane, ProductionDataPlaneDrain,
        ProductionDataPlaneError, ProductionDataPlaneFacts, ProductionMemberError,
        ProductionMemberRuntime, ProductionPeerAdmission, ProductionQuicEndpoint, ProductionRegion,
        ProductionTlsMaterial, ProductionTransportConfig, ProductionTransportLimits,
    };

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
        let spec = DatabaseSpec::scoped(scope.clone(), "db");
        let member = Member::open_production(
            &root,
            [spec.clone()],
            AdmissionLimits::new(2, 2),
            crate::test_keys::storage_keys(&scope),
        )
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
        let keys = crate::test_keys::storage_keys(spec.scope().unwrap());
        let member = Member::open_production(root, [spec], AdmissionLimits::new(2, 2), keys)
            .expect("reopen after cleanup");
        member.close(Duration::from_secs(1)).expect("close reopened member");
    }

    struct Writer;

    impl DatabaseRoleAuthority for Writer {
        fn accepted_role(&self, _: &terrabase::member::DatabaseId) -> Option<AcceptedDatabaseRole> {
            Some(AcceptedDatabaseRole { epoch: 1, role: DatabaseRole::Writer })
        }
    }

    struct AsyncDrainPlane {
        member: Member,
        pending: AtomicBool,
        lease: Mutex<Option<terrabase::member::DatabaseLease>>,
        completed: Arc<AtomicBool>,
        calls: Arc<AtomicUsize>,
        started: Arc<tokio::sync::Notify>,
    }

    impl ProductionDataPlane for AsyncDrainPlane {
        fn facts(&self) -> ProductionDataPlaneFacts {
            ProductionDataPlaneFacts::default()
        }

        fn begin_drain(&self) {}

        fn drain(&self, _: Duration) -> ProductionDataPlaneDrain<'_> {
            Box::pin(async move {
                self.calls.fetch_add(1, Ordering::AcqRel);
                self.started.notify_one();
                tokio::task::yield_now().await;
                if self.pending.load(Ordering::Acquire) {
                    return std::future::pending().await;
                }
                drop(self.lease.lock().expect("drain lease lock").take());
                self.completed.store(true, Ordering::Release);
                Ok(())
            })
        }
    }

    fn transport_pair(
        placement: &ProductionPlacement,
    ) -> (ProductionQuicEndpoint, ProductionQuicEndpoint) {
        let local = placement.local_member();
        let peer = placement.remote_members()[0];
        let cluster = ClusterUlid::generate();
        let local_identity =
            MemberWorkloadIdentity::try_new("production", &cluster.to_text(), &local.to_text())
                .expect("local identity");
        let peer_identity =
            MemberWorkloadIdentity::try_new("production", &cluster.to_text(), &peer.to_text())
                .expect("peer identity");
        let local_socket = UdpSocket::bind((Ipv4Addr::LOCALHOST, 0)).expect("local socket");
        let peer_socket = UdpSocket::bind((Ipv4Addr::LOCALHOST, 0)).expect("peer socket");
        let local_addr = local_socket.local_addr().expect("local address");
        let peer_addr = peer_socket.local_addr().expect("peer address");
        drop((local_socket, peer_socket));
        let issuer_key = KeyPair::generate().expect("issuer key");
        let mut issuer_params = CertificateParams::new(Vec::new()).expect("issuer params");
        issuer_params.is_ca = IsCa::Ca(BasicConstraints::Unconstrained);
        issuer_params.key_usages =
            vec![KeyUsagePurpose::DigitalSignature, KeyUsagePurpose::KeyCertSign];
        let issuer = issuer_params.self_signed(&issuer_key).expect("issuer");
        let anchor = CertificateDer::from(issuer.der().to_vec());
        let tls = |identity: &MemberWorkloadIdentity, name: &str| {
            let key = KeyPair::generate().expect("member key");
            let mut params = CertificateParams::new(vec![name.to_owned()]).expect("member params");
            params
                .subject_alt_names
                .push(SanType::URI(Ia5String::try_from(identity.as_str()).expect("SAN")));
            params.extended_key_usages =
                vec![ExtendedKeyUsagePurpose::ServerAuth, ExtendedKeyUsagePurpose::ClientAuth];
            let certificate = params.signed_by(&key, &issuer, &issuer_key).expect("member cert");
            ProductionTlsMaterial::try_new(
                vec![CertificateDer::from(certificate.der().to_vec())],
                PrivateKeyDer::Pkcs8(PrivatePkcs8KeyDer::from(key.serialize_der())),
                vec![anchor.clone()],
            )
            .expect("member TLS")
        };
        let limits = ProductionTransportLimits::try_new(
            16 * 1024 * 1024,
            8,
            8,
            128 * 1024 * 1024,
            Duration::from_secs(5),
            Duration::from_secs(5),
            Duration::from_secs(10),
            Duration::from_millis(20),
        )
        .expect("transport limits");
        let local_config = ProductionTransportConfig::try_new(
            local_addr,
            local_identity.clone(),
            ProductionRegion::Gru,
            placement.generation(),
            placement.epoch(),
            vec![
                ProductionPeerAdmission::try_new(
                    peer_identity.clone(),
                    ProductionRegion::Gru,
                    peer_addr,
                    "peer.internal",
                )
                .expect("peer admission"),
            ],
            limits.clone(),
        )
        .expect("local transport");
        let peer_config = ProductionTransportConfig::try_new(
            peer_addr,
            peer_identity.clone(),
            ProductionRegion::Gru,
            placement.generation(),
            placement.epoch(),
            vec![
                ProductionPeerAdmission::try_new(
                    local_identity.clone(),
                    ProductionRegion::Gru,
                    local_addr,
                    "local.internal",
                )
                .expect("local admission"),
            ],
            limits,
        )
        .expect("peer transport");
        (
            ProductionQuicEndpoint::bind(local_config, tls(&local_identity, "local.internal"))
                .expect("local endpoint"),
            ProductionQuicEndpoint::bind(peer_config, tls(&peer_identity, "peer.internal"))
                .expect("peer endpoint"),
        )
    }

    fn runtime_with_async_drain(
        label: &str,
        pending: bool,
    ) -> (ProductionMemberRuntime, PathBuf, DatabaseSpec, Arc<AsyncDrainPlane>) {
        let (bootstrap, root, spec) = opened_bootstrap(label);
        let parts = bootstrap.into_parts();
        let placement = parts.placement.clone();
        let database = parts.member.database_ids().next().expect("database");
        parts.member.accept_database_role(&database, &Writer).expect("writer role");
        let lease = parts.member.acquire(&database).expect("database lease");
        let routes = parts.routes.clone();
        let transport = transport_pair(&placement).0;
        let member = parts.member.clone();
        let runtime = ProductionMemberRuntime::try_new(placement, routes, parts.member, transport)
            .expect("production member runtime");
        let completed = Arc::new(AtomicBool::new(false));
        let calls = Arc::new(AtomicUsize::new(0));
        let started = Arc::new(tokio::sync::Notify::new());
        let plane = Arc::new(AsyncDrainPlane {
            member,
            pending: AtomicBool::new(pending),
            lease: Mutex::new(Some(lease)),
            completed,
            calls,
            started,
        });
        runtime.attach_data_plane(plane.clone()).expect("data plane attachment");
        (runtime, root, spec, plane)
    }

    pub(crate) fn runtime_for_readiness(
        label: &str,
    ) -> (ProductionMemberRuntime, PathBuf, DatabaseSpec) {
        let (runtime, root, spec, _) = runtime_with_async_drain(label, false);
        (runtime, root, spec)
    }

    struct AcceptedMembership(super::ProductionPlacement);

    impl terrabase_cluster::production::DurableMembershipSource for AcceptedMembership {
        fn load_admitted_placement(
            &self,
        ) -> Result<super::ProductionPlacement, terrabase_cluster::production::MembershipSourceError>
        {
            Ok(self.0.clone())
        }
    }

    struct AcceptedAssignment(terrabase_cluster::wal_route::WalAssignment);

    impl terrabase_cluster::wal_route::DurableWalAssignmentSource for AcceptedAssignment {
        fn load_assignment(
            &self,
            _: DatabaseRouteScope,
        ) -> Result<
            terrabase_cluster::wal_route::WalAssignment,
            terrabase_cluster::wal_route::WalAssignmentError,
        > {
            Ok(self.0.clone())
        }
    }

    #[tokio::test]
    async fn epoch_retirement_fences_runtime_arcs_and_allows_a_new_epoch_after_drain() {
        let (runtime, root, spec) = runtime_for_readiness("epoch-retirement-barrier");
        let owner = spec.scope().expect("scope").owner();
        let route = DatabaseRouteScope::new(owner.tenant(), owner.project(), owner.database());
        let runtime = Arc::new(runtime);

        let config = runtime.transport().config();
        let local =
            MemberUlid::from_text(config.local_identity().member().as_str()).expect("local member");
        let peer = MemberUlid::from_text(
            config.peers().next().expect("peer").identity().member().as_str(),
        )
        .expect("peer member");
        let placement = super::ProductionPlacement::try_new(
            local,
            runtime.facts().placement_generation,
            runtime.facts().replica_set_epoch,
            [super::ProductionRegionSeats::try_new(RegionCode::GRU, [local, peer]).expect("seats")],
        )
        .expect("placement");
        runtime.admit_membership(&AcceptedMembership(placement)).expect("membership");
        let binding = runtime
            .bind_wal_replica(
                route,
                &AcceptedAssignment(terrabase_cluster::wal_route::WalAssignment {
                    scope: route,
                    generation: runtime.facts().placement_generation,
                    epoch: runtime.facts().replica_set_epoch,
                    writer: config.local_identity().clone(),
                    follower: config.peers().next().expect("peer").identity().clone(),
                }),
            )
            .expect("wal binding");
        assert!(runtime.acquire_replication(&route).is_ok());

        runtime.begin_epoch_retirement();
        runtime.begin_epoch_retirement();
        assert!(runtime.acquire_replication(&route).is_err());
        assert!(binding.is_fenced());

        let runtime = Arc::try_unwrap(runtime).unwrap_or_else(|_| panic!("runtime Arc retained"));
        let retired = runtime.retire_epoch().await;
        assert!(retired.data_plane.is_ok());
        let database = retired.member.database_ids().next().expect("database");
        retired.member.close_database(&database).expect("close idle old-epoch engine");
        retired
            .member
            .accept_database_role(&database, &DatabaseRoleAuthorityForEpoch(2))
            .expect("successor role");
        let lease = retired.member.acquire(&database).expect("successor member remains usable");
        drop(lease);
        retired.member.close(Duration::from_secs(1)).expect("close successor");
        reopen(&root, spec);
        fs::remove_dir_all(root).expect("cleanup");
    }

    struct DatabaseRoleAuthorityForEpoch(u64);

    impl DatabaseRoleAuthority for DatabaseRoleAuthorityForEpoch {
        fn accepted_role(&self, _: &terrabase::member::DatabaseId) -> Option<AcceptedDatabaseRole> {
            Some(AcceptedDatabaseRole { epoch: self.0, role: DatabaseRole::Writer })
        }
    }

    pub(crate) struct NoDatabaseAssignments;

    impl terrabase_cluster::wal_route::DurableWalAssignmentSource for NoDatabaseAssignments {
        fn load_assignment(
            &self,
            _: terrabase_cluster::production::DatabaseRouteScope,
        ) -> Result<
            terrabase_cluster::wal_route::WalAssignment,
            terrabase_cluster::wal_route::WalAssignmentError,
        > {
            panic!("empty database startup must not invent assignments")
        }
    }

    pub(crate) struct NoControlFrames;

    impl crate::replication_peers::PeerControlDispatcher for NoControlFrames {
        fn dispatch<'a>(
            &'a self,
            _: terrabase_member_transport::production::ProductionConnection,
            _: Vec<u8>,
        ) -> std::pin::Pin<Box<dyn std::future::Future<Output = Result<bool, String>> + Send + 'a>>
        {
            Box::pin(async { Ok(false) })
        }
    }

    #[tokio::test]
    async fn production_data_lifecycle_retains_one_owner_and_refuses_restart() {
        use crate::production_data_runtime::ProductionDataLifecycle;
        let (runtime, root, spec, _) = runtime_with_async_drain("data-lifecycle-owner", false);
        let runtime = Arc::new(runtime);
        let progress = Arc::new(crate::replication_runtime::ReplicationProgress::default());
        let lifecycle = Arc::new(ProductionDataLifecycle::new(
            progress,
            Arc::new(crate::admitted_databases::AdmittedDatabaseRegistry::new(Arc::new(|| false))),
        ));
        let start = || {
            lifecycle.start(
                runtime.clone(),
                &NoDatabaseAssignments,
                Vec::new(),
                Arc::new(NoControlFrames),
            )
        };
        let (first, second) = tokio::join!(start(), start());
        assert_ne!(first.is_ok(), second.is_ok(), "exactly one startup is accepted");
        let data = lifecycle.get().expect("retained runtime");
        data.ensure_running().expect("worker owner is running");
        let (first, second) = tokio::join!(lifecycle.drain(), lifecycle.drain());
        first.expect("first drain");
        second.expect("shared drain");
        assert!(std::ptr::eq(data, lifecycle.get().expect("owner remains installed")));
        assert!(data.ensure_running().is_err());
        assert!(start().await.is_err(), "shutdown cannot start a new receiver");
        assert!(runtime.drain().await.storage.is_ok());
        reopen(&root, spec);
        fs::remove_dir_all(root).unwrap();
    }

    #[tokio::test]
    async fn production_data_lifecycle_empty_drain_fences_later_startup() {
        use crate::production_data_runtime::ProductionDataLifecycle;
        let (runtime, root, spec, _) = runtime_with_async_drain("data-lifecycle-empty", false);
        let runtime = Arc::new(runtime);
        let progress = Arc::new(crate::replication_runtime::ReplicationProgress::default());
        let lifecycle = ProductionDataLifecycle::new(
            progress,
            Arc::new(crate::admitted_databases::AdmittedDatabaseRegistry::new(Arc::new(|| false))),
        );
        lifecycle.drain().await.expect("empty drain");
        assert!(lifecycle.is_draining());
        assert!(
            lifecycle
                .start(
                    runtime.clone(),
                    &NoDatabaseAssignments,
                    Vec::new(),
                    Arc::new(NoControlFrames),
                )
                .await
                .is_err()
        );
        assert!(lifecycle.get().is_none());
        assert!(runtime.drain().await.storage.is_ok());
        reopen(&root, spec);
        fs::remove_dir_all(root).unwrap();
    }

    #[tokio::test]
    async fn production_member_drain_awaits_data_plane_before_closing_storage() {
        let (runtime, root, spec, completed) =
            runtime_with_async_drain("async-member-drain", false);
        let report = runtime.drain().await;
        assert!(completed.completed.load(Ordering::Acquire));
        assert!(report.data_plane.is_ok());
        assert!(report.storage.is_ok(), "storage close: {:?}", report.storage);
        reopen(&root, spec);
        let _ = fs::remove_dir_all(root);
    }

    #[tokio::test]
    async fn production_member_retirement_awaits_data_plane_before_returning_member() {
        let (runtime, root, spec, completed) =
            runtime_with_async_drain("async-member-retire", false);
        let retirement = runtime.retire_epoch().await;
        assert!(completed.completed.load(Ordering::Acquire));
        assert!(retirement.data_plane.is_ok());
        retirement.member.close(Duration::from_secs(1)).expect("retired member close");
        reopen(&root, spec);
        let _ = fs::remove_dir_all(root);
    }

    #[tokio::test]
    async fn production_member_drain_timeout_retains_lease_and_reports_failed_close() {
        let (runtime, root, spec, plane) = runtime_with_async_drain("async-member-timeout", true);
        let report = runtime.drain().await;
        assert_eq!(report.data_plane, Err(ProductionDataPlaneError::Timeout));
        assert!(!plane.completed.load(Ordering::Acquire));
        assert!(plane.lease.lock().unwrap().is_some());
        assert!(report.storage.is_err(), "unfinished drain released storage");
        drop(plane.lease.lock().unwrap().take());
        plane.member.close(Duration::from_secs(1)).expect("close after lease released");
        reopen(&root, spec);
        fs::remove_dir_all(root).unwrap();
    }

    #[tokio::test]
    async fn production_member_concurrent_drains_run_data_plane_once() {
        let (runtime, root, spec, plane) =
            runtime_with_async_drain("async-member-concurrent", false);
        let runtime = Arc::new(runtime);
        let other = Arc::clone(&runtime);
        let (first, second) = tokio::join!(runtime.drain(), other.drain());
        assert!(first.storage.is_ok() && second.storage.is_ok());
        assert!(first.data_plane.is_ok() && second.data_plane.is_ok());
        assert_eq!(plane.calls.load(Ordering::Acquire), 1);
        assert!(plane.lease.lock().unwrap().is_none());
        reopen(&root, spec);
        fs::remove_dir_all(root).unwrap();
    }

    #[tokio::test]
    async fn production_member_drain_timeout_retries_and_closes_storage() {
        let (runtime, root, spec, plane) =
            runtime_with_async_drain("async-member-timeout-retry", true);
        let first = runtime.drain().await;
        assert_eq!(first.data_plane, Err(ProductionDataPlaneError::Timeout));
        assert!(first.storage.is_err());
        plane.pending.store(false, Ordering::Release);
        let second = runtime.drain().await;
        assert!(second.data_plane.is_ok() && second.storage.is_ok());
        assert!(plane.lease.lock().unwrap().is_none());
        assert_eq!(plane.calls.load(Ordering::Acquire), 2);
        reopen(&root, spec);
        fs::remove_dir_all(root).unwrap();
    }

    #[tokio::test]
    async fn production_member_cancelled_drain_can_retry() {
        let (runtime, root, spec, plane) =
            runtime_with_async_drain("async-member-cancel-retry", true);
        {
            let drain = runtime.drain();
            tokio::pin!(drain);
            tokio::select! {
                report = &mut drain => panic!("drain completed before cancellation: {report:?}"),
                () = plane.started.notified() => {}
            }
        }
        assert!(plane.lease.lock().unwrap().is_some());
        plane.pending.store(false, Ordering::Release);
        let report = runtime.drain().await;
        assert!(report.data_plane.is_ok() && report.storage.is_ok());
        assert!(plane.lease.lock().unwrap().is_none());
        reopen(&root, spec);
        fs::remove_dir_all(root).unwrap();
    }

    #[tokio::test]
    async fn production_member_retirement_rejects_cancelled_normal_drain() {
        let (runtime, root, spec, plane) =
            runtime_with_async_drain("async-member-retire-cancelled", true);
        {
            let drain = runtime.drain();
            tokio::pin!(drain);
            tokio::select! {
                report = &mut drain => panic!("drain completed before cancellation: {report:?}"),
                () = plane.started.notified() => {}
            }
        }
        assert!(plane.lease.lock().unwrap().is_some());
        let retirement = runtime.retire_epoch().await;
        assert_eq!(retirement.data_plane, Err(ProductionDataPlaneError::Persistence));
        drop(plane.lease.lock().unwrap().take());
        retirement.member.close(Duration::from_secs(1)).expect("retired member close");
        reopen(&root, spec);
        fs::remove_dir_all(root).unwrap();
    }

    #[tokio::test]
    async fn production_member_rejects_late_data_plane_attachment() {
        let (runtime, root, spec, plane) =
            runtime_with_async_drain("async-member-late-attach", true);
        let runtime = Arc::new(runtime);
        let started = plane.started.notified();
        tokio::pin!(started);
        let drain = tokio::spawn({
            let runtime = Arc::clone(&runtime);
            async move { runtime.drain().await }
        });
        started.await;
        assert_eq!(
            runtime.attach_data_plane(plane.clone()),
            Err(ProductionMemberError::MembershipMismatch)
        );
        plane.pending.store(false, Ordering::Release);
        let report = drain.await.expect("drain task");
        assert!(report.data_plane.is_ok() && report.storage.is_ok());
        reopen(&root, spec);
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn startup_refusal_preserves_failed_catalog_drain() {
        struct Writer;
        impl terrabase::member::DatabaseRoleAuthority for Writer {
            fn accepted_role(
                &self,
                _: &terrabase::member::DatabaseId,
            ) -> Option<terrabase::member::AcceptedDatabaseRole> {
                Some(terrabase::member::AcceptedDatabaseRole {
                    epoch: 1,
                    role: terrabase::member::DatabaseRole::Writer,
                })
            }
        }
        let (bootstrap, root, spec) = opened_bootstrap("startup-drain-failure");
        let parts = bootstrap.into_parts();
        let database = parts.member.database_ids().next().expect("database");
        parts.member.accept_database_role(&database, &Writer).expect("writer role");
        let lease = parts.member.acquire(&database).expect("active operation");
        let refusal = super::ProductionStartupError::Component(ProductionComponentError {
            component: ProductionComponent::Membership,
            detail: "membership unavailable",
        });
        let error = refusal.clone().with_storage_cleanup(parts.member.close(Duration::ZERO));
        assert_eq!(
            error,
            super::ProductionStartupError::Cleanup {
                startup: Box::new(refusal.clone()),
                storage: terrabase::member::MemberError::DrainTimeout,
            }
        );
        assert!(error.to_string().contains("membership unavailable"));
        assert!(error.to_string().contains("cleanup failed"));
        drop(lease);
        assert_eq!(
            refusal.clone().with_storage_cleanup(parts.member.close(Duration::from_secs(1))),
            refusal
        );
        reopen(&root, spec);
        let _ = fs::remove_dir_all(root);
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
