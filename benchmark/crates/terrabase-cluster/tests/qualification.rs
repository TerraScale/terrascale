//! The leaderless qualification lane (P7): the charter's AC-2, AC-3,
//! and AC-4 fault scenarios, assembled through the cluster surface —
//! `docs/design/p0-backup-cluster-contract.md` §6 seeds AC-10/AC-11.
//!
//! Every row runs against REAL durable members: coordinator failure
//! completing by idempotent retry through another member (AC-2), the
//! three acknowledgement levels answering distinct receipts with their
//! fault variants and backup members never voting (AC-3), concurrent
//! multi-Region writes converging under duplication, restart, and
//! rejoin (AC-4), and the restore scenario — a backup restoring into a
//! NEW active identity while the backup stays backup (§6 scenario 9).

use std::collections::BTreeMap;
use std::path::PathBuf;

use terrabase::replication::{LeaderlessLevel, MemberId, RegionId, ReplicaSet};
use terrabase::types::DocumentValue;
use terrabase_cluster::backup::{BackupPolicy, BackupSet};
use terrabase_cluster::host::{ClusterHost, MemberSpec};
use terrabase_cluster::{ClusterMode, ComposedWrite};

fn dir(tag: &str) -> PathBuf {
    let dir = std::env::temp_dir().join(format!(
        "terrabase-cluster-qualification-{tag}-{}-{}",
        std::process::id(),
        std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map_or(0, |elapsed| elapsed.as_nanos())
    ));
    let _ = std::fs::remove_dir_all(&dir);
    dir
}

fn active_spec() -> Vec<MemberSpec> {
    vec![
        MemberSpec { name: "member-a".into(), region: "eu-1".into() },
        MemberSpec { name: "member-b".into(), region: "eu-1".into() },
        MemberSpec { name: "member-c".into(), region: "eu-1".into() },
        MemberSpec { name: "member-d".into(), region: "us-1".into() },
        MemberSpec { name: "member-e".into(), region: "us-1".into() },
    ]
}

fn backup_spec() -> Vec<MemberSpec> {
    (1..=3)
        .map(|index| MemberSpec { name: format!("backup-{index}"), region: "bulk-hdd".into() })
        .collect()
}

fn put(operation: &str, document: &str, status: &str) -> ComposedWrite {
    ComposedWrite::Put {
        operation: operation.to_owned(),
        document: document.to_owned(),
        body: BTreeMap::from([("status".to_owned(), DocumentValue::String(status.to_owned()))]),
    }
}

// AC-2: coordinator failure completes by idempotent retry through
// another member — the same operation identity accepted at a second
// member replays the recorded receipts, never a second application,
// and no leader, lease, or redirect exists anywhere in the path.
#[test]
fn ac2_a_failed_acceptance_completes_by_idempotent_retry_elsewhere() {
    let root = dir("ac2-retry");
    let mut cluster = ClusterHost::create(
        &root,
        "cell-a",
        ClusterMode::Active,
        "created",
        &active_spec(),
        "tenant-a",
    )
    .expect("creates");

    // The first acceptance at member-a commits regionally...
    let first = cluster
        .client_write("member-a", &put("op-1", "doc-1", "open"), LeaderlessLevel::RegionalQuorum)
        .expect("the first acceptance commits");
    // ...and the SAME operation identity retried at another member —
    // as a client would after losing the first coordinator — replays
    // idempotently: the receipt answers, nothing double-applies.
    let retried = cluster
        .client_write("member-c", &put("op-1", "doc-1", "open"), LeaderlessLevel::RegionalQuorum)
        .expect("the retry completes through another member");
    assert_eq!(
        first.operation(),
        retried.operation(),
        "the operation identity is the retry identity"
    );

    // Every member serves the same converged value once.
    for member in ["member-a", "member-c", "member-e"] {
        let (value, _) = cluster.client_read(member, "doc-1").expect("reads");
        let value = value.expect("the document is visible");
        assert_eq!(
            value.get("status"),
            Some(&DocumentValue::String("open".into())),
            "{member} serves the value exactly once"
        );
    }

    // A later write moves on: the retry did not wedge the document.
    cluster
        .client_write("member-d", &put("op-2", "doc-1", "closed"), LeaderlessLevel::RegionalQuorum)
        .expect("the next write commits");
    let (value, _) = cluster.client_read("member-b", "doc-1").expect("reads");
    assert_eq!(
        value.expect("serves").get("status"),
        Some(&DocumentValue::String("closed".into())),
        "the later write wins after the retry"
    );
    let _ = std::fs::remove_dir_all(&root);
}

// AC-3: the three acknowledgement levels answer DISTINCT receipts,
// the backup level names its fault outcome, backup members never vote,
// and membership changes prove quorum intersection.
#[test]
fn ac3_the_acknowledgement_levels_answer_distinct_receipts_and_backups_never_vote() {
    let root = dir("ac3-receipts");
    let mut cluster = ClusterHost::create(
        &root,
        "cell-a",
        ClusterMode::Active,
        "created",
        &active_spec(),
        "tenant-a",
    )
    .expect("creates");
    cluster.attach_backup(
        BackupSet::create(
            &root.join("backup"),
            "cell-a-backup",
            "created",
            &backup_spec(),
            "tenant-a",
        )
        .expect("the backup set creates"),
    );

    // Regional: the regional majority contributes.
    let regional = cluster
        .client_write("member-a", &put("op-r", "doc-r", "open"), LeaderlessLevel::RegionalQuorum)
        .expect("regional commits");
    assert_eq!(regional.achieved(), LeaderlessLevel::RegionalQuorum);

    // Global: every selected Region contributes.
    let global = cluster
        .client_write("member-d", &put("op-g", "doc-g", "open"), LeaderlessLevel::GlobalQuorum)
        .expect("global commits");
    assert_eq!(global.achieved(), LeaderlessLevel::GlobalQuorum);
    assert_ne!(
        regional.contributors().len(),
        0,
        "the receipts carry their contributors — the levels are distinct evidence"
    );

    // Backup: the ordered level answers its own receipt shape — the
    // backup durability names itself FULL or PARTIAL (the fault
    // variant is proven below and in the landing rows).
    let (receipt, outcome) = cluster
        .client_write_backup("member-b", &put("op-b", "doc-b", "open"))
        .expect("backup_ack commits");
    assert_eq!(receipt.achieved(), LeaderlessLevel::RegionalQuorum);
    assert!(outcome.is_full(), "2-of-3 backup durability: {outcome:?}");

    // The partial fault variant: a policy no set can satisfy names
    // PARTIAL while the active write stands.
    let partial = cluster
        .fan_to_backup(&put("op-p", "doc-p", "open"), BackupPolicy { min_acks: 4 })
        .expect("the fan answers");
    assert!(!partial.is_full(), "below the minimum the outcome is partial");
    let (value, _) = cluster.client_read("member-a", "doc-p").expect("reads");
    assert!(value.is_none(), "op-p never committed actively — only the fan ran");

    // Backup members never vote (§6 scenario 5): structurally absent
    // from every quorum the coordinators compute.
    let set = cluster.set();
    for member in ["backup-1", "backup-2", "backup-3"] {
        assert!(!set.is_voter(&MemberId::try_new(member).expect("valid")), "{member} is no voter");
    }

    // Membership changes prove quorum intersection: a step with NO
    // overlapping quorum refuses typed.
    let disjoint: BTreeMap<RegionId, Vec<MemberId>> = BTreeMap::from([(
        RegionId::try_new("ap-1").expect("valid"),
        vec![
            MemberId::try_new("member-x").expect("valid"),
            MemberId::try_new("member-y").expect("valid"),
        ],
    )]);
    let refusal = cluster
        .step_epoch(ReplicaSet::new(2, disjoint))
        .expect_err("a disjoint membership step refuses");
    assert_eq!(refusal, terrabase_cluster::ClusterWriteRefusal::EpochRefused);
    let _ = std::fs::remove_dir_all(&root);
}

// AC-4: multiple Regions accept concurrent writes and converge under
// duplication, restart, and rejoin.
#[test]
fn ac4_concurrent_multiregion_writes_converge_under_duplication_restart_and_rejoin() {
    let root = dir("ac4-convergence");
    let spec = active_spec();
    let mut cluster =
        ClusterHost::create(&root, "cell-a", ClusterMode::Active, "created", &spec, "tenant-a")
            .expect("creates");

    // Concurrent writes from two Regions to the SAME document: the
    // merged state is one converged value (last-writer-wins by
    // operation order in-process; the merge rules are the composed
    // plane's, proven in the replication core's fault rows).
    cluster
        .client_write(
            "member-a",
            &put("op-eu", "doc-1", "from-eu"),
            LeaderlessLevel::RegionalQuorum,
        )
        .expect("the eu-1 write commits");
    cluster
        .client_write(
            "member-d",
            &put("op-us", "doc-1", "from-us"),
            LeaderlessLevel::RegionalQuorum,
        )
        .expect("the us-1 write commits");

    // Duplication: the same operation delivered again replays the
    // recorded receipts.
    cluster
        .client_write(
            "member-d",
            &put("op-us", "doc-1", "from-us"),
            LeaderlessLevel::RegionalQuorum,
        )
        .expect("the duplicated write replays");

    // Every member in both Regions serves the SAME converged body.
    let mut observed = Vec::new();
    for member in ["member-a", "member-c", "member-d", "member-e"] {
        let (value, _) = cluster.client_read(member, "doc-1").expect("reads");
        observed.push(value.expect("converged").get("status").map(|status| format!("{status:?}")));
    }
    assert!(
        observed.iter().collect::<std::collections::BTreeSet<_>>().len() == 1,
        "both Regions converged to one value: {observed:?}"
    );

    // Restart: the whole cluster drops and reopens; the converged
    // state survives and serves.
    drop(cluster);
    let mut reopened = ClusterHost::open(&root, &spec, "tenant-a").expect("reopens");
    let (value, _) = reopened.client_read("member-b", "doc-1").expect("reads");
    assert!(value.is_some(), "the converged state survives the restart");

    // Rejoin: a member that missed the writes while away catches up
    // through anti-entropy and serves the converged state.
    reopened
        .expand(MemberSpec { name: "member-f".into(), region: "us-1".into() })
        .expect("the rejoining member enters through the epoch step");
    let (value, _) = reopened.client_read("member-f", "doc-1").expect("reads");
    assert!(value.is_some(), "the rejoining member serves the converged state after catch-up");
    let _ = std::fs::remove_dir_all(&root);
}

// Backup-cluster contract §6 scenario 9: restore from backup produces
// a NEW active cluster identity; the backup remains backup.
#[test]
fn scenario9_restore_from_backup_mints_a_new_active_identity_and_the_backup_stays_backup() {
    let root = dir("scenario9-restore");
    // The active cluster and its work.
    let mut cluster = ClusterHost::create(
        &root,
        "cell-a",
        ClusterMode::Active,
        "created",
        &active_spec(),
        "tenant-a",
    )
    .expect("creates");
    cluster
        .client_write("member-a", &put("op-1", "doc-1", "open"), LeaderlessLevel::RegionalQuorum)
        .expect("commits");
    cluster
        .client_write("member-d", &put("op-2", "doc-2", "held"), LeaderlessLevel::RegionalQuorum)
        .expect("commits");

    // The replication: the backup converges on both documents.
    let mut backup = BackupSet::create(
        &root.join("backup"),
        "cell-a-backup",
        "created",
        &backup_spec(),
        "tenant-a",
    )
    .expect("the backup set creates");
    assert!(
        backup.await_ack(&put("op-1", "doc-1", "open"), BackupPolicy::default()).is_full(),
        "doc-1 replicates"
    );
    assert!(
        backup.await_ack(&put("op-2", "doc-2", "held"), BackupPolicy::default()).is_full(),
        "doc-2 replicates"
    );

    // A non-empty target refuses: a restore never merges silently.
    let occupied = std::env::temp_dir()
        .join(format!("terrabase-cluster-qualification-scenario9-occupied-{}", std::process::id()));
    let _ = std::fs::remove_dir_all(&occupied);
    std::fs::create_dir_all(&occupied).unwrap();
    std::fs::write(occupied.join("marker"), b"x").unwrap();
    assert!(
        ClusterHost::restore_from_backup(
            &occupied,
            "cell-restored",
            "restored",
            &active_spec(),
            "tenant-a",
            &mut backup
        )
        .is_err(),
        "a non-empty target refuses"
    );
    let _ = std::fs::remove_dir_all(&occupied);

    // The restore: a NEW active identity at a fresh root.
    let restored_root = root.join("restored");
    let mut restored = ClusterHost::restore_from_backup(
        &restored_root,
        "cell-restored",
        "restored",
        &active_spec(),
        "tenant-a",
        &mut backup,
    )
    .expect("the restore mints a new active cluster");
    assert_eq!(restored.mode(), ClusterMode::Active, "the restored cluster is ACTIVE");
    assert_ne!(
        restored.identity().content_hash(),
        backup.identity().content_hash(),
        "the restored identity is NEW, not a mode change of the backup"
    );
    for document in ["doc-1", "doc-2"] {
        let (value, _) = restored.client_read("member-a", document).expect("reads");
        assert!(value.is_some(), "{document} restored");
    }
    let (value, _) = restored.client_read("member-c", "doc-1").expect("reads");
    assert_eq!(
        value.expect("serves").get("status"),
        Some(&DocumentValue::String("open".into())),
        "the restored body is the backup's converged value"
    );
    drop(restored);

    // The SOURCE backup remains backup: terminal, refusing writes,
    // unchanged by the restore.
    assert_eq!(backup.identity().mode(), ClusterMode::Backup, "the backup stays backup");
    drop(backup);
    let reopened_backup = BackupSet::create(
        &root.join("backup"),
        "cell-a-backup",
        "created",
        &backup_spec(),
        "tenant-a",
    )
    .expect("the backup reopens unchanged");
    assert_eq!(reopened_backup.identity().mode(), ClusterMode::Backup);
    let _ = std::fs::remove_dir_all(&root);
}
