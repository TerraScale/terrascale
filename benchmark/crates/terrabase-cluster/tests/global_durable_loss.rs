//! The GlobalDurable complete-Region-loss lane — gap-sweep row 14
//! (`docs/evidence/qualification-gap-sweep.md`): the charter row "complete
//! Region loss after `GlobalDurable` | No acknowledged commit is lost."
//!
//! Two planes carry the lane, and each proves what only it can:
//!
//! - the cluster-crate acknowledgement ledger
//!   (`terrabase_cluster::durability`) mints the receipt that NAMES
//!   `DurabilityLevel::GlobalDurable` as the acknowledged level of the
//!   write, then a complete Region loss (both of its Members) leaves the
//!   acknowledged write durably held by every surviving Region — zero
//!   acknowledged-write loss — while the same shape acknowledged at
//!   `RegionalDurable` states its own honest RPO window on its receipt
//!   and loses the write to the identical fault;
//! - the real-member cluster host (`ClusterHost`, three Regions of RF=2
//!   durable Members) commits the write at the every-selected-Region
//!   level, the lost Region's durable state is destroyed, and every
//!   surviving Member reopens and READS the acknowledged write back.
//!
//! The receipt contrast is ledger-side only: the in-process host fans a
//! write to every seated Member synchronously whatever level is
//! requested, so it cannot model the un-replicated `RegionalDurable`
//! window. Nothing here is a production or timing claim.

use std::collections::{BTreeMap, BTreeSet};
use std::path::PathBuf;

use terrabase_cluster::durability::{
    DurabilityCoordinator, DurabilityFailure, DurabilityLevel, Membership, RegionMembers,
};
use terrabase_cluster::host::{ClusterHost, MemberSpec};
use terrabase_cluster::{ClusterMode, ComposedWrite};
use terrabase_v2::replication::{LeaderlessLevel, MemberId, RegionId};
use terrabase_v2::types::DocumentValue;

/// The three selected Regions of every lane shape, in canonical order.
const REGIONS: [&str; 3] = ["r-alpha", "r-beta", "r-gamma"];

/// One Region's RF=2 seats, in declaration order.
fn seats(region: &str) -> [&'static str; 2] {
    match region {
        "r-alpha" => ["m-a1", "m-a2"],
        "r-beta" => ["m-b1", "m-b2"],
        _ => ["m-g1", "m-g2"],
    }
}

fn member(name: &str) -> MemberId {
    MemberId::try_new(name).unwrap()
}

fn region(name: &str) -> RegionId {
    RegionId::try_new(name).unwrap()
}

/// The three-Region RF=2 selected shape at configuration generation 1,
/// replica-set epoch 1.
fn three_region_membership() -> Membership {
    let regions =
        REGIONS.iter().map(|name| RegionMembers::try_new(name, seats(name)).unwrap()).collect();
    Membership::new(1, 1, regions).unwrap()
}

/// The same three-Region shape as real durable host Members.
fn active_spec() -> Vec<MemberSpec> {
    REGIONS
        .iter()
        .flat_map(|region| {
            seats(region)
                .into_iter()
                .map(|name| MemberSpec { name: name.into(), region: (*region).into() })
                .collect::<Vec<_>>()
        })
        .collect()
}

fn put(operation: &str, document: &str, status: &str) -> ComposedWrite {
    ComposedWrite::Put {
        operation: operation.to_owned(),
        document: document.to_owned(),
        body: BTreeMap::from([("status".to_owned(), DocumentValue::String(status.to_owned()))]),
    }
}

fn dir(tag: &str) -> PathBuf {
    let dir = std::env::temp_dir().join(format!(
        "tbv2-cluster-global-durable-loss-{tag}-{}-{}",
        std::process::id(),
        std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map_or(0, |elapsed| elapsed.as_nanos())
    ));
    let _ = std::fs::remove_dir_all(&dir);
    dir
}

/// Row 14's positive half: a write acknowledged at `GlobalDurable` in a
/// three-Region shape survives the COMPLETE loss of one Region — both of
/// its Members — with every surviving Region's RF=2 seats still holding
/// the acknowledged write.
#[test]
fn global_durable_receipt_survives_complete_region_loss_in_the_ledger() {
    let mut ledger = DurabilityCoordinator::new(three_region_membership());

    // (1) The write is acknowledged at GlobalDurable: the receipt NAMES
    // the level, and its durable footprint is every selected Region's
    // both seats under the same fencing pair.
    let receipt = ledger
        .submit("op-1", &member("m-a1"), DurabilityLevel::GlobalDurable)
        .expect("the three-Region shape acknowledges at the global level");
    assert_eq!(receipt.level(), DurabilityLevel::GlobalDurable, "the level is named");
    assert_eq!(receipt.operation(), "op-1");
    assert_eq!((receipt.generation(), receipt.epoch()), (1, 1));
    assert_eq!(receipt.contributors().len(), 6, "every seat contributed");
    for region_name in REGIONS {
        let held: Vec<&str> = receipt
            .contributors()
            .iter()
            .filter(|ack| ack.region().as_str() == region_name)
            .map(|ack| ack.member().as_str())
            .collect();
        assert_eq!(held, seats(region_name), "{region_name} holds both of its seats");
    }
    for ack in receipt.contributors() {
        assert_eq!((ack.generation(), ack.epoch()), (1, 1), "fenced observations only");
    }

    // (2) The complete loss of r-beta: BOTH of its Members go away.
    ledger.set_member_active(&member("m-b1"), false).unwrap();
    ledger.set_member_active(&member("m-b2"), false).unwrap();

    // (3) Zero acknowledged-write loss, observed live: re-coordinating
    // the SAME operation identity from a surviving Member adds nothing
    // (storage is idempotent) and refuses to re-claim the global
    // boundary over the lost shape — while the observation names every
    // Member that durably holds the acknowledged write.
    let probe = ledger
        .submit("op-1", &member("m-g1"), DurabilityLevel::GlobalDurable)
        .expect_err("the lost Region can no longer be part of the boundary");
    assert!(matches!(probe, DurabilityFailure::Partial(_)), "typed partial, not a claim");
    assert!(!probe.is_durable(), "a failure variant never claims the level");
    let observation = probe.observation().unwrap();
    assert_eq!(
        observation.missing_regions().iter().map(RegionId::as_str).collect::<Vec<_>>(),
        ["r-beta"],
        "the typed outcome names exactly the lost Region"
    );
    assert!(
        observation.missing_members().is_empty(),
        "the lost Region's seats HAD stored before the loss: {observation:?}"
    );
    for survivor in ["m-a1", "m-a2", "m-g1", "m-g2"] {
        assert!(
            observation.stored().iter().any(|ack| ack.member().as_str() == survivor),
            "{survivor} durably holds the acknowledged write"
        );
    }
    assert_eq!(observation.stored().len(), 6, "the idempotent probe added no contributor");

    // (4) The surviving shape keeps acknowledging at the named level: the
    // configuration steps forward over the overlap, the write minted
    // under the lost configuration is fenced (never silently re-acked),
    // and a NEW write acknowledges at GlobalDurable over the survivors.
    let survivors = Membership::new(
        2,
        2,
        vec![
            RegionMembers::try_new("r-alpha", seats("r-alpha")).unwrap(),
            RegionMembers::try_new("r-gamma", seats("r-gamma")).unwrap(),
        ],
    )
    .unwrap();
    ledger.advance_membership(survivors).expect("the overlapping step accepts");
    let fenced = ledger
        .submit("op-1", &member("m-a1"), DurabilityLevel::GlobalDurable)
        .expect_err("an operation of the lost configuration is fenced");
    assert!(
        matches!(fenced, DurabilityFailure::StaleOperation { ref operation, .. } if operation == "op-1"),
        "the acknowledged write is not re-acked under a new configuration: {fenced:?}"
    );
    let next = ledger
        .submit("op-2", &member("m-a1"), DurabilityLevel::GlobalDurable)
        .expect("the surviving two-Region shape acknowledges globally");
    assert_eq!(next.level(), DurabilityLevel::GlobalDurable);
    assert_eq!(next.contributors().len(), 4, "the surviving RF=2 shape, both Regions");
}

/// The receipt contrast: the identical fault takes the RegionalDurable
/// write and spares the GlobalDurable one. The RegionalDurable receipt
/// states its own honest RPO window at acknowledgement time — its
/// durable footprint is one Region — so the complete loss of that Region
/// destroys every copy of the acknowledged write. That is the charter's
/// regional-loss window, stated on the receipt and not hidden.
#[test]
fn regional_durable_receipt_states_its_rpo_window_and_the_identical_loss_takes_the_write() {
    let mut ledger = DurabilityCoordinator::new(three_region_membership());

    // The regional write: acknowledged with no WAN acknowledgement.
    let regional = ledger
        .submit("op-r", &member("m-a1"), DurabilityLevel::RegionalDurable)
        .expect("the local RF=2 boundary acknowledges");
    assert_eq!(regional.level(), DurabilityLevel::RegionalDurable);
    assert_eq!(regional.contributors().len(), 2, "the receipt's footprint is one Region");
    assert!(
        regional.contributors().iter().all(|ack| ack.region().as_str() == "r-alpha"),
        "the receipt names only the accepting Region: {regional:?}"
    );

    // The contrast write in the same shape: the global level.
    let global = ledger
        .submit("op-g", &member("m-a1"), DurabilityLevel::GlobalDurable)
        .expect("the global level acknowledges in the same shape");
    assert_eq!(global.contributors().len(), 6, "the global receipt spans every Region");

    // The complete loss of the ACCEPTING Region r-beta is not the fault
    // that separates them — r-alpha is: the RegionalDurable footprint
    // lives there and nowhere else.
    ledger.set_member_active(&member("m-a1"), false).unwrap();
    ledger.set_member_active(&member("m-a2"), false).unwrap();

    // op-r: every durable contributor named on its receipt was inside
    // the lost Region. Zero surviving copies — the acknowledged write is
    // gone. CRDT convergence cannot recover a mutation absent from every
    // surviving copy.
    let regional_footprint: BTreeSet<_> =
        regional.contributors().iter().map(|ack| ack.member().as_str()).collect();
    let lost_seats: BTreeSet<_> = seats("r-alpha").into_iter().collect();
    assert_eq!(
        regional_footprint, lost_seats,
        "the whole durable footprint of op-r is the lost Region: RPO loss, not recovery"
    );

    // op-g: the identical fault leaves every surviving seat holding it.
    let probe = ledger
        .submit("op-g", &member("m-b1"), DurabilityLevel::GlobalDurable)
        .expect_err("the boundary over the lost shape is refused, never claimed");
    let observation = probe.observation().unwrap();
    assert_eq!(
        observation.missing_regions().iter().map(RegionId::as_str).collect::<Vec<_>>(),
        ["r-alpha"]
    );
    let held: BTreeSet<_> = observation.stored().iter().map(|ack| ack.member().as_str()).collect();
    for survivor in ["m-b1", "m-b2", "m-g1", "m-g2"] {
        assert!(held.contains(survivor), "{survivor} durably holds op-g");
    }
    // The lost seats' observations remain in the ledger as history; the
    // surviving copies are the four above — the acknowledged write is
    // retained, and the fault is stated, not guessed.
    assert_eq!(held.len(), 6);
}

/// The level demands EVERY selected Region, not a majority: with only
/// one Region reachable out of three, GlobalDurable refuses typed while
/// the identical shape acknowledges at RegionalDurable — and the SAME
/// operation identity recovers to the global level once the WAN heals.
#[test]
fn global_durable_demands_every_selected_region_and_refuses_typed_when_one_cannot_be_selected() {
    let mut ledger = DurabilityCoordinator::new(three_region_membership());

    // Only the accepting Region can be selected: the other two are
    // WAN-partitioned.
    ledger.set_region_reachable(&region("r-beta"), false).unwrap();
    ledger.set_region_reachable(&region("r-gamma"), false).unwrap();

    let refusal = ledger
        .submit("op-1", &member("m-a1"), DurabilityLevel::GlobalDurable)
        .expect_err("a majority of Regions is not the global boundary");
    assert!(matches!(refusal, DurabilityFailure::Partial(_)));
    assert!(!refusal.is_durable());
    let observation = refusal.observation().unwrap();
    assert_eq!(
        observation.missing_regions().iter().map(RegionId::as_str).collect::<Vec<_>>(),
        ["r-beta", "r-gamma"],
        "both unreachable Regions are named"
    );
    // No receipt was minted, but the durable partials are named for the
    // retry — the refusal is honest about what already stored.
    let partials: BTreeSet<_> =
        observation.stored().iter().map(|ack| ack.member().as_str()).collect();
    let reachable_seats: BTreeSet<&str> = seats("r-alpha").into_iter().collect();
    assert_eq!(partials, reachable_seats, "the reachable Region's stores are named for the retry");

    // The identical shape at RegionalDurable acknowledges: the refusal
    // belongs to the level's every-Region requirement, not to the shape.
    let regional = ledger
        .submit("op-2", &member("m-a1"), DurabilityLevel::RegionalDurable)
        .expect("the single reachable Region satisfies the regional boundary");
    assert_eq!(regional.level(), DurabilityLevel::RegionalDurable);

    // The WAN heals and the SAME operation identity recovers to the
    // requested level — no replacement operation is minted.
    ledger.set_region_reachable(&region("r-beta"), true).unwrap();
    ledger.set_region_reachable(&region("r-gamma"), true).unwrap();
    let recovered = ledger
        .recover("op-1", &member("m-b1"), DurabilityLevel::GlobalDurable)
        .expect("the healed shape recovers the refused operation");
    assert_eq!(recovered.level(), DurabilityLevel::GlobalDurable);
    assert_eq!(recovered.operation(), "op-1");
    assert_eq!(recovered.coordinator(), &member("m-b1"), "a new member coordinates, owns nothing");
    assert_eq!(recovered.contributors().len(), 6);
}

/// The single-selected-Region shape: the ledger answers a DEGENERATE
/// GlobalDurable receipt whose durable footprint is that one Region (the
/// behavior pinned by the in-crate row
/// `global_requires_all_regions_for_one_two_and_three_region_shapes`),
/// and the complete loss of that Region takes the acknowledged write.
/// The charter's RPO 0 promise is conditioned on at least two selected
/// active Regions; with one, there is nothing to lose a Region against
/// and still keep a copy.
#[test]
fn a_single_selected_region_gets_no_region_loss_protection_from_the_global_level() {
    let solo =
        Membership::new(1, 1, vec![RegionMembers::try_new("r-solo", ["m-s1", "m-s2"]).unwrap()])
            .unwrap();
    let mut ledger = DurabilityCoordinator::new(solo);

    let receipt = ledger
        .submit("op-only", &member("m-s1"), DurabilityLevel::GlobalDurable)
        .expect("the degenerate single-Region shape acknowledges today");
    assert_eq!(receipt.level(), DurabilityLevel::GlobalDurable, "the level is named");
    assert_eq!(
        receipt.contributors().len(),
        2,
        "the footprint is one Region: no WAN boundary was crossed"
    );
    assert!(
        receipt.contributors().iter().all(|ack| ack.region().as_str() == "r-solo"),
        "the receipt names only the sole selected Region"
    );

    // The complete loss of the only selected Region: both Members.
    ledger.set_member_active(&member("m-s1"), false).unwrap();
    ledger.set_member_active(&member("m-s2"), false).unwrap();

    // Every durable contributor named on the receipt was inside the lost
    // Region — zero surviving copies of the acknowledged write.
    assert!(
        receipt.contributors().iter().all(|ack| ack.region().as_str() == "r-solo"),
        "the whole footprint is the lost Region: the acknowledged write is gone"
    );

    // And the boundary cannot be re-established: the typed outcome names
    // the lost Region, and no failure variant claims the level.
    let after = ledger
        .submit_until_deadline("op-only", &member("m-s1"), DurabilityLevel::GlobalDurable)
        .expect_err("no seat is active to answer for");
    assert!(matches!(after, DurabilityFailure::Timeout(_)), "typed timeout, got {after:?}");
    assert!(!after.is_durable());
    assert_eq!(
        after
            .observation()
            .unwrap()
            .missing_regions()
            .iter()
            .map(RegionId::as_str)
            .collect::<Vec<_>>(),
        ["r-solo"],
        "the typed outcome states the lost Region's boundary is gone"
    );
}

/// The real-member half: the same three-Region RF=2 shape as durable
/// `ClusterHost` Members, the write committed at the
/// every-selected-Region level, then the complete loss of one Region —
/// its two Members' durable state destroyed — and every surviving Member
/// reopens and reads the acknowledged write back. The ledger receipt
/// naming `GlobalDurable` is checked against the composed plane's
/// contributors for the same operation identity.
#[test]
fn global_durable_write_survives_complete_region_loss_on_real_members() {
    let root = dir("host");
    let spec = active_spec();
    let mut cluster = ClusterHost::create(
        &root,
        "cell-global",
        ClusterMode::Active,
        "created",
        &spec,
        "tenant-a",
    )
    .expect("the three-Region cluster creates");

    // The write at the every-selected-Region level over real durable
    // Members: the host's boundary check demands two contributors in
    // EVERY selected Region, not a majority of Regions.
    let committed = cluster
        .client_write(
            "m-a1",
            &put("op-1", "doc-1", "acknowledged-global"),
            LeaderlessLevel::GlobalQuorum,
        )
        .expect("the global level commits across three Regions");
    assert_eq!(committed.achieved(), LeaderlessLevel::GlobalQuorum);
    for region_name in REGIONS {
        let held = committed
            .contributors()
            .iter()
            .filter(|ack| ack.region().as_str() == region_name)
            .count();
        assert_eq!(held, 2, "{region_name} contributed both of its seats");
    }

    // The row-14 receipt: the same operation identity, acknowledged at
    // the NAMED level over the same shape in the cluster-crate ledger —
    // with the identical durable footprint.
    let mut ledger = DurabilityCoordinator::new(three_region_membership());
    let receipt = ledger
        .submit("op-1", &member("m-a1"), DurabilityLevel::GlobalDurable)
        .expect("the ledger acknowledges the same operation");
    assert_eq!(receipt.level(), DurabilityLevel::GlobalDurable);
    let ledger_seats: BTreeSet<_> =
        receipt.contributors().iter().map(|ack| ack.member().as_str().to_owned()).collect();
    let host_seats: BTreeSet<_> =
        committed.contributors().iter().map(|ack| ack.member().as_str().to_owned()).collect();
    assert_eq!(ledger_seats, host_seats, "the ledger names the same six durable seats");
    assert_eq!(ledger_seats.len(), 6);

    // The complete loss of r-beta: the cluster closes and the lost
    // Region's durable state is destroyed — both Members.
    drop(cluster);
    for lost in ["m-b1", "m-b2"] {
        let location = root.join("members").join(lost);
        std::fs::remove_dir_all(&location).expect("the lost Region's member state is destroyed");
        assert!(!location.exists(), "{lost} is gone");
    }

    // The survivors reopen from their own durable state and every one of
    // them READS the acknowledged write back — zero acknowledged-write
    // loss, in every surviving Region.
    let surviving: Vec<MemberSpec> =
        spec.iter().filter(|seat| seat.region != "r-beta").cloned().collect();
    assert_eq!(surviving.len(), 4);
    let mut reopened = ClusterHost::open(&root, &surviving, "tenant-a")
        .expect("the surviving Regions reopen from durable state");
    for reader in ["m-a1", "m-a2", "m-g1", "m-g2"] {
        let (value, _) = reopened.client_read(reader, "doc-1").expect("the read answers");
        let value = value.unwrap_or_else(|| panic!("{reader} serves the acknowledged write"));
        assert_eq!(
            value.get("status"),
            Some(&DocumentValue::String("acknowledged-global".into())),
            "{reader} reads the acknowledged body back"
        );
    }

    // The surviving cluster keeps serving: a new write at the global
    // level over the two surviving Regions acknowledges and is readable
    // in both.
    reopened
        .client_write("m-g1", &put("op-2", "doc-2", "post-loss"), LeaderlessLevel::GlobalQuorum)
        .expect("the surviving shape keeps acknowledging globally");
    for reader in ["m-a1", "m-g2"] {
        let (value, _) = reopened.client_read(reader, "doc-2").expect("reads");
        assert!(value.is_some(), "{reader} serves the post-loss write");
    }
    let _ = std::fs::remove_dir_all(&root);
}
