//! Production Member composition boundary.
//!
//! One process owns one local durable Member seat and a catalog of isolated
//! database engines. Remote seats are reached through the authenticated QUIC
//! boundary. This module validates placement, routing, membership admission,
//! and readiness facts. It does not implement the missing replication data
//! plane. Until a durable replication adapter reports every required fact,
//! readiness fails closed.

use std::collections::{BTreeMap, BTreeSet};
use std::ops::Deref;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};
use std::time::Duration;

use terrabase::identity::{DatabaseUlid, MemberUlid, ProjectUlid, TenantUlid};
use terrabase::member::{
    DatabaseId, DatabaseLease, LifecycleState, Member, MemberError as StorageMemberError,
    StorageHealth,
};
use terrabase::{Engine, RegionCode};
use terrabase_member_transport::production::{
    ProductionDrainReport as TransportDrainReport, ProductionQuicEndpoint, ProductionRegion,
    ProductionTransportFacts,
};

/// The maximum selected Regions in the initial production topology.
pub const INITIAL_PRODUCTION_REGION_COUNT: usize = 3;

/// A fully bound database routing scope.
///
/// The TBEP boundary constructs this value from its authenticated and
/// validated scope. No request path or filesystem text enters the catalog.
#[derive(Clone, Copy, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub struct DatabaseRouteScope {
    tenant: TenantUlid,
    project: ProjectUlid,
    database: DatabaseUlid,
}

impl DatabaseRouteScope {
    /// Creates one typed tenant, Project, and database scope.
    #[must_use]
    pub const fn new(tenant: TenantUlid, project: ProjectUlid, database: DatabaseUlid) -> Self {
        Self { tenant, project, database }
    }

    /// Returns the tenant identity.
    #[must_use]
    pub const fn tenant(&self) -> TenantUlid {
        self.tenant
    }

    /// Returns the Project identity.
    #[must_use]
    pub const fn project(&self) -> ProjectUlid {
        self.project
    }

    /// Returns the database identity.
    #[must_use]
    pub const fn database(&self) -> DatabaseUlid {
        self.database
    }

    fn catalog_database_id(&self) -> Result<DatabaseId, ProductionMemberError> {
        DatabaseId::try_new(self.database.to_text())
            .map_err(|_| ProductionMemberError::InvalidRoutingCatalog)
    }
}

/// Immutable routing from bound database scopes to operator-provisioned
/// catalog identities.
///
/// A route stores no path. The root [`Member`] already maps each
/// [`DatabaseId`] to a separately validated location beneath its fixed root.
#[derive(Clone, Debug)]
pub struct DatabaseRoutingCatalog {
    routes: BTreeMap<DatabaseRouteScope, DatabaseId>,
}

impl DatabaseRoutingCatalog {
    /// Creates a routing catalog from typed scopes.
    ///
    /// Each database ULID becomes the opaque root-Member catalog key. The
    /// constructor refuses an empty catalog, duplicate scope, duplicate
    /// database identity, or more than the root Member's catalog ceiling.
    ///
    /// # Errors
    ///
    /// Returns [`ProductionMemberError::InvalidRoutingCatalog`] when any
    /// invariant fails.
    pub fn try_new(
        scopes: impl IntoIterator<Item = DatabaseRouteScope>,
    ) -> Result<Self, ProductionMemberError> {
        let mut routes = BTreeMap::new();
        let mut databases = BTreeSet::new();
        for (index, scope) in scopes.into_iter().enumerate() {
            if index >= terrabase::member::MAX_DATABASES {
                return Err(ProductionMemberError::InvalidRoutingCatalog);
            }
            let database = scope.catalog_database_id()?;
            if !databases.insert(database.clone()) || routes.insert(scope, database).is_some() {
                return Err(ProductionMemberError::InvalidRoutingCatalog);
            }
        }
        if routes.is_empty() {
            return Err(ProductionMemberError::InvalidRoutingCatalog);
        }
        Ok(Self { routes })
    }

    /// Resolves a typed bound scope to its opaque root-Member database key.
    #[must_use]
    pub fn resolve(&self, scope: &DatabaseRouteScope) -> Option<&DatabaseId> {
        self.routes.get(scope)
    }

    /// Returns the number of admitted database routes.
    #[must_use]
    pub fn len(&self) -> usize {
        self.routes.len()
    }

    /// Reports whether the catalog is empty.
    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.routes.is_empty()
    }

    /// Returns routes in canonical scope order.
    pub fn routes(&self) -> impl Iterator<Item = (&DatabaseRouteScope, &DatabaseId)> {
        self.routes.iter()
    }
}

/// The two active Member seats of one selected Region.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct ProductionRegionSeats {
    region: RegionCode,
    members: [MemberUlid; 2],
}

impl ProductionRegionSeats {
    /// Creates an exact RF=2 Region entry.
    ///
    /// # Errors
    ///
    /// Returns [`ProductionMemberError::InvalidPlacement`] when the Region
    /// is outside the initial catalog or both seats name the same Member.
    pub fn try_new(
        region: RegionCode,
        members: [MemberUlid; 2],
    ) -> Result<Self, ProductionMemberError> {
        if !is_initial_region(region) || members[0] == members[1] {
            return Err(ProductionMemberError::InvalidPlacement);
        }
        Ok(Self { region, members })
    }

    /// Returns the canonical product Region code.
    #[must_use]
    pub const fn region(&self) -> RegionCode {
        self.region
    }

    /// Returns the two active seats.
    #[must_use]
    pub const fn members(&self) -> &[MemberUlid; 2] {
        &self.members
    }
}

/// One validated placement generation and replica-set epoch.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ProductionPlacement {
    local_member: MemberUlid,
    generation: u64,
    epoch: u64,
    regions: BTreeMap<RegionCode, [MemberUlid; 2]>,
}

impl ProductionPlacement {
    /// Validates a one-, two-, or three-Region RF=2 placement.
    ///
    /// The local Member must occupy exactly one seat. Every Member identity
    /// is unique across the full mesh. Infrastructure fault-domain proof
    /// remains an Orchestrator qualification fact and does not enter the
    /// engine placement contract as a provider identifier.
    ///
    /// # Errors
    ///
    /// Returns [`ProductionMemberError::InvalidPlacement`] for version zero,
    /// an invalid Region count, duplicate Region or Member, or a local Member
    /// that is absent or repeated.
    pub fn try_new(
        local_member: MemberUlid,
        generation: u64,
        epoch: u64,
        regions: impl IntoIterator<Item = ProductionRegionSeats>,
    ) -> Result<Self, ProductionMemberError> {
        if generation == 0 || epoch == 0 {
            return Err(ProductionMemberError::InvalidPlacement);
        }
        let mut selected = BTreeMap::new();
        let mut members = BTreeSet::new();
        let mut local_seats = 0_usize;
        for entry in regions {
            if selected.contains_key(&entry.region) {
                return Err(ProductionMemberError::InvalidPlacement);
            }
            for member in entry.members {
                if !members.insert(member) {
                    return Err(ProductionMemberError::InvalidPlacement);
                }
                if member == local_member {
                    local_seats += 1;
                }
            }
            selected.insert(entry.region, entry.members);
        }
        if selected.is_empty()
            || selected.len() > INITIAL_PRODUCTION_REGION_COUNT
            || local_seats != 1
        {
            return Err(ProductionMemberError::InvalidPlacement);
        }
        Ok(Self { local_member, generation, epoch, regions: selected })
    }

    /// Returns the one durable Member seat owned by this process.
    #[must_use]
    pub const fn local_member(&self) -> MemberUlid {
        self.local_member
    }

    /// Returns the placement generation.
    #[must_use]
    pub const fn generation(&self) -> u64 {
        self.generation
    }

    /// Returns the replica-set epoch.
    #[must_use]
    pub const fn epoch(&self) -> u64 {
        self.epoch
    }

    /// Returns selected Region entries in canonical Region order.
    pub fn regions(&self) -> impl Iterator<Item = ProductionRegionSeats> + '_ {
        self.regions
            .iter()
            .map(|(region, members)| ProductionRegionSeats { region: *region, members: *members })
    }

    /// Returns the local Member's Region.
    #[must_use]
    pub fn local_region(&self) -> RegionCode {
        self.regions
            .iter()
            .find_map(|(region, members)| members.contains(&self.local_member).then_some(*region))
            .expect("validated placement contains the local Member exactly once")
    }

    /// Returns every remote peer in canonical Region and seat order.
    #[must_use]
    pub fn remote_members(&self) -> Vec<MemberUlid> {
        self.regions
            .values()
            .flat_map(|members| members.iter().copied())
            .filter(|member| *member != self.local_member)
            .collect()
    }
}

fn is_initial_region(region: RegionCode) -> bool {
    matches!(region, RegionCode::GRU | RegionCode::SFO | RegionCode::FRA)
}

fn transport_region(region: RegionCode) -> ProductionRegion {
    match region {
        RegionCode::GRU => ProductionRegion::Gru,
        RegionCode::SFO => ProductionRegion::Sfo,
        RegionCode::FRA => ProductionRegion::Fra,
        _ => unreachable!("validated production placement contains only the initial catalog"),
    }
}

/// Durable membership authority consumed at startup or epoch change.
///
/// Implementations must load an accepted record from the coordinated durable
/// membership lane. A NATS lease, route advertisement, health opinion, or
/// elapsed timeout is not an implementation of this trait.
pub trait DurableMembershipSource: Send + Sync {
    /// Loads the complete durably admitted placement.
    ///
    /// # Errors
    ///
    /// Returns a typed source failure instead of inventing an empty or newer
    /// membership.
    fn load_admitted_placement(&self) -> Result<ProductionPlacement, MembershipSourceError>;
}

/// Failure to load an authoritative membership record.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum MembershipSourceError {
    /// Durable membership storage could not be read.
    Unavailable,
    /// The stored record or its authority proof was invalid.
    InvalidProof,
}

/// Facts that only the durable replication and full serving composition can
/// supply.
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct ProductionDataPlaneFacts {
    /// Every configured database location is admitted in this epoch.
    pub databases_admitted: bool,
    /// The local RF=2 peer was reconciled before counter allocation.
    pub local_rf2_reconciled: bool,
    /// Current and catch-up replication use the durable engine state machine.
    pub replication_serving: bool,
    /// Anti-entropy, Merkle repair, and snapshot fallback are serving.
    pub repair_serving: bool,
    /// Stored, applied, and verified progress comes from durable facts.
    pub durable_progress_available: bool,
    /// Reactive and usage publication order comes from each engine journal.
    pub publication_journal_authoritative: bool,
    /// Every required engine location uses the accepted production encryption
    /// provider and key lifecycle.
    pub storage_encrypted: bool,
    /// One TBEP Member host serves every required plane against these engines.
    pub complete_member_host: bool,
    /// The private bounded metrics listener is serving.
    pub metrics_serving: bool,
    /// Shutdown can persist every required replication and publication
    /// frontier before closing storage.
    pub shutdown_frontiers_persistable: bool,
}

/// Adapter from authenticated Member frames to durable replication and the
/// complete serving host.
///
/// No implementation ships in this module. The missing adapter is kept as a
/// readiness blocker rather than replaced with the in-process qualification
/// host.
pub trait ProductionDataPlane: Send + Sync {
    /// Returns current durable integration facts.
    fn facts(&self) -> ProductionDataPlaneFacts;

    /// Stops admitting new replication and serving work.
    fn begin_drain(&self);

    /// Persists frontiers and drains bounded work.
    ///
    /// # Errors
    ///
    /// Returns a typed error when the deadline expires or a required durable
    /// frontier cannot be persisted.
    fn drain(&self, timeout: Duration) -> Result<(), ProductionDataPlaneError>;
}

/// Failure while draining the durable data-plane adapter.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ProductionDataPlaneError {
    /// The bounded deadline elapsed.
    Timeout,
    /// A required durable frontier could not be persisted.
    Persistence,
}

/// One bounded reason the Member cannot accept production traffic.
#[derive(Clone, Copy, Debug, Eq, Ord, PartialEq, PartialOrd)]
pub enum ProductionReadinessBlocker {
    /// Shutdown has started.
    Draining,
    /// The root multi-database catalog is not serving healthy storage.
    CatalogNotServing,
    /// No coordinated durable membership record admitted this exact version.
    MembershipNotAdmitted,
    /// The production transport is not accepting authenticated sessions.
    TransportNotAccepting,
    /// At least one admitted peer lacks an authenticated application session.
    PeerSessionsMissing,
    /// The durable replication and full-host adapter is absent.
    DataPlaneMissing,
    /// At least one database location is not admitted in the data plane.
    DatabaseLocationsNotAdmitted,
    /// The local RF=2 reconciliation boundary has not passed.
    LocalRf2NotReconciled,
    /// Durable live and catch-up replication is not serving.
    ReplicationNotServing,
    /// Repair and snapshot fallback are not serving.
    RepairNotServing,
    /// Durable stored, applied, and verified progress is unavailable.
    DurableProgressUnavailable,
    /// Publication still has another in-memory authority.
    PublicationJournalNotAuthoritative,
    /// Required engine storage is not under the accepted encryption contract.
    StorageEncryptionUnavailable,
    /// The single Member host does not serve every required TBEP plane.
    CompleteMemberHostUnavailable,
    /// The required bounded metrics listener is unavailable.
    MetricsUnavailable,
    /// Shutdown cannot yet persist every required frontier.
    ShutdownFrontiersUnavailable,
}

/// Readiness and admission facts consumable by the TBEP Member host.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ProductionMemberFacts {
    /// True only when no blocker remains.
    pub ready: bool,
    /// Stable, bounded blocker codes.
    pub blockers: Vec<ProductionReadinessBlocker>,
    /// The exact admitted placement generation.
    pub placement_generation: u64,
    /// The exact admitted replica-set epoch.
    pub replica_set_epoch: u64,
    /// Configured isolated database locations.
    pub databases: usize,
    /// Current authenticated transport facts.
    pub transport: ProductionTransportFacts,
    /// Current durable data-plane facts.
    pub data_plane: ProductionDataPlaneFacts,
}

/// One production Member process around one local durable seat.
pub struct ProductionMemberRuntime {
    placement: ProductionPlacement,
    routes: DatabaseRoutingCatalog,
    member: Member,
    transport: ProductionQuicEndpoint,
    membership_admitted: AtomicBool,
    data_plane: Mutex<Option<Arc<dyn ProductionDataPlane>>>,
    draining: AtomicBool,
}

impl ProductionMemberRuntime {
    /// Assembles one process after storage and QUIC have opened.
    ///
    /// The constructor proves exact agreement between the root database
    /// catalog, routing catalog, placement, and transport admission table. It
    /// does not admit traffic. The durable membership source and data-plane
    /// adapter must still be attached, and their facts must pass readiness.
    ///
    /// # Errors
    ///
    /// Returns a typed error for catalog, local identity, Region, version, or
    /// full-mesh peer disagreement.
    pub fn try_new(
        placement: ProductionPlacement,
        routes: DatabaseRoutingCatalog,
        member: Member,
        transport: ProductionQuicEndpoint,
    ) -> Result<Self, ProductionMemberError> {
        let member_databases = member.database_ids().cloned().collect::<BTreeSet<_>>();
        let route_databases = routes.routes.values().cloned().collect::<BTreeSet<_>>();
        if member_databases != route_databases || member.status().databases != routes.len() {
            return Err(ProductionMemberError::CatalogMismatch);
        }

        let transport_config = transport.config();
        if transport_config.local_identity().member().as_str() != placement.local_member.to_text()
            || transport_config.local_region() != transport_region(placement.local_region())
            || transport_config.placement_generation() != placement.generation
            || transport_config.replica_set_epoch() != placement.epoch
        {
            return Err(ProductionMemberError::TransportMismatch);
        }
        let expected_peers = placement
            .remote_members()
            .into_iter()
            .map(MemberUlid::to_text)
            .collect::<BTreeSet<_>>();
        let configured_peers = transport_config
            .peers()
            .map(|peer| peer.identity().member().as_str().to_owned())
            .collect::<BTreeSet<_>>();
        if expected_peers != configured_peers {
            return Err(ProductionMemberError::TransportMismatch);
        }

        Ok(Self {
            placement,
            routes,
            member,
            transport,
            membership_admitted: AtomicBool::new(false),
            data_plane: Mutex::new(None),
            draining: AtomicBool::new(false),
        })
    }

    /// Reconciles this configured version with the coordinated durable
    /// membership authority.
    ///
    /// A control-plane outage after this succeeds does not revoke the durable
    /// admission. A lease expiry alone cannot remove a Member or lower an
    /// acknowledgement boundary.
    ///
    /// # Errors
    ///
    /// Returns a source failure or [`ProductionMemberError::MembershipMismatch`]
    /// when the durable record is not byte-for-byte the configured placement.
    pub fn admit_membership(
        &self,
        source: &dyn DurableMembershipSource,
    ) -> Result<(), ProductionMemberError> {
        let admitted =
            source.load_admitted_placement().map_err(ProductionMemberError::MembershipSource)?;
        if admitted != self.placement {
            return Err(ProductionMemberError::MembershipMismatch);
        }
        self.membership_admitted.store(true, Ordering::Release);
        Ok(())
    }

    /// Attaches the one durable data-plane and complete-host composition.
    ///
    /// # Errors
    ///
    /// Refuses replacement because switching authorities while serving would
    /// make readiness and progress ambiguous.
    pub fn attach_data_plane(
        &self,
        data_plane: Arc<dyn ProductionDataPlane>,
    ) -> Result<(), ProductionMemberError> {
        let mut slot = lock_unpoisoned(&self.data_plane);
        if slot.is_some() {
            return Err(ProductionMemberError::DataPlaneAlreadyAttached);
        }
        *slot = Some(data_plane);
        Ok(())
    }

    /// Returns the authenticated transport for its accept/dial driver.
    #[must_use]
    pub const fn transport(&self) -> &ProductionQuicEndpoint {
        &self.transport
    }

    /// Computes readiness without consulting NATS liveness.
    #[must_use]
    pub fn facts(&self) -> ProductionMemberFacts {
        let transport = self.transport.facts();
        let data_plane = lock_unpoisoned(&self.data_plane)
            .as_ref()
            .map_or_else(ProductionDataPlaneFacts::default, |data_plane| data_plane.facts());
        let data_plane_present = lock_unpoisoned(&self.data_plane).is_some();
        let storage = self.member.status();
        let mut blockers = BTreeSet::new();
        if self.draining.load(Ordering::Acquire) {
            blockers.insert(ProductionReadinessBlocker::Draining);
        }
        if storage.lifecycle != LifecycleState::Serving || storage.storage != StorageHealth::Healthy
        {
            blockers.insert(ProductionReadinessBlocker::CatalogNotServing);
        }
        if !self.membership_admitted.load(Ordering::Acquire) {
            blockers.insert(ProductionReadinessBlocker::MembershipNotAdmitted);
        }
        if !transport.bound || !transport.accepting || !transport.aws_lc_fips {
            blockers.insert(ProductionReadinessBlocker::TransportNotAccepting);
        }
        if transport.authenticated_peers < transport.configured_peers {
            blockers.insert(ProductionReadinessBlocker::PeerSessionsMissing);
        }
        if !data_plane_present {
            blockers.insert(ProductionReadinessBlocker::DataPlaneMissing);
        }
        if !data_plane.databases_admitted {
            blockers.insert(ProductionReadinessBlocker::DatabaseLocationsNotAdmitted);
        }
        if !data_plane.local_rf2_reconciled {
            blockers.insert(ProductionReadinessBlocker::LocalRf2NotReconciled);
        }
        if !data_plane.replication_serving {
            blockers.insert(ProductionReadinessBlocker::ReplicationNotServing);
        }
        if !data_plane.repair_serving {
            blockers.insert(ProductionReadinessBlocker::RepairNotServing);
        }
        if !data_plane.durable_progress_available {
            blockers.insert(ProductionReadinessBlocker::DurableProgressUnavailable);
        }
        if !data_plane.publication_journal_authoritative {
            blockers.insert(ProductionReadinessBlocker::PublicationJournalNotAuthoritative);
        }
        if !data_plane.storage_encrypted {
            blockers.insert(ProductionReadinessBlocker::StorageEncryptionUnavailable);
        }
        if !data_plane.complete_member_host {
            blockers.insert(ProductionReadinessBlocker::CompleteMemberHostUnavailable);
        }
        if !data_plane.metrics_serving {
            blockers.insert(ProductionReadinessBlocker::MetricsUnavailable);
        }
        if !data_plane.shutdown_frontiers_persistable {
            blockers.insert(ProductionReadinessBlocker::ShutdownFrontiersUnavailable);
        }
        let blockers = blockers.into_iter().collect::<Vec<_>>();
        ProductionMemberFacts {
            ready: blockers.is_empty(),
            blockers,
            placement_generation: self.placement.generation,
            replica_set_epoch: self.placement.epoch,
            databases: self.routes.len(),
            transport,
            data_plane,
        }
    }

    /// Resolves a bound scope and acquires the isolated engine only when the
    /// complete Member is ready.
    ///
    /// # Errors
    ///
    /// Returns bounded readiness blockers, an unknown-scope refusal, or the
    /// root Member's bounded admission refusal.
    pub fn acquire(
        &self,
        scope: &DatabaseRouteScope,
    ) -> Result<ProductionDatabaseLease, ProductionAdmissionError> {
        let facts = self.facts();
        if !facts.ready {
            return Err(ProductionAdmissionError::NotReady(facts.blockers));
        }
        let database =
            self.routes.resolve(scope).ok_or(ProductionAdmissionError::UnknownDatabaseScope)?;
        let lease = self.member.acquire(database).map_err(ProductionAdmissionError::Storage)?;
        Ok(ProductionDatabaseLease { scope: *scope, lease })
    }

    /// Stops admission, drains the data plane and QUIC endpoint, then flushes
    /// every isolated engine within bounded component deadlines.
    pub async fn drain(self) -> ProductionMemberDrainReport {
        self.draining.store(true, Ordering::Release);
        let data_plane = lock_unpoisoned(&self.data_plane).clone();
        if let Some(data_plane) = &data_plane {
            data_plane.begin_drain();
        }
        let timeout = self.transport.config().limits().drain_timeout();
        let transport = self.transport.drain().await;
        let data_plane = data_plane
            .map_or(Err(ProductionDataPlaneError::Persistence), |plane| plane.drain(timeout));
        let storage = self.member.close(timeout);
        ProductionMemberDrainReport { transport, data_plane, storage }
    }
}

/// One admitted database operation. It exposes the engine, never its durable
/// location.
pub struct ProductionDatabaseLease {
    scope: DatabaseRouteScope,
    lease: DatabaseLease,
}

impl ProductionDatabaseLease {
    /// Returns the exact bound route scope used for admission.
    #[must_use]
    pub const fn scope(&self) -> DatabaseRouteScope {
        self.scope
    }

    /// Returns the opaque database catalog identity.
    #[must_use]
    pub fn database_id(&self) -> &DatabaseId {
        self.lease.database_id()
    }
}

impl Deref for ProductionDatabaseLease {
    type Target = Engine;

    fn deref(&self) -> &Self::Target {
        &self.lease
    }
}

/// Production request-admission refusal.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum ProductionAdmissionError {
    /// The complete Member has one or more bounded readiness blockers.
    NotReady(Vec<ProductionReadinessBlocker>),
    /// The authenticated scope has no operator-provisioned route.
    UnknownDatabaseScope,
    /// The root Member refused storage or bounded admission.
    Storage(StorageMemberError),
}

/// Component outcomes of one bounded process drain.
#[derive(Debug)]
pub struct ProductionMemberDrainReport {
    /// Authenticated transport drain facts.
    pub transport: TransportDrainReport,
    /// Durable replication and serving-plane drain result.
    pub data_plane: Result<(), ProductionDataPlaneError>,
    /// Isolated engine flush and close result.
    pub storage: Result<(), StorageMemberError>,
}

/// Production Member assembly failure.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ProductionMemberError {
    /// The selected Region or RF=2 shape was invalid.
    InvalidPlacement,
    /// The bound database routing catalog was invalid.
    InvalidRoutingCatalog,
    /// The root Member database set and routing catalog did not match exactly.
    CatalogMismatch,
    /// Transport identity, Region, membership version, or peer mesh differed.
    TransportMismatch,
    /// The durable membership source could not load an accepted record.
    MembershipSource(MembershipSourceError),
    /// The durable membership record did not match the configured placement.
    MembershipMismatch,
    /// A second data-plane authority was offered.
    DataPlaneAlreadyAttached,
}

fn lock_unpoisoned<T>(mutex: &Mutex<T>) -> std::sync::MutexGuard<'_, T> {
    match mutex.lock() {
        Ok(guard) => guard,
        Err(poisoned) => poisoned.into_inner(),
    }
}

#[cfg(test)]
mod tests {
    use terrabase::identity::{DatabaseUlid, MemberUlid, ProjectUlid, TenantUlid, Ulid};
    use terrabase::RegionCode;

    use super::{
        DatabaseRouteScope, DatabaseRoutingCatalog, ProductionMemberError, ProductionPlacement,
        ProductionRegionSeats,
    };

    fn value(number: u128) -> Ulid {
        Ulid::from_parts(1, number).expect("test ULID")
    }

    fn member(number: u128) -> MemberUlid {
        MemberUlid::from_value(value(number))
    }

    fn scope(number: u128) -> DatabaseRouteScope {
        DatabaseRouteScope::new(
            TenantUlid::from_value(value(100)),
            ProjectUlid::from_value(value(200)),
            DatabaseUlid::from_value(value(number)),
        )
    }

    #[test]
    fn placement_is_exact_rf2_over_the_initial_catalog() {
        let placement = ProductionPlacement::try_new(
            member(1),
            7,
            9,
            [
                ProductionRegionSeats::try_new(RegionCode::GRU, [member(1), member(2)]).unwrap(),
                ProductionRegionSeats::try_new(RegionCode::SFO, [member(3), member(4)]).unwrap(),
                ProductionRegionSeats::try_new(RegionCode::FRA, [member(5), member(6)]).unwrap(),
            ],
        )
        .expect("three-Region RF=2 placement");
        assert_eq!(placement.local_region(), RegionCode::GRU);
        assert_eq!(placement.remote_members().len(), 5);
        assert_eq!(placement.generation(), 7);
        assert_eq!(placement.epoch(), 9);
    }

    #[test]
    fn placement_refuses_aliases_duplicate_seats_and_an_absent_local_member() {
        let sjc = RegionCode::from_text("SJC").expect("well-shaped but unregistered code");
        assert_eq!(
            ProductionRegionSeats::try_new(sjc, [member(1), member(2)]),
            Err(ProductionMemberError::InvalidPlacement)
        );
        assert_eq!(
            ProductionRegionSeats::try_new(RegionCode::GRU, [member(1), member(1)]),
            Err(ProductionMemberError::InvalidPlacement)
        );
        let gru = ProductionRegionSeats::try_new(RegionCode::GRU, [member(1), member(2)]).unwrap();
        assert_eq!(
            ProductionPlacement::try_new(member(3), 1, 1, [gru]),
            Err(ProductionMemberError::InvalidPlacement)
        );
    }

    #[test]
    fn routing_uses_bound_ulids_and_refuses_database_aliases() {
        let first = scope(1);
        let second = scope(2);
        let routes = DatabaseRoutingCatalog::try_new([first, second]).expect("routes");
        assert_eq!(routes.len(), 2);
        assert_eq!(
            routes.resolve(&first).map(|database| database.as_str().to_owned()),
            Some(first.database().to_text())
        );
        assert_eq!(
            DatabaseRoutingCatalog::try_new([first, first]).err(),
            Some(ProductionMemberError::InvalidRoutingCatalog)
        );
    }
}
