//! The leaderless, masterless replication core (P2, ADR 0018).
//!
//! This module implements the write path of the frozen leaderless
//! contract (`docs/design/p0-leaderless-replication-contract.md`):
//! symmetric replica sets, any-Member request entry, request-scoped
//! coordination, and the `RegionalQuorum` / `GlobalQuorum`
//! acknowledgement lattice. It contains no ownership, lease, election,
//! term, or fencing concept — the superseded leader machinery stays in
//! `member.rs` until the full replacement lands, and nothing here
//! delegates to it.
//!
//! Write flow (contract §3): the accepting Member validates the write,
//! resolves the current epoch's replica set, applies durably through
//! its own engine, fans the same operation to every eligible voting
//! replica, and returns success only when the requested level is met:
//!
//! - `RegionalQuorum` — durable acceptance by a majority of eligible
//!   voting Members in the accepting Region (the minimum success level);
//! - `GlobalQuorum` — `RegionalQuorum` in a majority of eligible active
//!   Regions, including the accepting Region.
//!
//! Coordinator failure is retry: the client resubmits the same payload
//! with the same `OperationId` to any other active Member; every
//! replica's engine resolves the identity idempotently, so the retry
//! converges to one apply per replica and one receipt per Member. No
//! election, no redirect.
//!
//! This first landing proves the coordination contract over real
//! engines: any-Member entry, the quorum arithmetic, coordinator-failure
//! retry without a second apply, and typed partial-state failure. The
//! anti-entropy and read-repair halves of the contract (partition
//! healing, catch-up after rejoin) land with the member-transport
//! wiring that follows.

use std::collections::BTreeMap;

use super::{MemberId, RegionId};
use crate::engine::{CommitOutcome, CommitReceipt, Engine};
use crate::types::{
    Document, DocumentId, ExpectedVersion, LocalDurability, OperationId, TenantId, TenantScope,
};

/// The leaderless acknowledgement lattice (contract §3).
///
/// Vocabulary discipline: no `NotOwner`, no leader, no term. A local
/// commit alone is internal partial progress and is not a level a
/// caller may request or receive as success.
#[derive(Clone, Copy, Debug, Eq, Hash, PartialEq)]
pub enum LeaderlessLevel {
    /// Durable acceptance by a majority of eligible voting Members in
    /// the accepting Region. The minimum successful write
    /// acknowledgement.
    RegionalQuorum,
    /// `RegionalQuorum` in every selected active Region,
    /// including the accepting Region.
    GlobalQuorum,
}

/// One versioned replica set: the eligible voting Members per Region.
///
/// The epoch is the accepted configuration epoch (contract §7), never a
/// value a Member assigns alone. Every Member of the set holds the same
/// write, read, repair, and recovery role — there is no distinguished
/// replica and no per-range assignment inside this set.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ReplicaSet {
    epoch: u64,
    voters: BTreeMap<RegionId, Vec<MemberId>>,
}

impl ReplicaSet {
    /// Builds one replica set at a configuration epoch.
    #[must_use]
    pub fn new(epoch: u64, voters: BTreeMap<RegionId, Vec<MemberId>>) -> Self {
        Self { epoch, voters }
    }

    /// Returns the replica-set epoch.
    #[must_use]
    pub const fn epoch(&self) -> u64 {
        self.epoch
    }

    /// Returns the Regions holding eligible voting Members, in canonical
    /// order.
    #[must_use]
    pub fn regions(&self) -> Vec<RegionId> {
        self.voters.keys().cloned().collect()
    }

    /// Returns the eligible voting Members of one Region.
    #[must_use]
    pub fn voters_of(&self, region: &RegionId) -> &[MemberId] {
        self.voters.get(region).map_or(&[], Vec::as_slice)
    }

    /// Reports whether one Member is an eligible voter in the set.
    #[must_use]
    pub fn is_voter(&self, member: &MemberId) -> bool {
        self.voters.values().any(|members| members.contains(member))
    }

    /// Returns the Region a Member votes in, when it is eligible.
    #[must_use]
    pub fn region_of(&self, member: &MemberId) -> Option<RegionId> {
        self.voters
            .iter()
            .find(|(_, members)| members.contains(member))
            .map(|(region, _)| region.clone())
    }

    /// The majority threshold of one Region's eligible voters.
    #[must_use]
    pub fn regional_threshold(&self, region: &RegionId) -> usize {
        self.voters_of(region).len() / 2 + 1
    }

    /// The number of selected active Regions required for global durability.
    #[must_use]
    pub fn global_threshold(&self) -> usize {
        self.regions().len()
    }
}

/// One staged mutation of a leaderless write.
#[derive(Clone, Debug, PartialEq)]
enum LeaderlessMutation {
    /// Stages one document put.
    Put(Document),
    /// Stages one document deletion.
    Delete(DocumentId),
}

/// One write request in the leaderless vocabulary: a stable operation
/// identity plus its write set. No range, no owner, no per-request
/// acknowledgement field — the level is an input of coordination, not a
/// property of the write.
#[derive(Clone, Debug, PartialEq)]
pub struct LeaderlessWrite {
    operation: OperationId,
    writes: Vec<LeaderlessMutation>,
}

impl LeaderlessWrite {
    /// Creates one write request carrying a stable operation identity.
    #[must_use]
    pub fn new(operation: OperationId) -> Self {
        Self { operation, writes: Vec::new() }
    }

    /// Stages one document put.
    #[must_use]
    pub fn with_put(mut self, document: Document) -> Self {
        self.writes.push(LeaderlessMutation::Put(document));
        self
    }

    /// Stages one document deletion.
    #[must_use]
    pub fn with_delete(mut self, id: DocumentId) -> Self {
        self.writes.push(LeaderlessMutation::Delete(id));
        self
    }

    /// Returns the stable operation identity.
    #[must_use]
    pub fn operation(&self) -> &OperationId {
        &self.operation
    }
}

/// One replica's durable acknowledgement of one operation.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ReplicaAck {
    member: MemberId,
    region: RegionId,
    epoch: u64,
    sequence: u64,
}

impl ReplicaAck {
    /// Returns the acknowledging Member.
    #[must_use]
    pub fn member(&self) -> &MemberId {
        &self.member
    }

    /// Returns the acknowledging Member's Region.
    #[must_use]
    pub fn region(&self) -> &RegionId {
        &self.region
    }

    /// Returns the replica-set epoch the acknowledgement was collected
    /// at.
    #[must_use]
    pub const fn epoch(&self) -> u64 {
        self.epoch
    }

    /// Returns the storage sequence the Member's engine published.
    #[must_use]
    pub const fn sequence(&self) -> u64 {
        self.sequence
    }
}

/// The receipt of one leaderless write (contract §3).
///
/// Names the achieved level, every contributing Member and Region, the
/// accepting Member, and the epoch the quorums were evaluated at. A
/// local commit alone never appears here.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct LeaderlessReceipt {
    operation: OperationId,
    requested: LeaderlessLevel,
    achieved: LeaderlessLevel,
    accepting_member: MemberId,
    accepting_region: RegionId,
    epoch: u64,
    contributors: Vec<ReplicaAck>,
    local: CommitReceipt,
}

impl LeaderlessReceipt {
    /// Returns the stable operation identity.
    #[must_use]
    pub fn operation(&self) -> &OperationId {
        &self.operation
    }

    /// Returns the requested acknowledgement level.
    #[must_use]
    pub const fn requested(&self) -> LeaderlessLevel {
        self.requested
    }

    /// Returns the achieved acknowledgement level.
    #[must_use]
    pub const fn achieved(&self) -> LeaderlessLevel {
        self.achieved
    }

    /// Returns the request-scoped coordinating Member.
    #[must_use]
    pub fn accepting_member(&self) -> &MemberId {
        &self.accepting_member
    }

    /// Returns the accepting Region.
    #[must_use]
    pub fn accepting_region(&self) -> &RegionId {
        &self.accepting_region
    }

    /// Returns the replica-set epoch the quorums were evaluated at.
    #[must_use]
    pub const fn epoch(&self) -> u64 {
        self.epoch
    }

    /// Returns every contributing Member's acknowledgement, in canonical
    /// Member order.
    #[must_use]
    pub fn contributors(&self) -> &[ReplicaAck] {
        &self.contributors
    }

    /// Returns the Regions that contributed, in canonical order.
    #[must_use]
    pub fn contributing_regions(&self) -> Vec<RegionId> {
        let mut regions: Vec<RegionId> =
            self.contributors.iter().map(ReplicaAck::region).cloned().collect();
        regions.sort();
        regions.dedup();
        regions
    }

    /// Returns the accepting Member's own local commit receipt.
    #[must_use]
    pub fn local(&self) -> &CommitReceipt {
        &self.local
    }
}

/// Why a leaderless write did not succeed.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum LeaderlessError {
    /// `GlobalQuorum` needs at least two selected active Regions.
    InsufficientRegions {
        /// The number of selected active Regions in the replica set.
        selected: usize,
        /// The minimum number required by the durability contract.
        minimum: usize,
    },
    /// The accepting Member is not an eligible voter of the set's epoch.
    NotEligible {
        /// The rejecting epoch.
        epoch: u64,
    },
    /// One replica's apply failed; the receipt names nothing.
    ApplyFailed {
        /// The replica whose apply failed.
        member: MemberId,
    },
    /// The requested level was not met. The partial state is durable at
    /// the named contributors: retry with the same `OperationId`
    /// through any Member (contract §3, coordinator failure).
    QuorumNotReached {
        /// The durable partial contributors at the deadline.
        contributors: Vec<ReplicaAck>,
    },
}

/// Why one replica's apply produced no receipt.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum ReplicaApplyRefusal {
    /// The snapshot could not be taken.
    Snapshot,
    /// The transaction could not begin.
    Begin,
    /// A staged write was refused.
    Stage,
    /// The commit was rejected or returned unknown.
    Commit,
}

/// One replica the coordinator fans out to: its identity, its Region
/// seat, and its engine.
pub struct ReplicaEndpoint<'a> {
    member: MemberId,
    region: RegionId,
    engine: &'a Engine,
}

impl<'a> ReplicaEndpoint<'a> {
    /// Seats one engine in the replica set under its Member identity.
    #[must_use]
    pub fn new(member: MemberId, region: RegionId, engine: &'a Engine) -> Self {
        Self { member, region, engine }
    }
}

/// Applies one write durably through one replica's engine.
///
/// Idempotent by operation identity: a retry through any Member resolves
/// to the recorded outcome, never a second apply — the engine's
/// operation ledger holds that invariant (contract §2).
fn apply_on_replica(
    engine: &Engine,
    scope: &TenantScope,
    write: &LeaderlessWrite,
) -> Result<CommitReceipt, ReplicaApplyRefusal> {
    let snapshot = engine.read_snapshot(scope).map_err(|_| ReplicaApplyRefusal::Snapshot)?;
    let mut transaction = engine
        .begin(scope, snapshot, write.operation().clone())
        .map_err(|_| ReplicaApplyRefusal::Begin)?;
    for staged in &write.writes {
        match staged {
            LeaderlessMutation::Put(document) => {
                transaction
                    .put(document.clone(), ExpectedVersion::Any)
                    .map_err(|_| ReplicaApplyRefusal::Stage)?;
            }
            LeaderlessMutation::Delete(id) => {
                transaction
                    .delete(id.clone(), ExpectedVersion::Any)
                    .map_err(|_| ReplicaApplyRefusal::Stage)?;
            }
        }
    }
    match transaction.commit(LocalDurability::Synced) {
        CommitOutcome::Committed(receipt) => Ok(receipt),
        // A rejected retry of a committed identity is impossible — the
        // ledger replays it — so reaching here means the write itself
        // failed on this replica.
        CommitOutcome::Rejected(_) | CommitOutcome::Unknown(_) => Err(ReplicaApplyRefusal::Commit),
    }
}

/// Coordinates one leaderless write (contract §3).
///
/// The accepting Member is the request-scoped coordinator only: it
/// applies through its own engine, every eligible replica applies the
/// same operation through theirs, and success is decided purely by the
/// collected durable acknowledgements against the epoch's replica set.
/// There is no ownership check, no lease, and no distinguished replica.
///
/// # Errors
///
/// Returns [`LeaderlessError::NotEligible`] when the accepting Member
/// is not an eligible voter of the set's epoch,
/// [`LeaderlessError::ApplyFailed`] when one reachable replica's apply
/// fails, and [`LeaderlessError::QuorumNotReached`] when the requested
/// level is not met — naming the durable partial contributors for the
/// retry.
pub fn coordinate_write(
    set: &ReplicaSet,
    accepting: &MemberId,
    replicas: &[ReplicaEndpoint<'_>],
    tenant: &TenantId,
    write: &LeaderlessWrite,
    level: LeaderlessLevel,
) -> Result<LeaderlessReceipt, LeaderlessError> {
    let Some(accepting_region) = set.region_of(accepting) else {
        return Err(LeaderlessError::NotEligible { epoch: set.epoch() });
    };
    if level == LeaderlessLevel::GlobalQuorum {
        let selected = set.regions().len();
        if selected < 2 {
            return Err(LeaderlessError::InsufficientRegions { selected, minimum: 2 });
        }
    }
    // Fan out to every eligible reachable replica: the coordinator
    // itself and every other voter of the set. Remote links run in
    // parallel in the production transport; this core proves the
    // collection and evaluation contract, not the wire scheduling.
    let mut contributors: Vec<ReplicaAck> = Vec::new();
    let mut local: Option<CommitReceipt> = None;
    for endpoint in replicas {
        if !set.is_voter(&endpoint.member) {
            continue;
        }
        let scope = TenantScope::from_parts(tenant.clone(), endpoint.engine.identity());
        let receipt = apply_on_replica(endpoint.engine, &scope, write)
            .map_err(|_| LeaderlessError::ApplyFailed { member: endpoint.member.clone() })?;
        if &endpoint.member == accepting {
            local = Some(receipt.clone());
        }
        contributors.push(ReplicaAck {
            member: endpoint.member.clone(),
            region: endpoint.region.clone(),
            epoch: set.epoch(),
            sequence: receipt.sequence().get(),
        });
    }
    let Some(local) = local else {
        // The accepting Member contributed no durable acknowledgement,
        // so no quorum containing it was met.
        return Err(LeaderlessError::QuorumNotReached { contributors });
    };
    // RegionalQuorum: majority of the accepting Region's eligible
    // voters.
    let regional_count = contributors.iter().filter(|ack| ack.region == accepting_region).count();
    if regional_count < set.regional_threshold(&accepting_region) {
        return Err(LeaderlessError::QuorumNotReached { contributors });
    }
    // GlobalQuorum: RegionalQuorum in every selected Region, including the
    // accepting Region established above.
    if level == LeaderlessLevel::GlobalQuorum {
        let qualified_regions = set
            .regions()
            .into_iter()
            .filter(|region| {
                let count = contributors.iter().filter(|ack| &ack.region == region).count();
                count >= set.regional_threshold(region)
            })
            .count();
        if qualified_regions < set.global_threshold() {
            return Err(LeaderlessError::QuorumNotReached { contributors });
        }
    }
    contributors.sort_by(|left, right| left.member.as_str().cmp(right.member.as_str()));
    Ok(LeaderlessReceipt {
        operation: write.operation().clone(),
        requested: level,
        achieved: level,
        accepting_member: accepting.clone(),
        accepting_region,
        epoch: set.epoch(),
        contributors,
        local,
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::EngineConfig;
    use crate::types::{CollectionName, DocumentValue, StorageSequence};
    use std::collections::BTreeMap;
    use std::path::PathBuf;

    fn member_id(name: &str) -> MemberId {
        MemberId::try_new(name).expect("valid member identifier")
    }

    fn region_id(name: &str) -> RegionId {
        RegionId::try_new(name).expect("valid region identifier")
    }

    fn tenant_of(name: &str) -> TenantId {
        TenantId::try_new(name).expect("valid tenant identifier")
    }

    fn operation_of(name: &str) -> OperationId {
        OperationId::try_new(name).expect("valid operation identifier")
    }

    /// A three-Region, six-Member symmetric replica set: every Region
    /// holds two eligible voters, no Member distinguished.
    fn three_region_set() -> ReplicaSet {
        let mut voters = BTreeMap::new();
        voters.insert(region_id("r-alpha"), vec![member_id("a1"), member_id("a2")]);
        voters.insert(region_id("r-beta"), vec![member_id("b1"), member_id("b2")]);
        voters.insert(region_id("r-gamma"), vec![member_id("g1"), member_id("g2")]);
        ReplicaSet::new(3, voters)
    }

    /// Opens one scratch engine standing in for one Member.
    fn engine_for(tag: &str) -> (PathBuf, Engine) {
        let dir = std::env::temp_dir().join(format!(
            "terrabase-leaderless-{tag}-{}-{}",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .map_or(0, |elapsed| elapsed.as_nanos())
        ));
        let _ = std::fs::remove_dir_all(&dir);
        let engine = Engine::open(EngineConfig::durable(&dir).expect("durable location"))
            .expect("engine opens");
        (dir, engine)
    }

    fn build_engines(names: &[&'static str]) -> Vec<(&'static str, PathBuf, Engine)> {
        names
            .iter()
            .map(|name| {
                let (dir, engine) = engine_for(name);
                ((*name), dir, engine)
            })
            .collect()
    }

    fn cleanup(engines: &[(&'static str, PathBuf, Engine)]) {
        for (_, dir, _) in engines {
            let _ = std::fs::remove_dir_all(dir);
        }
    }

    /// Wires one endpoint per engine seated in the set.
    fn endpoints<'a>(
        engines: &'a [(&'static str, PathBuf, Engine)],
        set: &ReplicaSet,
    ) -> Vec<ReplicaEndpoint<'a>> {
        engines
            .iter()
            .filter_map(|(name, _, engine)| {
                let id = member_id(name);
                let region = set.region_of(&id)?;
                Some(ReplicaEndpoint::new(id, region, engine))
            })
            .collect()
    }

    fn put_write(operation: &str, id: &str) -> LeaderlessWrite {
        LeaderlessWrite::new(operation_of(operation)).with_put(Document::new(
            DocumentId::try_new(id).expect("valid document identifier"),
            CollectionName::try_new("orders").expect("valid collection identifier"),
            DocumentValue::String("payload".to_owned()),
        ))
    }

    /// One tenant's commit count on one engine — the engine-seam view of
    /// "applied once, not twice".
    fn commit_count(engine: &Engine, tenant: &TenantId) -> usize {
        engine
            .tenant_commits(tenant, StorageSequence::new(0), 1000)
            .expect("commit window serves")
            .len()
    }

    #[test]
    fn every_member_accepts_the_same_write() {
        let set = three_region_set();
        let engines = build_engines(&["a1", "a2", "b1", "b2", "g1", "g2"]);
        let fan = endpoints(&engines, &set);
        let tenant = tenant_of("tenant-a");
        for accepting in ["a1", "a2", "b1", "b2", "g1", "g2"] {
            let receipt = coordinate_write(
                &set,
                &member_id(accepting),
                &fan,
                &tenant,
                &put_write(&format!("op-{accepting}"), "doc-1"),
                LeaderlessLevel::RegionalQuorum,
            )
            .expect("every active Member accepts the write");
            assert_eq!(receipt.contributors().len(), 6, "the fan-out reaches every voter");
            assert_eq!(*receipt.accepting_member(), member_id(accepting));
            assert_eq!(receipt.epoch(), 3);
            assert_eq!(receipt.contributing_regions(), set.regions());
        }
        cleanup(&engines);
    }

    #[test]
    fn regional_quorum_succeeds_while_other_regions_are_partitioned() {
        let set = three_region_set();
        // Only the accepting Region's Members are reachable.
        let engines = build_engines(&["a1", "a2"]);
        let fan = endpoints(&engines, &set);
        let tenant = tenant_of("tenant-a");
        let receipt = coordinate_write(
            &set,
            &member_id("a1"),
            &fan,
            &tenant,
            &put_write("op-regional", "doc-1"),
            LeaderlessLevel::RegionalQuorum,
        )
        .expect("a majority of the accepting Region suffices");
        assert_eq!(receipt.contributing_regions(), vec![region_id("r-alpha")]);
        cleanup(&engines);
    }

    #[test]
    fn a_minority_of_one_region_fails_regional_quorum() {
        let set = three_region_set();
        let engines = build_engines(&["a1"]);
        let fan = endpoints(&engines, &set);
        let tenant = tenant_of("tenant-a");
        let error = coordinate_write(
            &set,
            &member_id("a1"),
            &fan,
            &tenant,
            &put_write("op-minority", "doc-1"),
            LeaderlessLevel::RegionalQuorum,
        )
        .expect_err("one of two voters is not a majority");
        let LeaderlessError::QuorumNotReached { contributors } = error else {
            panic!("a missed majority is a quorum failure, found {error:?}");
        };
        assert_eq!(contributors.len(), 1, "the durable partial state is named");
        assert_eq!(*contributors[0].member(), member_id("a1"));
        cleanup(&engines);
    }

    #[test]
    fn global_quorum_requires_every_selected_region() {
        let set = three_region_set();
        let engines = build_engines(&["a1", "a2", "b1", "b2", "g1", "g2"]);
        let fan = endpoints(&engines, &set);
        let tenant = tenant_of("tenant-a");
        let receipt = coordinate_write(
            &set,
            &member_id("a1"),
            &fan,
            &tenant,
            &put_write("op-global", "doc-1"),
            LeaderlessLevel::GlobalQuorum,
        )
        .expect("every selected Region has a regional majority");
        assert_eq!(
            receipt.contributing_regions(),
            vec![region_id("r-alpha"), region_id("r-beta"), region_id("r-gamma")]
        );
        cleanup(&engines);
    }

    #[test]
    fn one_region_cannot_reach_global_quorum() {
        let set = three_region_set();
        let engines = build_engines(&["a1", "a2"]);
        let fan = endpoints(&engines, &set);
        let tenant = tenant_of("tenant-a");
        let error = coordinate_write(
            &set,
            &member_id("a1"),
            &fan,
            &tenant,
            &put_write("op-global-fail", "doc-1"),
            LeaderlessLevel::GlobalQuorum,
        )
        .expect_err("one Region is not a majority of three");
        assert!(matches!(error, LeaderlessError::QuorumNotReached { .. }));
        cleanup(&engines);
    }

    #[test]
    fn coordinator_failure_retry_through_another_member_applies_once() {
        let set = three_region_set();
        let engines = build_engines(&["a1", "a2", "b1", "b2", "g1", "g2"]);
        let tenant = tenant_of("tenant-a");
        let write = put_write("op-retry", "doc-1");
        // The first coordinator applies and completes the fan-out.
        let first = coordinate_write(
            &set,
            &member_id("a1"),
            &endpoints(&engines, &set),
            &tenant,
            &write,
            LeaderlessLevel::RegionalQuorum,
        )
        .expect("the first coordinator completes");
        let counts: Vec<usize> =
            engines.iter().map(|(_, _, engine)| commit_count(engine, &tenant)).collect();
        assert!(counts.iter().all(|count| *count == 1), "one commit per replica: {counts:?}");
        // The coordinator dies; the client retries the SAME operation
        // through another Member.
        let retried = coordinate_write(
            &set,
            &member_id("b1"),
            &endpoints(&engines, &set),
            &tenant,
            &write,
            LeaderlessLevel::RegionalQuorum,
        )
        .expect("the retry through another Member completes");
        assert_eq!(retried.operation(), first.operation());
        for (index, (name, _, engine)) in engines.iter().enumerate() {
            assert_eq!(
                commit_count(engine, &tenant),
                counts[index],
                "member {name} applied the retried operation exactly once"
            );
        }
        // The accepting Member changed; the achieved quorum did not.
        assert_eq!(*retried.accepting_member(), member_id("b1"));
        assert_eq!(retried.contributors().len(), 6);
        cleanup(&engines);
    }

    #[test]
    fn an_outside_member_cannot_coordinate() {
        let set = three_region_set();
        let engines = build_engines(&["a1"]);
        let fan = endpoints(&engines, &set);
        let tenant = tenant_of("tenant-a");
        let error = coordinate_write(
            &set,
            &member_id("outsider"),
            &fan,
            &tenant,
            &put_write("op-outsider", "doc-1"),
            LeaderlessLevel::RegionalQuorum,
        )
        .expect_err("an ineligible Member cannot coordinate");
        assert_eq!(error, LeaderlessError::NotEligible { epoch: 3 });
        cleanup(&engines);
    }

    #[test]
    fn replica_sets_expose_voter_seats_without_ownership() {
        let set = three_region_set();
        assert_eq!(set.voters_of(&region_id("r-beta")), [member_id("b1"), member_id("b2")]);
        assert!(set.is_voter(&member_id("g2")));
        assert!(!set.is_voter(&member_id("outsider")));
        assert_eq!(set.region_of(&member_id("a1")), Some(region_id("r-alpha")));
        assert_eq!(set.region_of(&member_id("outsider")), None);
        assert_eq!(set.epoch(), 3);
    }
}
