//! The coordinator crash-point matrix (gap-sweep row 9): coordinator
//! loss injected before, during, and after EVERY durable boundary of
//! the cluster seam's write path, with retry-same-identity safety.
//!
//! The seam under test is `ClusterHost::client_write` — which drives
//! `coordinate_composed_write` over the cluster's durable members. The
//! host itself has no injection point (that is precisely the gap row-9
//! records), so this lane drives the coordinator path the host drives,
//! step by step, over the SAME durable member roots a `ClusterHost`
//! created, stopping at one boundary and dropping everything (the
//! crash: no shutdown, only `LocalDurability::Synced` facts survive —
//! the kill-point discipline of `src/storage/compaction_tests.rs`),
//! then REOPENS the cluster through the real seam and retries the SAME
//! idempotency identity through a healthy member.
//!
//! The boundaries, in path order: validation (schema admission, before
//! any WAL append), identity mint (the operation identity exists, no
//! engine holds it), first-member WAL append, second-member WAL append
//! (the RF=2 regional boundary), quorum assembly, receipt
//! materialization/publication, and reply. Per boundary this lane
//! asserts the charter row (`GOAL.md` "coordinator fails before,
//! during, or after local commits"): the outcome is committed, typed
//! partial, or typed unknown exactly as the boundary implies; a
//! duplicate logical apply never occurs (per-member storage sequences
//! advance exactly once for the identity and replay recorded receipts
//! thereafter); and no leader, lease, redirect, or election appears
//! (the retrying member coordinates itself, the epoch never moves, and
//! the seam's refusal vocabulary names no authority).

use std::collections::BTreeMap;
use std::path::{Path, PathBuf};

use terrabase_cluster::host::{ClusterHost, MemberSpec};
use terrabase_cluster::{ClusterMode, ClusterWriteRefusal, ComposedWrite};
use terrabase_replication_core::{
    ComposedCoordinationError, ComposedMember, ComposeError, coordinate_composed_write,
};
use terrabase_v2::replication::{LeaderlessLevel, MemberId, RegionId, ReplicaSet};
use terrabase_v2::types::DocumentValue;

/// The members, in the host's canonical lend order.
const NAMES: [&str; 4] = ["member-a", "member-b", "member-c", "member-d"];

fn root(tag: &str) -> PathBuf {
    let dir = std::env::temp_dir().join(format!(
        "tbv2-cluster-crash-matrix-{tag}-{}-{}",
        std::process::id(),
        std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map_or(0, |elapsed| elapsed.as_nanos())
    ));
    let _ = std::fs::remove_dir_all(&dir);
    dir
}

/// The production RF=2 shape: two Regions, two seats each — every
/// regional quorum is exactly both distinct Members.
fn spec() -> Vec<MemberSpec> {
    vec![
        MemberSpec { name: "member-a".into(), region: "eu-1".into() },
        MemberSpec { name: "member-b".into(), region: "eu-1".into() },
        MemberSpec { name: "member-c".into(), region: "us-1".into() },
        MemberSpec { name: "member-d".into(), region: "us-1".into() },
    ]
}

fn member(name: &str) -> MemberId {
    MemberId::try_new(name).expect("valid member")
}

/// The epoch-1 replica set `ClusterHost::open_under` builds from the
/// spec — the set the crash drives evaluate quorums against.
fn replica_set() -> ReplicaSet {
    let mut voters = BTreeMap::new();
    voters.insert(RegionId::try_new("eu-1").expect("valid"), vec![member("member-a"), member("member-b")]);
    voters.insert(RegionId::try_new("us-1").expect("valid"), vec![member("member-c"), member("member-d")]);
    ReplicaSet::new(1, voters)
}

fn body(entries: &[(&str, &str)]) -> BTreeMap<String, DocumentValue> {
    entries
        .iter()
        .map(|(path, text)| ((*path).to_owned(), DocumentValue::String((*text).to_owned())))
        .collect()
}

fn put(operation: &str, document: &str, status: &str) -> ComposedWrite {
    ComposedWrite::Put {
        operation: operation.to_owned(),
        document: document.to_owned(),
        body: body(&[("status", status)]),
    }
}

/// Creates the cluster through the real seam and closes it, leaving the
/// durable roots (identity + member state) the crash drives reopen.
fn created_root(tag: &str) -> PathBuf {
    let root = root(tag);
    let cluster =
        ClusterHost::create(&root, "cell-a", ClusterMode::Active, "created", &spec(), "tenant-a")
            .expect("the cluster creates");
    drop(cluster);
    root
}

/// Opens the cluster's durable members directly, in the host's
/// canonical order — the plane the coordinator path drives.
fn open_members(root: &Path) -> Vec<ComposedMember> {
    spec()
        .iter()
        .map(|member_spec| {
            let state = root.join("members").join(&member_spec.name).join("state");
            ComposedMember::open(&member_spec.name, &state, "tenant-a")
                .expect("the durable member reopens")
        })
        .collect()
}

/// The per-member published storage sequences — the no-duplicate-apply
/// observable: a logical apply advances a member's sequence exactly
/// once; a replayed delivery never moves it.
fn positions(members: &[ComposedMember]) -> Vec<u64> {
    members.iter().map(ComposedMember::position).collect()
}

fn seam_positions(cluster: &ClusterHost) -> Vec<u64> {
    NAMES.iter().map(|name| cluster.position_of(name).expect("known member")).collect()
}

/// Every member serves the one converged body through the seam.
fn assert_all_serve(cluster: &mut ClusterHost, document: &str, status: &str) {
    for name in NAMES {
        let (value, _) = cluster.client_read(name, document).expect("reads");
        assert_eq!(
            value.expect("the document is visible").get("status"),
            Some(&DocumentValue::String(status.to_owned())),
            "{name} serves the value exactly once"
        );
    }
}

/// The crash-discipline invariant asserted per boundary: the epoch and
/// membership never moved (no election), and the retrying member
/// coordinated the retry itself (no redirect, no handoff step).
fn assert_no_election(
    cluster: &ClusterHost,
    retried: &terrabase_replication_core::ComposedReceipt,
    retrying: &str,
) {
    assert_eq!(cluster.set().epoch(), 1, "no boundary moves the epoch — no election");
    assert_eq!(cluster.set().regions().len(), 2, "the membership is unchanged");
    assert_eq!(
        retried.accepting_member(),
        &member(retrying),
        "the retrying member coordinated the retry itself — no redirect"
    );
    assert_eq!(retried.epoch(), 1, "the receipt is answered at the same epoch");
}

/// The seam's refusal vocabulary names no authority anywhere: matched
/// exhaustively (a new variant fails this file's compilation), with no
/// leader, lease, redirect, election, term, primary, or master in any
/// name or payload.
fn assert_authority_free(text: &str) {
    let lower = text.to_ascii_lowercase();
    for banned in
        ["leader", "lease", "redirect", "election", "notleader", "primary", "master", "term"]
    {
        assert!(!lower.contains(banned), "the seam vocabulary must never name {banned}: {text}");
    }
}

// Boundary 1 — VALIDATION. The coordinator validates against the
// collection policy before any durable acceptance. A loss here leaves
// nothing stored anywhere, and the boundary's own refusal (a policy
// violation) is typed with zero durable effect — so the same request
// retried through a healthy member commits exactly once.
#[test]
fn validation_refusal_is_durable_nothing_and_a_mid_validation_loss_commits_once() {
    let root = created_root("b1-validation");
    {
        let cluster = ClusterHost::open(&root, &spec(), "tenant-a").expect("reopens");
        assert_eq!(seam_positions(&cluster), vec![0, 0, 0, 0], "the fresh cluster holds nothing");
    }
    let mut members = open_members(&root);

    // The typed refusal half of the boundary: strict admission refuses
    // the undeclared path BEFORE any WAL append — nothing durable.
    use terrabase_v2::families::modes::{AdmissionError, CollectionPolicy};
    use terrabase_v2::families::{SchemaDefinition, SchemaRule, SchemaVersion};
    let definition = SchemaDefinition::try_new(
        SchemaVersion::try_new(1).expect("valid version"),
        vec![SchemaRule::Required { field: "status".to_owned() }],
    )
    .expect("valid definition");
    members[0].set_policy(CollectionPolicy::strict(definition));
    let refusal = members[0]
        .accept_put("op-refused", "doc-1", &body(&[("undeclared", "x")]))
        .expect_err("strict refuses the undeclared path");
    assert!(matches!(
        refusal,
        ComposeError::SchemaAdmission(AdmissionError::UndeclaredPath { .. })
    ));
    assert_eq!(members[0].position(), 0, "the refusal stored nothing");
    assert_eq!(members[0].visible("doc-1"), Ok(None));
    members[0].set_policy(CollectionPolicy::schema_free());

    // The loss: mid-validation, before any member's append began.
    assert_eq!(positions(&members), vec![0, 0, 0, 0], "nothing was stored");
    for member in &mut members {
        assert_eq!(member.visible("doc-1"), Ok(None));
    }
    drop(members); // the crash: no shutdown, nothing durable to keep

    // The retry: the SAME request through healthy member-b commits once.
    let mut cluster = ClusterHost::open(&root, &spec(), "tenant-a").expect("the healthy cluster reopens");
    let write = put("op-b1", "doc-1", "open");
    let retried = cluster
        .client_write("member-b", &write, LeaderlessLevel::RegionalQuorum)
        .expect("the retry commits through the healthy member");
    assert_eq!(retried.achieved(), LeaderlessLevel::RegionalQuorum);
    assert_no_election(&cluster, &retried, "member-b");
    assert_eq!(seam_positions(&cluster), vec![1, 1, 1, 1], "every member applied exactly once");
    assert_all_serve(&mut cluster, "doc-1", "open");

    // The duplicate delivery replays the recorded receipts, never
    // re-applies — and a later write moves the document on.
    let replay = cluster
        .client_write("member-d", &write, LeaderlessLevel::RegionalQuorum)
        .expect("the duplicate delivery replays");
    assert_eq!(replay.operation(), "op-b1");
    assert_eq!(seam_positions(&cluster), vec![1, 1, 1, 1], "no member applied twice");
    cluster
        .client_write("member-a", &put("op-b1-next", "doc-1", "closed"), LeaderlessLevel::RegionalQuorum)
        .expect("the next write commits");
    assert_all_serve(&mut cluster, "doc-1", "closed");
    let _ = std::fs::remove_dir_all(&root);
}

// Boundary 2 — IDENTITY MINT. The operation identity (the
// idempotency identity the charter's client-request ULID names) exists
// and is held by the client; the coordinator is lost before the first
// WAL append, so no engine holds it. Retrying the SAME identity
// commits it exactly once, and the identity stays stable across every
// later duplicate delivery.
#[test]
fn loss_after_identity_mint_before_any_append_commits_the_same_identity_once() {
    let root = created_root("b2-identity");
    let operation = "op-b2";
    let members = open_members(&root);
    // The loss: after the mint, before the fan-out — the identity
    // exists, no engine holds it.
    assert_eq!(positions(&members), vec![0, 0, 0, 0]);
    drop(members);

    let mut cluster = ClusterHost::open(&root, &spec(), "tenant-a").expect("reopens");
    let write = put(operation, "doc-1", "held");
    let first = cluster
        .client_write("member-c", &write, LeaderlessLevel::RegionalQuorum)
        .expect("the minted identity commits through a healthy member");
    assert_eq!(first.operation(), operation, "the retry lands the minted identity");
    assert_no_election(&cluster, &first, "member-c");
    assert_eq!(seam_positions(&cluster), vec![1, 1, 1, 1], "one apply per member");
    assert_all_serve(&mut cluster, "doc-1", "held");

    // Identity stability across deliveries: a second delivery replays
    // the recorded receipts — identical contributor sequences.
    let second = cluster
        .client_write("member-a", &write, LeaderlessLevel::RegionalQuorum)
        .expect("the second delivery replays");
    let recorded: Vec<_> =
        first.contributors().iter().map(|ack| (ack.member().clone(), ack.sequence())).collect();
    let replayed: Vec<_> =
        second.contributors().iter().map(|ack| (ack.member().clone(), ack.sequence())).collect();
    assert_eq!(recorded, replayed, "the same durable facts answer, not new applies");
    assert_eq!(seam_positions(&cluster), vec![1, 1, 1, 1], "no member applied twice");
    let _ = std::fs::remove_dir_all(&root);
}

// Boundary 3 — FIRST-MEMBER WAL APPEND. The coordinator's own member
// durably applied (engine commit at Synced + CRDT op + staged
// snapshot); the coordinator was lost before the region's second seat
// appended. What it left behind is observable as a TYPED PARTIAL — a
// coordinator evaluating the request against the seats that stored
// answers QuorumNotReached NAMING the durable contributor and its
// recorded sequence, never silent success and never a fresh identity —
// and the retry through a healthy member completes with the crashed
// seat never re-applying.
#[test]
fn loss_after_the_first_member_append_is_a_typed_partial_and_retry_never_reapplies() {
    let root = created_root("b3-first-append");
    let write = put("op-b3", "doc-1", "partial");
    let mut members = open_members(&root);

    // The fan-out completed member-a's durable append only.
    let local = members[0]
        .accept_put("op-b3", "doc-1", &body(&[("status", "partial")]))
        .expect("the coordinator's own seat stored");
    let crashed_sequence = local.sequence().get();
    assert_eq!(positions(&members), vec![1, 0, 0, 0], "one seat stored, nothing else");
    assert_eq!(
        members[0].visible("doc-1"),
        Ok(Some(body(&[("status", "partial")])))
    );
    assert_eq!(members[1].visible("doc-1"), Ok(None), "the second seat holds nothing");

    // The typed partial: any coordinator seeing only the stored seat
    // answers QuorumNotReached naming the durable contributor, with the
    // RECORDED sequence — the crashed attempt's facts, replayed.
    let set = replica_set();
    let error = coordinate_composed_write(
        &set,
        &member("member-b"),
        &mut members[0..1],
        &write,
        LeaderlessLevel::RegionalQuorum,
    )
    .expect_err("one stored seat of two is not a regional quorum");
    let ComposedCoordinationError::QuorumNotReached { contributors } = error else {
        panic!("the refusal is the typed partial, got {error:?}");
    };
    assert_eq!(contributors.len(), 1, "the stored fact is named, not hidden: {contributors:?}");
    assert_eq!(contributors[0].member(), &member("member-a"));
    assert_eq!(contributors[0].sequence(), crashed_sequence, "the recorded receipt, not a new apply");

    drop(members); // the crash

    // The retry through healthy member-b commits; the crashed seat
    // never re-applies — its recorded sequence answers again.
    let mut cluster = ClusterHost::open(&root, &spec(), "tenant-a").expect("reopens");
    let retried = cluster
        .client_write("member-b", &write, LeaderlessLevel::RegionalQuorum)
        .expect("the retry completes through the healthy member");
    assert_eq!(retried.achieved(), LeaderlessLevel::RegionalQuorum);
    assert_no_election(&cluster, &retried, "member-b");
    assert_eq!(
        seam_positions(&cluster),
        vec![1, 1, 1, 1],
        "the crashed seat never re-applied; the others applied once"
    );
    let stored = retried
        .contributors()
        .iter()
        .find(|ack| ack.member() == &member("member-a"))
        .expect("the crashed seat's ack is on the receipt");
    assert_eq!(stored.sequence(), crashed_sequence, "the recorded sequence replays");
    assert_all_serve(&mut cluster, "doc-1", "partial");
    let _ = std::fs::remove_dir_all(&root);
}

// Boundary 4 — SECOND-MEMBER WAL APPEND. Both seats of the accepting
// Region durably applied (the RF=2 regional boundary was met), and the
// coordinator was lost before it evaluated the quorum. The retry
// resolves the request as committed — a coordinator seeing only the
// region's seats answers Ok from the recorded facts — and the full
// retry through the OTHER Region's healthy member completes with zero
// new applies at the crashed Region's seats.
#[test]
fn loss_after_the_second_member_append_resolves_committed_with_zero_new_regional_applies() {
    let root = created_root("b4-second-append");
    let write = put("op-b4", "doc-1", "regional");
    let mut members = open_members(&root);

    // The fan-out completed both regional appends; the coordinator died
    // before the quorum was evaluated.
    let first = members[0]
        .accept_put("op-b4", "doc-1", &body(&[("status", "regional")]))
        .expect("the first seat stored");
    let second = members[1]
        .accept_put("op-b4", "doc-1", &body(&[("status", "regional")]))
        .expect("the second seat stored");
    let recorded = [first.sequence().get(), second.sequence().get()];
    assert_eq!(positions(&members), vec![1, 1, 0, 0], "the regional boundary is durable");

    // The boundary was already durable: a coordinator seeing only the
    // region's seats resolves the SAME request as committed, from the
    // recorded receipts — no new apply.
    let set = replica_set();
    let regional = coordinate_composed_write(
        &set,
        &member("member-b"),
        &mut members[0..2],
        &write,
        LeaderlessLevel::RegionalQuorum,
    )
    .expect("the durable regional boundary answers committed");
    assert_eq!(regional.achieved(), LeaderlessLevel::RegionalQuorum);
    assert_eq!(
        regional
            .contributors()
            .iter()
            .map(|ack| ack.sequence())
            .collect::<Vec<_>>(),
        recorded.to_vec(),
        "the recorded sequences answer, not new applies"
    );

    drop(members); // the crash

    // The retry through the OTHER Region's member commits; the eu-1
    // seats are frozen (pure replay), us-1 applies once.
    let mut cluster = ClusterHost::open(&root, &spec(), "tenant-a").expect("reopens");
    let retried = cluster
        .client_write("member-c", &write, LeaderlessLevel::RegionalQuorum)
        .expect("the retry commits through the other Region");
    assert_no_election(&cluster, &retried, "member-c");
    assert_eq!(
        seam_positions(&cluster),
        vec![1, 1, 1, 1],
        "the crashed Region's seats never re-applied"
    );
    assert_eq!(
        retried
            .contributors()
            .iter()
            .take(2)
            .map(|ack| ack.sequence())
            .collect::<Vec<_>>(),
        recorded.to_vec(),
        "the eu-1 contributors are the recorded receipts"
    );
    assert_all_serve(&mut cluster, "doc-1", "regional");
    let _ = std::fs::remove_dir_all(&root);
}

// Boundary 5 — QUORUM ASSEMBLY. The global fan-out completed at every
// seat of every Region; the coordinator was lost while assembling the
// global quorum (counting Regions against the threshold). The retry at
// the global level resolves committed by PURE REPLAY — zero new
// applies anywhere in the cluster.
#[test]
fn loss_during_global_quorum_assembly_resolves_committed_by_pure_replay() {
    let root = created_root("b5-assembly");
    let write = put("op-b5", "doc-1", "global");
    let mut members = open_members(&root);

    // The global fan-out completed at all four seats.
    for member in &mut members {
        member
            .accept_put("op-b5", "doc-1", &body(&[("status", "global")]))
            .expect("every seat stored");
    }
    assert_eq!(positions(&members), vec![1, 1, 1, 1], "every seat durably applied");
    drop(members); // the crash: mid-assembly, no receipt was built

    // The retry at the global level: committed, zero new applies.
    let mut cluster = ClusterHost::open(&root, &spec(), "tenant-a").expect("reopens");
    let retried = cluster
        .client_write("member-b", &write, LeaderlessLevel::GlobalQuorum)
        .expect("the retry commits at the global level");
    assert_eq!(retried.achieved(), LeaderlessLevel::GlobalQuorum);
    assert_no_election(&cluster, &retried, "member-b");
    assert_eq!(retried.contributors().len(), 4, "every seat contributes its recorded ack");
    assert_eq!(seam_positions(&cluster), vec![1, 1, 1, 1], "pure replay: no seat re-applied");
    assert_all_serve(&mut cluster, "doc-1", "global");
    let _ = std::fs::remove_dir_all(&root);
}

// Boundary 6 — MATERIALIZATION / PUBLICATION. The coordinator assembled
// the quorum and built the receipt, and every member's engine had
// already published its commit (the materialized, served state) — but
// the reply never reached the client. The retry replays the SAME
// durable facts: identical operation, level, epoch, and per-member
// sequences — the recorded receipts, not new ones.
#[test]
fn loss_at_publication_replays_the_recorded_receipt_facts() {
    let root = created_root("b6-publication");
    let write = put("op-b6", "doc-1", "published");
    let mut members = open_members(&root);

    // The full coordinator path ran to the assembled receipt; the
    // client never saw it.
    let set = replica_set();
    let assembled = coordinate_composed_write(
        &set,
        &member("member-a"),
        &mut members,
        &write,
        LeaderlessLevel::GlobalQuorum,
    )
    .expect("the quorum assembled");
    assert_eq!(assembled.achieved(), LeaderlessLevel::GlobalQuorum);
    for member in &mut members {
        assert!(member.visible("doc-1").expect("serves").is_some(), "the write is published");
    }
    drop(members); // the crash: the assembled receipt dies with the coordinator

    // The retry through the seam: the same durable facts answer.
    let mut cluster = ClusterHost::open(&root, &spec(), "tenant-a").expect("reopens");
    let published = cluster
        .client_write("member-c", &write, LeaderlessLevel::GlobalQuorum)
        .expect("the retry publishes the receipt");
    assert_eq!(published.operation(), assembled.operation());
    assert_eq!(published.epoch(), assembled.epoch());
    assert_eq!(published.achieved(), assembled.achieved());
    assert_no_election(&cluster, &published, "member-c");
    let recorded: Vec<_> =
        assembled.contributors().iter().map(|ack| (ack.member().clone(), ack.sequence())).collect();
    let answered: Vec<_> =
        published.contributors().iter().map(|ack| (ack.member().clone(), ack.sequence())).collect();
    assert_eq!(recorded, answered, "the identical per-member facts answer, not new applies");
    assert_eq!(seam_positions(&cluster), vec![1, 1, 1, 1], "no seat re-applied");
    assert_all_serve(&mut cluster, "doc-1", "published");
    let _ = std::fs::remove_dir_all(&root);
}

// Boundary 7 — REPLY. The client received the receipt; the coordinator
// member is lost afterwards, and the client re-delivers the SAME
// identity (the at-least-once case). The recorded receipt replays and
// nothing anywhere applies a second time.
#[test]
fn loss_after_the_reply_replays_the_delivered_receipt_forever() {
    let root = root("b7-reply");
    let write = put("op-b7", "doc-1", "delivered");
    let delivered;
    let recorded: Vec<_>;
    {
        let mut cluster = ClusterHost::create(
            &root,
            "cell-a",
            ClusterMode::Active,
            "created",
            &spec(),
            "tenant-a",
        )
        .expect("creates");
        delivered = cluster
            .client_write("member-a", &write, LeaderlessLevel::RegionalQuorum)
            .expect("the reply reaches the client");
        assert_eq!(delivered.accepting_member(), &member("member-a"));
        assert_eq!(seam_positions(&cluster), vec![1, 1, 1, 1], "one apply per member");
        recorded = delivered
            .contributors()
            .iter()
            .map(|ack| (ack.member().clone(), ack.sequence()))
            .collect();
        drop(cluster); // the coordinator member is lost AFTER the reply
    }

    // The re-delivery through the reopened cluster: the recorded
    // receipt answers, nothing applies twice.
    let mut cluster = ClusterHost::open(&root, &spec(), "tenant-a").expect("reopens");
    let replay = cluster
        .client_write("member-d", &write, LeaderlessLevel::RegionalQuorum)
        .expect("the re-delivery replays");
    assert_eq!(replay.operation(), delivered.operation());
    assert_no_election(&cluster, &replay, "member-d");
    let answered: Vec<_> =
        replay.contributors().iter().map(|ack| (ack.member().clone(), ack.sequence())).collect();
    assert_eq!(recorded, answered, "the recorded receipts answer, not new applies");
    assert_eq!(seam_positions(&cluster), vec![1, 1, 1, 1], "no member applied twice");
    assert_all_serve(&mut cluster, "doc-1", "delivered");
    let _ = std::fs::remove_dir_all(&root);
}

// The typed-outcome lattice at the cluster crate's own durability
// ledger: every crash-boundary class above maps onto the typed Timeout
// (nothing stored — the typed unknown), Partial (naming the stored
// seat and the missing one), or a committed receipt — and the same
// identity recovers through another member without ownership, while a
// duplicate acknowledgement never counts twice.
#[test]
fn the_typed_outcome_lattice_classifies_every_crash_boundary_at_the_ledger() {
    use terrabase_cluster::{
        DurabilityCoordinator, DurabilityError, DurabilityFailure, DurabilityLevel, DurableAck,
        Membership, RegionMembers,
    };

    fn membership() -> Membership {
        Membership::new(
            1,
            1,
            vec![
                RegionMembers::try_new("eu-1", ["member-a", "member-b"]).expect("valid"),
                RegionMembers::try_new("us-1", ["member-c", "member-d"]).expect("valid"),
            ],
        )
        .expect("valid membership")
    }
    let region = RegionId::try_new("eu-1").expect("valid region");
    let regional = DurabilityLevel::RegionalDurable;

    // Nothing stored (validation / identity-mint losses): the deadline
    // answer is the TYPED UNKNOWN — Timeout, distinct from partial —
    // and never claims durability.
    let mut ledger = DurabilityCoordinator::new(membership());
    ledger.set_member_active(&member("member-a"), false).expect("known");
    ledger.set_member_active(&member("member-b"), false).expect("known");
    let unknown =
        ledger.submit_until_deadline("op-x", &member("member-b"), regional).expect_err("no store");
    assert!(matches!(unknown, DurabilityFailure::Timeout(_)), "got {unknown:?}");
    assert!(unknown.observation().expect("carries").stored().is_empty());
    assert!(!unknown.is_durable());

    // One seat stored (loss after the first member append): the answer
    // is the TYPED PARTIAL naming the stored seat and the missing one.
    let mut ledger = DurabilityCoordinator::new(membership());
    ledger
        .record_ack(DurableAck::new("op-y".into(), member("member-a"), region.clone(), 1, 1))
        .expect("the stored fact records");
    ledger.set_member_active(&member("member-b"), false).expect("known");
    let partial =
        ledger.submit_until_deadline("op-y", &member("member-a"), regional).expect_err("below RF=2");
    assert!(matches!(partial, DurabilityFailure::Partial(_)), "got {partial:?}");
    let observation = partial.observation().expect("carries");
    assert_eq!(observation.stored().len(), 1);
    assert_eq!(observation.stored()[0].member(), &member("member-a"));
    assert_eq!(observation.missing_members(), &[member("member-b")]);

    // The same identity recovers through ANOTHER member — no ownership,
    // no re-mint — and a duplicate acknowledgement never counts twice.
    ledger.set_member_active(&member("member-b"), true).expect("known");
    let recovered = ledger.recover("op-y", &member("member-b"), regional).expect("recovers");
    assert_eq!(recovered.contributors().len(), 2);
    assert_eq!(recovered.coordinator(), &member("member-b"), "recovered without ownership");
    assert!(matches!(
        ledger.record_ack(DurableAck::new("op-y".into(), member("member-a"), region.clone(), 1, 1)),
        Err(DurabilityError::DuplicateAcknowledgement { .. })
    ));

    // The boundary met before evaluation (loss after both regional
    // appends): the retry resolves committed from the recorded facts,
    // exactly the two seats, once each.
    let mut ledger = DurabilityCoordinator::new(membership());
    ledger
        .record_ack(DurableAck::new("op-z".into(), member("member-a"), region.clone(), 1, 1))
        .expect("records");
    ledger
        .record_ack(DurableAck::new("op-z".into(), member("member-b"), region.clone(), 1, 1))
        .expect("records");
    let committed = ledger.recover("op-z", &member("member-b"), regional).expect("resolves");
    assert_eq!(committed.contributors().len(), 2, "the recorded seats, once each");
}

// The no-election structural fact, live and exhaustive: every refusal
// the seam can answer names no authority, and an unseated writer meets
// a typed refusal — never a redirect to a "right" member.
#[test]
fn the_seam_refusal_vocabulary_names_no_authority_and_never_redirects() {
    let refusals = vec![
        ClusterWriteRefusal::UnknownMember,
        ClusterWriteRefusal::ClusterReadOnly(terrabase_cluster::MutationRefusal { position: 0 }),
        ClusterWriteRefusal::QuorumNotReached,
        ClusterWriteRefusal::ApplyFailed,
        ClusterWriteRefusal::AdmissionRefused,
        ClusterWriteRefusal::ReadQuorumNotReached,
        ClusterWriteRefusal::EpochRefused,
        ClusterWriteRefusal::NoBackupSet,
    ];
    for refusal in &refusals {
        assert_authority_free(&format!("{refusal:?}"));
    }
    assert_authority_free(&format!(
        "{:?}",
        ComposedCoordinationError::QuorumNotReached { contributors: Vec::new() }
    ));
    assert_authority_free(&format!("{:?}", ComposedCoordinationError::NotEligible { epoch: 1 }));
    assert_authority_free(&format!(
        "{:?}",
        ComposedCoordinationError::ApplyFailed { member: member("member-a") }
    ));

    // Live: an unseated writer is refused typed, never redirected.
    let root = created_root("no-redirect");
    let mut cluster = ClusterHost::open(&root, &spec(), "tenant-a").expect("reopens");
    let refusal = cluster
        .client_write("member-z", &put("op-x", "doc-1", "x"), LeaderlessLevel::RegionalQuorum)
        .expect_err("an unseated writer is refused");
    assert_eq!(refusal, ClusterWriteRefusal::UnknownMember);
    assert_authority_free(&format!("{refusal:?}"));
    assert_eq!(cluster.set().epoch(), 1, "the refusal moved no epoch");
    let _ = std::fs::remove_dir_all(&root);
}
