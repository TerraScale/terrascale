//! Backup-last routing and minimum-frontier reads (P5): the frozen
//! read routing order of `p0-backup-cluster-contract` §5.
//!
//! The order is frozen: (1) local active NVMe, (2) home S3 Region,
//! (3) eligible active Regions over QUIC, (4) the backup cluster —
//! last resort only. In this in-process model the active cluster's
//! local read stands for steps 1-3 (the S3 and QUIC legs are the
//! engine-side sources of the same active database; they are recorded
//! as scoping, not skipped silently): while the active serves, the
//! backup is never touched. A backup read returns the latest applied
//! value plus EXACT replication position and lag — a client can never
//! mistake a stale result for a current one.
//!
//! Minimum-frontier reads: the caller may require `frontier >= F`; the
//! backup waits within the deadline to reach `F` or fails
//! `FrontierUnreachable` — it never returns older state silently.

use std::collections::BTreeMap;
use std::time::{Duration, Instant};

use terrabase_v2::types::DocumentValue;

use crate::backup::{BackupOutcome, BackupPolicy, BackupSet};
use crate::host::{ClusterHost, ClusterWriteRefusal};
use crate::identity::ClusterMode;

/// Which source served a backup-last read — receipts name the chosen
/// source, per the contract.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ReadSource {
    /// The active cluster's own materialized state (the frozen order's
    /// steps 1-3: local NVMe, home S3, active Regions over QUIC).
    Active,
    /// The backup cluster — last resort only.
    Backup,
}

/// One backup-last read's answer: the value, the exact position, the
/// lag behind the active frontier, and the source that served it.
#[derive(Clone, Debug, PartialEq)]
pub struct BackupLastRead {
    /// The latest applied value (`None` when the document is absent
    /// everywhere — an honest miss, not an error).
    pub value: Option<BTreeMap<String, DocumentValue>>,
    /// The exact replication position of the source that served.
    pub position: u64,
    /// How far behind the active frontier the source is.
    pub lag: u64,
    /// The source that served.
    pub source: ReadSource,
}

/// Why a backup-last read failed.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ReadRoutingError {
    /// The active cluster refused the read (its typed refusal passes
    /// through — the router never masks it).
    ActiveRefused(ClusterWriteRefusal),
    /// The minimum frontier was not reached within the deadline. The
    /// exact position is attached: older state was NEVER returned
    /// silently.
    FrontierUnreachable {
        /// The frontier the caller required.
        required: u64,
        /// The backup's position when the deadline passed.
        position: u64,
    },
    /// No backup set is attached and the active could not serve.
    NoBackup,
}

/// The backup-last router: one active cluster and its optional backup
/// set, reading in the frozen order.
pub struct BackupLastRouter<'a> {
    active: &'a mut ClusterHost,
    backup: Option<&'a mut BackupSet>,
    /// The active frontier as last observed — the lag reference when
    /// the active itself is unreadable.
    last_active_position: u64,
    /// The active-side read refusal of the last read, when one was
    /// held for the no-backup case.
    refused: Option<ClusterWriteRefusal>,
}

impl<'a> BackupLastRouter<'a> {
    /// Builds the router over one active cluster and its backup set.
    ///
    /// # Panics
    ///
    /// Panics when the cluster is not active — backup-last routing is
    /// an ACTIVE-side read policy; a backup cluster serves its own
    /// reads directly.
    pub fn new(active: &'a mut ClusterHost, backup: Option<&'a mut BackupSet>) -> Self {
        assert_ne!(active.mode(), ClusterMode::Backup, "backup-last routing is active-side");
        let mut router = Self { active, backup, last_active_position: 0, refused: None };
        router.observe_active_frontier();
        router
    }

    /// Reads one document in the frozen order. While the active
    /// cluster serves, the backup is never touched; when the active
    /// cannot serve (its typed refusal), the backup answers with the
    /// latest applied value plus exact position and lag.
    ///
    /// # Errors
    ///
    /// Returns [`ReadRoutingError::ActiveRefused`] when the active
    /// refused and no backup is attached to fall back on.
    pub fn read(
        &mut self,
        member: &str,
        document: &str,
    ) -> Result<BackupLastRead, ReadRoutingError> {
        self.read_inner(member, document, None)
    }

    /// Reads one document requiring `frontier >= required`: the backup
    /// waits within the deadline to reach it or fails
    /// [`ReadRoutingError::FrontierUnreachable`] — never older state
    /// silently. The frontier requirement gates the backup leg; the
    /// active leg serves only its own current state.
    ///
    /// # Errors
    ///
    /// As [`Self::read`], plus the frontier failure.
    pub fn read_with_frontier(
        &mut self,
        member: &str,
        document: &str,
        required: u64,
        deadline: Instant,
    ) -> Result<BackupLastRead, ReadRoutingError> {
        self.read_inner(member, document, Some((required, deadline)))
    }

    fn read_inner(
        &mut self,
        member: &str,
        document: &str,
        required: Option<(u64, Instant)>,
    ) -> Result<BackupLastRead, ReadRoutingError> {
        self.observe_active_frontier();
        // Steps 1-3 of the frozen order: the active database's own
        // sources. A successful active read never touches the backup.
        match self.active.client_read(member, document) {
            Ok((value, _repair)) => {
                let position = self.active.position_of(member).unwrap_or(0);
                return Ok(BackupLastRead { value, position, lag: 0, source: ReadSource::Active });
            }
            // An unknown member or a broken quorum is the active
            // refusing — hold the refusal for the no-backup case.
            Err(refusal) => self.refused = Some(refusal),
        }
        // Step 4: the backup — last resort only.
        let Some(backup) = self.backup.as_deref_mut() else {
            return Err(ReadRoutingError::ActiveRefused(
                self.refused.take().unwrap_or(ClusterWriteRefusal::ReadQuorumNotReached),
            ));
        };
        // The minimum-frontier wait: poll the backup's position until
        // it covers the requirement or the deadline passes. In-process
        // replication converges between polls (the fan of a write, or
        // a catch-up the caller drives); with no convergence the
        // deadline decides — and the failure carries the truth.
        let (required, deadline) = required.unwrap_or((0, Instant::now()));
        loop {
            let position = backup.position();
            if position >= required {
                let value = backup.read(document);
                return Ok(BackupLastRead {
                    value,
                    position,
                    lag: self.last_active_position.saturating_sub(position),
                    source: ReadSource::Backup,
                });
            }
            if Instant::now() >= deadline {
                return Err(ReadRoutingError::FrontierUnreachable { required, position });
            }
            let remaining = deadline.duration_since(Instant::now());
            std::thread::sleep(remaining.min(Duration::from_millis(5)));
        }
    }

    /// Fans one write to the backup through the router's set — the
    /// convenience the active side uses to keep the backup's frontier
    /// advancing behind its own.
    pub fn fan_to_backup(
        &mut self,
        write: &crate::ComposedWrite,
        policy: BackupPolicy,
    ) -> Option<BackupOutcome> {
        self.active.fan_to_backup(write, policy)
    }

    /// Observes the active frontier: the furthest-ahead member's
    /// position, the lag reference of every backup answer.
    fn observe_active_frontier(&mut self) {
        let frontier = self
            .active
            .member_names()
            .iter()
            .filter_map(|id| self.active.position_of(id.as_str()).ok())
            .max()
            .unwrap_or(0);
        self.last_active_position = self.last_active_position.max(frontier);
    }
}

#[cfg(test)]
mod tests {
    use std::collections::BTreeMap;
    use std::time::{Duration, Instant};

    use terrabase_replication_core::compose::ComposedWrite;
    use terrabase_v2::replication::LeaderlessLevel;
    use terrabase_v2::types::DocumentValue;

    use super::{BackupLastRouter, ReadRoutingError, ReadSource};
    use crate::backup::{BackupPolicy, BackupSet};
    use crate::host::{ClusterHost, MemberSpec};
    use crate::identity::ClusterMode;

    fn dir(tag: &str) -> std::path::PathBuf {
        let dir = std::env::temp_dir().join(format!(
            "tbv2-cluster-reads-{tag}-{}-{}",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .map_or(0, |elapsed| elapsed.as_nanos())
        ));
        let _ = std::fs::remove_dir_all(&dir);
        std::fs::create_dir_all(&dir).unwrap();
        dir
    }

    fn active_spec() -> Vec<MemberSpec> {
        (1..=3)
            .map(|index| MemberSpec { name: format!("member-{index}"), region: "eu-1".into() })
            .collect()
    }

    fn backup_spec() -> Vec<MemberSpec> {
        (1..=3)
            .map(|index| MemberSpec { name: format!("backup-{index}"), region: "bulk-hdd".into() })
            .collect()
    }

    fn write(operation: &str, document: &str) -> ComposedWrite {
        ComposedWrite::Put {
            operation: operation.to_owned(),
            document: document.to_owned(),
            body: BTreeMap::from([("status".to_owned(), DocumentValue::String("open".to_owned()))]),
        }
    }

    #[test]
    fn while_the_active_serves_the_backup_is_never_touched() {
        let root = dir("active-serves");
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
            .client_write("member-1", &write("op-1", "doc-1"), LeaderlessLevel::RegionalQuorum)
            .expect("commits");
        let mut backup = BackupSet::create(
            &root.join("backup"),
            "cell-a-backup",
            "created",
            &backup_spec(),
            "tenant-a",
        )
        .expect("creates");
        // The backup holds NOTHING yet: if the router touched it, the
        // read would miss.
        let mut router = BackupLastRouter::new(&mut cluster, Some(&mut backup));
        let answer = router.read("member-2", "doc-1").expect("the active serves");
        assert_eq!(answer.source, ReadSource::Active);
        assert!(answer.value.is_some(), "the active's converged value");
        assert_eq!(answer.lag, 0, "the active is its own frontier");
        let _ = std::fs::remove_dir_all(&root);
    }

    #[test]
    fn the_last_resort_read_returns_value_position_and_lag() {
        let root = dir("last-resort");
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
            .client_write("member-1", &write("op-1", "doc-1"), LeaderlessLevel::RegionalQuorum)
            .expect("commits");
        let mut backup = BackupSet::create(
            &root.join("backup"),
            "cell-a-backup",
            "created",
            &backup_spec(),
            "tenant-a",
        )
        .expect("creates");
        // Replication: op-1 fans, op-2 does not — the backup must name
        // its lag behind the active frontier honestly.
        assert!(backup.await_ack(&write("op-1", "doc-1"), BackupPolicy::default()).is_full());
        cluster
            .client_write("member-1", &write("op-2", "doc-2"), LeaderlessLevel::RegionalQuorum)
            .expect("commits");
        let backup_position = backup.position();

        // Force the backup leg: the active refuses at an unknown
        // member, so the frozen order falls through to the backup.
        let mut router = BackupLastRouter::new(&mut cluster, Some(&mut backup));
        let answer =
            router.read("not-a-member", "doc-1").expect("the backup answers as the last resort");
        assert_eq!(answer.source, ReadSource::Backup);
        assert!(answer.value.as_ref().expect("the replicated value").contains_key("status"));
        assert_eq!(answer.position, backup_position, "the exact position");
        assert!(answer.lag > 0, "the unfanned op-2 names the lag: {answer:?}");

        // No backup attached: the active's typed refusal passes
        // through — the router never masks it.
        let mut router = BackupLastRouter::new(&mut cluster, None);
        assert!(matches!(
            router.read("not-a-member", "doc-1"),
            Err(ReadRoutingError::ActiveRefused(_))
        ));
        let _ = std::fs::remove_dir_all(&root);
    }

    #[test]
    fn a_minimum_frontier_serves_at_coverage_and_fails_typed_past_the_deadline() {
        let root = dir("min-frontier");
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
            .client_write("member-1", &write("op-1", "doc-1"), LeaderlessLevel::RegionalQuorum)
            .expect("commits");
        let mut backup = BackupSet::create(
            &root.join("backup"),
            "cell-a-backup",
            "created",
            &backup_spec(),
            "tenant-a",
        )
        .expect("creates");
        assert!(backup.await_ack(&write("op-1", "doc-1"), BackupPolicy::default()).is_full());
        let covered = backup.position();
        assert!(covered >= 1, "the fan advanced the backup: {covered}");

        let mut router = BackupLastRouter::new(&mut cluster, Some(&mut backup));
        // Coverage: the requirement the backup already meets serves,
        // with position and lag named.
        let answer = router
            .read_with_frontier(
                "not-a-member",
                "doc-1",
                covered,
                Instant::now() + Duration::from_millis(200),
            )
            .expect("the covered frontier serves");
        assert_eq!(answer.source, ReadSource::Backup);
        assert_eq!(answer.position, covered);

        // Unreachable: a frontier the backup cannot reach inside a
        // short deadline fails typed — never older state silently.
        let failure = router
            .read_with_frontier(
                "not-a-member",
                "doc-1",
                covered + 1_000,
                Instant::now() + Duration::from_millis(30),
            )
            .expect_err("the unreachable frontier fails");
        assert_eq!(
            failure,
            ReadRoutingError::FrontierUnreachable { required: covered + 1_000, position: covered },
            "the failure carries the truth"
        );
        let _ = std::fs::remove_dir_all(&root);
    }

    #[test]
    fn backup_members_present_in_no_quorum_computation() {
        // The non-voting proof (contract §6 scenario 5): the backup
        // members are structurally absent from every quorum the
        // coordinators compute — they are not in the replica set.
        let root = dir("non-voting");
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
            .expect("creates"),
        );
        let set = cluster.set();
        for member in ["backup-1", "backup-2", "backup-3"] {
            assert!(
                !set.is_voter(
                    &terrabase_v2::replication::MemberId::try_new(member).expect("valid")
                ),
                "the backup member {member} is no voter"
            );
        }
        for member in ["member-1", "member-2", "member-3"] {
            assert!(
                set.is_voter(&terrabase_v2::replication::MemberId::try_new(member).expect("valid")),
                "the active member {member} votes"
            );
        }
        let _ = std::fs::remove_dir_all(&root);
    }
}
