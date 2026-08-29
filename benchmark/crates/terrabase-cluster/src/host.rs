//! The multi-member cluster host (P5): N durable members under one
//! immutable identity and one replica set, driven through the P2
//! coordinators — any member accepts, the quorums decide.
//!
//! The host is the in-process cluster the serving seam binds: writes
//! fan out through [`coordinate_composed_write`] at the requested
//! acknowledgement level, reads repair through
//! [`quorum_read_with_repair`], and membership changes step through
//! [`accept_epoch`] with the overlapping-quorum precondition. A backup
//! host refuses every client write permanently at the door; both modes
//! read (the backup is the last resort of the frozen routing order).

use std::collections::BTreeMap;
use std::path::{Path, PathBuf};

use terrabase_replication_core::compose::{
    self, coordinate_composed_write, quorum_read_with_repair, ComposedWrite,
};
use terrabase_v2::replication::{LeaderlessLevel, MemberId, RegionId, ReplicaSet};
use terrabase_v2::types::DocumentValue;

use crate::backup::{BackupOutcome, BackupPolicy, BackupSet};
use crate::identity::{ClusterIdentity, ClusterMode, IdentityError};
use crate::member::{ClusterMember, MemberError, MutationRefusal};

/// One member of the cluster's shape: its name and its Region.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct MemberSpec {
    /// The member's durable name.
    pub name: String,
    /// The member's Region.
    pub region: String,
}

/// Why a cluster-host operation failed.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ClusterError {
    /// The identity failed to create, open, or verify.
    Identity(IdentityError),
    /// One member's durable state failed.
    Member(MemberError),
    /// The named member is not part of this cluster.
    UnknownMember,
    /// The cluster's shape is invalid (an empty voter map, or a member
    /// listed outside it).
    InvalidShape,
    /// The write did not reach its quorum.
    QuorumNotReached,
    /// A member's apply failed inside the coordinator.
    ApplyFailed,
    /// The coordinated write was refused at admission.
    AdmissionRefused,
    /// The membership epoch step refused (no overlapping quorum, or
    /// not a step forward).
    EpochRefused,
}

/// The multi-member cluster host: the identity, the replica set, and
/// the durable members, in one place. The composed members live in one
/// canonical-order vector — the coordinators drive a contiguous
/// `&mut [ComposedMember]` — with the member ids beside them.
pub struct ClusterHost {
    identity: ClusterIdentity,
    set: ReplicaSet,
    root: PathBuf,
    tenant: String,
    ids: Vec<MemberId>,
    members: Vec<ClusterMember>,
    backup: Option<BackupSet>,
}

impl ClusterHost {
    /// Creates a cluster in one step: the identity (mode frozen now),
    /// one member per spec at `<root>/members/<name>`, and the
    /// epoch-1 replica set of the spec's Regions.
    ///
    /// # Errors
    ///
    /// Returns [`ClusterError::Identity`] for an invalid identity,
    /// [`ClusterError::InvalidShape`] for an empty spec, and
    /// [`ClusterError::Member`] when a member's state cannot open.
    pub fn create(
        root: &Path,
        cluster: &str,
        mode: ClusterMode,
        creation_record: &str,
        spec: &[MemberSpec],
        tenant: &str,
    ) -> Result<Self, ClusterError> {
        if spec.is_empty() {
            return Err(ClusterError::InvalidShape);
        }
        let identity = ClusterIdentity::create(cluster, mode, creation_record)
            .map_err(ClusterError::Identity)?;
        identity.pin(root).map_err(ClusterError::Identity)?;
        Self::open_under(identity, root, spec, tenant)
    }

    /// Opens an existing cluster: the identity verifies, every member
    /// reopens under it, and the epoch-1 replica set rebuilds from the
    /// spec — the durable epoch records the members hold govern later
    /// steps (the config plane reconciles them).
    ///
    /// # Errors
    ///
    /// As [`Self::create`].
    pub fn open(root: &Path, spec: &[MemberSpec], tenant: &str) -> Result<Self, ClusterError> {
        let identity = ClusterIdentity::open(root).map_err(ClusterError::Identity)?;
        Self::open_under(identity, root, spec, tenant)
    }

    fn open_under(
        identity: ClusterIdentity,
        root: &Path,
        spec: &[MemberSpec],
        tenant: &str,
    ) -> Result<Self, ClusterError> {
        if spec.is_empty() {
            return Err(ClusterError::InvalidShape);
        }
        let mut voters: BTreeMap<RegionId, Vec<MemberId>> = BTreeMap::new();
        let mut ids = Vec::new();
        let mut members = Vec::new();
        for member_spec in spec {
            let member_id =
                MemberId::try_new(&member_spec.name).map_err(|_| ClusterError::InvalidShape)?;
            let region =
                RegionId::try_new(&member_spec.region).map_err(|_| ClusterError::InvalidShape)?;
            let location = root.join("members").join(&member_spec.name);
            // Each member dir carries the same pinned identity record:
            // every start verifies it locally.
            identity.pin(&location).map_err(ClusterError::Identity)?;
            let member = ClusterMember::open(&location, &member_spec.name, tenant)
                .map_err(ClusterError::Member)?;
            voters.entry(region).or_default().push(member_id.clone());
            ids.push(member_id);
            members.push(member);
        }
        Ok(Self {
            identity,
            set: ReplicaSet::new(1, voters),
            root: root.to_path_buf(),
            tenant: tenant.to_owned(),
            ids,
            members,
            backup: None,
        })
    }

    /// The verified identity.
    #[must_use]
    pub fn identity(&self) -> &ClusterIdentity {
        &self.identity
    }

    /// The frozen mode.
    #[must_use]
    pub fn mode(&self) -> ClusterMode {
        self.identity.mode()
    }

    /// The current replica set.
    #[must_use]
    pub fn set(&self) -> &ReplicaSet {
        &self.set
    }

    /// The tenant this cluster serves — every member binds it at open.
    #[must_use]
    pub fn tenant(&self) -> &str {
        &self.tenant
    }

    /// The durable root.
    #[must_use]
    pub fn root(&self) -> &Path {
        &self.root
    }

    /// The member names, in this host's canonical order.
    #[must_use]
    pub fn member_names(&self) -> &[MemberId] {
        &self.ids
    }

    /// One member's replication position.
    ///
    /// # Errors
    ///
    /// Returns [`ClusterError::UnknownMember`] for a name this cluster
    /// does not hold.
    pub fn position_of(&self, member: &str) -> Result<u64, ClusterError> {
        let id = MemberId::try_new(member).map_err(|_| ClusterError::UnknownMember)?;
        let index = self.ids.iter().position(|known| *known == id);
        index.map_or(Err(ClusterError::UnknownMember), |at| Ok(self.members[at].position()))
    }

    /// Attaches the backup target set: the non-voting members this
    /// cluster fans its committed writes to for the `backup_ack`
    /// level. One active cluster, one backup set — the active-to-backup
    /// replication setup of the one-step creation.
    pub fn attach_backup(&mut self, set: BackupSet) {
        self.backup = Some(set);
    }

    /// The attached backup set, when one is.
    #[must_use]
    pub fn backup(&self) -> Option<&BackupSet> {
        self.backup.as_ref()
    }

    /// The attached backup set, mutably, when one is.
    pub fn backup_mut(&mut self) -> Option<&mut BackupSet> {
        self.backup.as_mut()
    }

    /// The ordered `backup_ack` level (frozen): the regional quorum
    /// commits FIRST, then the write fans to the backup set and awaits
    /// 2-of-3 durability. The outcome names the truth — FULL at the
    /// minimum, PARTIAL below it — and the active write is never
    /// rolled back either way. Requesting the level without an
    /// attached backup set refuses BEFORE anything commits.
    ///
    /// # Errors
    ///
    /// As [`Self::client_write`], plus
    /// [`ClusterWriteRefusal::NoBackupSet`] when no backup set is
    /// attached.
    pub fn client_write_backup(
        &mut self,
        accepting: &str,
        write: &ComposedWrite,
    ) -> Result<(compose::ComposedReceipt, BackupOutcome), ClusterWriteRefusal> {
        if self.backup.is_none() {
            return Err(ClusterWriteRefusal::NoBackupSet);
        }
        // The level is ordered: the requested regional quorum first...
        let receipt = self.client_write(accepting, write, LeaderlessLevel::RegionalQuorum)?;
        // ...then the backup durability, whatever it turns out to be.
        let outcome = self.fan_to_backup(write, BackupPolicy::default());
        Ok((receipt, outcome.expect("the backup set is attached")))
    }

    /// Fans one already-committed write to the attached backup set —
    /// `None` names "no backup set attached, no backup durability
    /// claimed". The active commit stands whatever the outcome.
    pub fn fan_to_backup(
        &mut self,
        write: &ComposedWrite,
        policy: BackupPolicy,
    ) -> Option<BackupOutcome> {
        self.backup.as_mut().map(|set| set.await_ack(write, policy))
    }

    /// Accepts one client write at any member — the leaderless
    /// coordinator fans it to the voters and answers the achieved
    /// level. A backup cluster refuses permanently at the door with the
    /// LOWEST member position attached (the furthest-behind backup is
    /// what a last-resort reader would see).
    ///
    /// # Errors
    ///
    /// Returns the mode refusal, the unknown-member refusal, or the
    /// coordinator's typed failures.
    pub fn client_write(
        &mut self,
        accepting: &str,
        write: &ComposedWrite,
        level: LeaderlessLevel,
    ) -> Result<compose::ComposedReceipt, ClusterWriteRefusal> {
        if self.mode() == ClusterMode::Backup {
            let position = self.members.iter().map(ClusterMember::position).min().unwrap_or(0);
            return Err(ClusterWriteRefusal::ClusterReadOnly(MutationRefusal { position }));
        }
        let accepting =
            MemberId::try_new(accepting).map_err(|_| ClusterWriteRefusal::UnknownMember)?;
        if !self.ids.contains(&accepting) {
            return Err(ClusterWriteRefusal::UnknownMember);
        }
        // One contiguous lend of the composed plane: the coordinator
        // fans the write to the voters itself.
        let mut composed = self.take_composed();
        let outcome = coordinate_composed_write(&self.set, &accepting, &mut composed, write, level);
        self.return_composed(composed);
        outcome.map_err(ClusterWriteRefusal::from_coordination)
    }

    /// Reads one document at any member, repairing behind the regional
    /// quorum on the way. Both modes read.
    ///
    /// # Errors
    ///
    /// Returns the unknown-member refusal or the read's typed failures.
    pub fn client_read(
        &mut self,
        reading: &str,
        document: &str,
    ) -> Result<
        (Option<BTreeMap<String, DocumentValue>>, compose::ReadRepairReport),
        ClusterWriteRefusal,
    > {
        let reading = MemberId::try_new(reading).map_err(|_| ClusterWriteRefusal::UnknownMember)?;
        if !self.ids.contains(&reading) {
            return Err(ClusterWriteRefusal::UnknownMember);
        }
        let mut composed = self.take_composed();
        let outcome = quorum_read_with_repair(&self.set, &reading, &mut composed, document);
        self.return_composed(composed);
        outcome.map_err(ClusterWriteRefusal::from_compose)
    }

    /// Takes the composed plane out for one coordinator call: each
    /// member's composed state moves into the working vector, leaving
    /// an empty slot behind.
    fn take_composed(&mut self) -> Vec<terrabase_replication_core::compose::ComposedMember> {
        let mut taken = Vec::with_capacity(self.members.len());
        for slot in &mut self.members {
            taken.push(slot.take_composed());
        }
        taken
    }

    /// Returns the composed plane after one coordinator call.
    fn return_composed(
        &mut self,
        composed: Vec<terrabase_replication_core::compose::ComposedMember>,
    ) {
        for (slot, member) in self.members.iter_mut().zip(composed) {
            slot.return_composed(member);
        }
    }

    /// Expands the cluster in one step: the new member's durable state
    /// is created under the identity, the membership steps to the next
    /// epoch through the overlapping-quorum coordinator, and the new
    /// member catches up through anti-entropy with an existing voter —
    /// frontier exchange, deltas, reconcile — so it serves the
    /// converged state the moment it joins.
    ///
    /// # Errors
    ///
    /// Returns [`ClusterError::InvalidShape`] for a duplicate name or
    /// an invalid id, [`ClusterError::Member`] when the new member's
    /// state cannot open, and the epoch coordinator's typed failures
    /// through [`ClusterWriteRefusal`]-free [`ClusterError`].
    pub fn expand(&mut self, spec: MemberSpec) -> Result<compose::EpochReceipt, ClusterError> {
        let member_id = MemberId::try_new(&spec.name).map_err(|_| ClusterError::InvalidShape)?;
        let region = RegionId::try_new(&spec.region).map_err(|_| ClusterError::InvalidShape)?;
        if self.ids.contains(&member_id) {
            return Err(ClusterError::InvalidShape);
        }
        // The new member's durable state, under the same pinned
        // identity every member verifies at start.
        let location = self.root.join("members").join(&spec.name);
        self.identity.pin(&location).map_err(ClusterError::Identity)?;
        let member = ClusterMember::create(
            &location,
            self.identity.cluster(),
            self.identity.mode(),
            self.identity.creation_record(),
            &spec.name,
            &self.tenant,
        )
        .map_err(ClusterError::Member)?;

        // The next epoch: the same Regions, plus the new member voting
        // in its own — the overlapping-quorum precondition is the
        // coordinator's to enforce.
        let mut voters: BTreeMap<RegionId, Vec<MemberId>> = BTreeMap::new();
        for known in self.set.regions() {
            voters.insert(known.clone(), self.set.voters_of(&known).to_vec());
        }
        voters.entry(region).or_default().push(member_id.clone());

        // The step runs BEFORE the member joins the host's vectors, so
        // the old shape coordinates its own expansion.
        let next = ReplicaSet::new(self.set.epoch() + 1, voters);
        let mut composed = self.take_composed();
        let outcome = compose::accept_epoch(&self.set, &next, &mut composed);
        self.return_composed(composed);
        let receipt = outcome.map_err(|_| ClusterError::EpochRefused)?;

        // Now the member joins, and catches up: frontier exchange with
        // an existing voter, deltas, reconcile — the rejoin path of the
        // contract §5, run eagerly so the member serves converged
        // state immediately.
        self.ids.push(member_id);
        self.members.push(member);
        self.set = next;
        let mut composed = self.take_composed();
        let newcomer_at = composed.len() - 1;
        let (donor, newcomer) = composed.split_at_mut(newcomer_at);
        let _ = donor[0].anti_entropy_with(&mut newcomer[0]);
        self.return_composed(composed);
        Ok(receipt)
    }

    /// Steps the membership to a new epoch through the P2 epoch
    /// coordinator: the overlapping-quorum precondition holds or the
    /// step refuses typed, and every member records the epoch durably.
    ///
    /// # Errors
    ///
    /// Returns the epoch coordinator's typed failures.
    pub fn step_epoch(
        &mut self,
        proposed: ReplicaSet,
    ) -> Result<compose::EpochReceipt, ClusterWriteRefusal> {
        let mut composed = self.take_composed();
        let outcome = compose::accept_epoch(&self.set, &proposed, &mut composed);
        self.return_composed(composed);
        let receipt = outcome.map_err(ClusterWriteRefusal::from_epoch)?;
        self.set = proposed;
        Ok(receipt)
    }
}

/// The typed refusal of a cluster-host client operation.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ClusterWriteRefusal {
    /// The named member is not part of this cluster.
    UnknownMember,
    /// A backup cluster's permanent write refusal, position attached.
    ClusterReadOnly(MutationRefusal),
    /// The quorum was not reached.
    QuorumNotReached,
    /// A member's apply failed.
    ApplyFailed,
    /// The write was refused at admission.
    AdmissionRefused,
    /// The read was refused (quorum below the regional majority).
    ReadQuorumNotReached,
    /// The epoch step refused (no overlap, or not a step).
    EpochRefused,
    /// The backup_ack level was requested without an attached backup
    /// set — refused before anything committed.
    NoBackupSet,
}

impl ClusterWriteRefusal {
    fn from_coordination(error: compose::ComposedCoordinationError) -> Self {
        match error {
            compose::ComposedCoordinationError::NotEligible { .. }
            | compose::ComposedCoordinationError::ApplyFailed { .. } => Self::ApplyFailed,
            compose::ComposedCoordinationError::QuorumNotReached { .. } => Self::QuorumNotReached,
        }
    }

    fn from_compose(_error: compose::ComposeError) -> Self {
        Self::ReadQuorumNotReached
    }

    fn from_epoch(_error: compose::EpochError) -> Self {
        Self::EpochRefused
    }
}

#[cfg(test)]
mod tests {
    use std::collections::BTreeMap;

    use terrabase_replication_core::compose::ComposedWrite;
    use terrabase_v2::replication::{LeaderlessLevel, MemberId, RegionId, ReplicaSet};
    use terrabase_v2::types::DocumentValue;

    use super::{ClusterHost, ClusterMode, ClusterWriteRefusal, MemberSpec};

    fn root(tag: &str) -> std::path::PathBuf {
        let dir = std::env::temp_dir().join(format!(
            "tbv2-cluster-host-{tag}-{}-{}",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .map_or(0, |elapsed| elapsed.as_nanos())
        ));
        let _ = std::fs::remove_dir_all(&dir);
        std::fs::create_dir_all(&dir).unwrap();
        dir
    }

    fn spec() -> Vec<MemberSpec> {
        vec![
            MemberSpec { name: "member-a".into(), region: "eu-1".into() },
            MemberSpec { name: "member-b".into(), region: "eu-1".into() },
            MemberSpec { name: "member-c".into(), region: "eu-1".into() },
            MemberSpec { name: "member-d".into(), region: "us-1".into() },
            MemberSpec { name: "member-e".into(), region: "us-1".into() },
        ]
    }

    fn write(operation: &str, document: &str) -> ComposedWrite {
        ComposedWrite::Put {
            operation: operation.to_owned(),
            document: document.to_owned(),
            body: BTreeMap::from([("status".to_owned(), DocumentValue::String("open".to_owned()))]),
        }
    }

    #[test]
    fn one_step_creation_serves_quorum_writes_and_reads_from_any_member() {
        let dir = root("create");
        let mut cluster = ClusterHost::create(
            &dir,
            "cell-a",
            ClusterMode::Active,
            "created",
            &spec(),
            "tenant-a",
        )
        .expect("the cluster creates in one step");
        assert_eq!(cluster.mode(), ClusterMode::Active);
        assert_eq!(cluster.set().epoch(), 1);

        // A write accepted at one member answers the regional quorum.
        let receipt = cluster
            .client_write("member-a", &write("op-1", "doc-1"), LeaderlessLevel::RegionalQuorum)
            .expect("the coordinated write commits");
        assert_eq!(receipt.epoch(), 1);
        assert!(receipt.contributors().len() >= 3, "the eu-1 majority contributed");

        // Every member serves the document — the converged state.
        for member in ["member-a", "member-c", "member-e"] {
            let (value, _) = cluster.client_read(member, "doc-1").expect("the read answers");
            assert!(value.is_some(), "{member} serves the converged document");
        }
        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn a_backup_cluster_refuses_writes_with_the_lowest_position_but_reads() {
        let dir = root("backup");
        let mut cluster = ClusterHost::create(
            &dir,
            "cell-backup",
            ClusterMode::Backup,
            "created",
            &spec(),
            "tenant-a",
        )
        .expect("creates as backup");
        let refusal = cluster
            .client_write("member-a", &write("op-1", "doc-1"), LeaderlessLevel::RegionalQuorum)
            .expect_err("a backup cluster refuses client writes");
        let ClusterWriteRefusal::ClusterReadOnly(refusal) = refusal else {
            panic!("the refusal is the typed read-only, got {refusal:?}");
        };
        let lowest = cluster
            .member_names()
            .iter()
            .map(|id| cluster.position_of(id.as_str()).unwrap())
            .min()
            .unwrap_or(0);
        assert_eq!(refusal.position, lowest, "the furthest-behind position is attached");

        // Reads still answer on the backup: the last resort of the
        // routing order.
        let (value, _) = cluster.client_read("member-a", "does-not-exist").expect("reads");
        assert!(value.is_none());
        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn reopening_verifies_the_identity_and_serves_the_converged_state() {
        let dir = root("reopen");
        {
            let mut cluster = ClusterHost::create(
                &dir,
                "cell-a",
                ClusterMode::Active,
                "created",
                &spec(),
                "tenant-a",
            )
            .expect("creates");
            cluster
                .client_write("member-b", &write("op-1", "doc-1"), LeaderlessLevel::RegionalQuorum)
                .expect("commits");
        }
        // A fresh host over the same root verifies the identity and
        // serves what the cluster holds.
        let mut reopened = ClusterHost::open(&dir, &spec(), "tenant-a").expect("reopens");
        let (value, _) = reopened.client_read("member-d", "doc-1").expect("reads");
        assert!(value.is_some(), "the converged state survives the reopen");
        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn backup_ack_commits_regionally_then_answers_the_backup_outcome() {
        let dir = root("backup-ack");
        let mut cluster = ClusterHost::create(
            &dir,
            "cell-a",
            ClusterMode::Active,
            "created",
            &spec(),
            "tenant-a",
        )
        .expect("creates");

        // The level without a backup set refuses before committing.
        let refusal = cluster
            .client_write_backup("member-a", &write("op-0", "doc-0"))
            .expect_err("no backup set, no backup_ack");
        assert_eq!(refusal, super::ClusterWriteRefusal::NoBackupSet);
        assert!(
            cluster.client_read("member-a", "doc-0").expect("reads").0.is_none(),
            "nothing committed behind the refusal"
        );

        // The backup set: three bulk-HDD members under their own
        // backup identity, beside the active cluster.
        let backup = crate::backup::BackupSet::create(
            &dir.join("backup"),
            "cell-a-backup",
            "created",
            &[
                MemberSpec { name: "backup-1".into(), region: "bulk-hdd".into() },
                MemberSpec { name: "backup-2".into(), region: "bulk-hdd".into() },
                MemberSpec { name: "backup-3".into(), region: "bulk-hdd".into() },
            ],
            "tenant-a",
        )
        .expect("the backup set creates");
        cluster.attach_backup(backup);
        assert_eq!(cluster.backup().map_or(0, crate::backup::BackupSet::len), 3);

        // The ordered level: regional quorum first, then 2-of-3.
        let (receipt, outcome) = cluster
            .client_write_backup("member-b", &write("op-1", "doc-1"))
            .expect("the ordered write commits");
        assert_eq!(receipt.achieved(), LeaderlessLevel::RegionalQuorum);
        assert!(outcome.is_full(), "all three backups hold it: {outcome:?}");
        assert_eq!(outcome.acks(), 3);

        // The backup members converge: they read what replicated.
        let converged =
            cluster.backup_mut().expect("attached").read("doc-1").expect("the backup reads");
        assert!(converged.contains_key("status"), "the replicated body is visible");
        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn one_step_expansion_catches_up_and_survives_the_restart() {
        let dir = root("expand");
        let mut cluster = ClusterHost::create(
            &dir,
            "cell-a",
            ClusterMode::Active,
            "created",
            &spec(),
            "tenant-a",
        )
        .expect("creates");
        cluster
            .client_write("member-a", &write("op-1", "doc-1"), LeaderlessLevel::RegionalQuorum)
            .expect("commits before the expansion");

        // The one-step expansion: durable state, epoch step, catch-up.
        let receipt = cluster
            .expand(MemberSpec { name: "member-f".into(), region: "us-1".into() })
            .expect("the expansion accepts");
        assert_eq!(receipt.to(), 2, "the membership stepped an epoch");
        assert_eq!(cluster.set().epoch(), 2);
        assert_eq!(cluster.member_names().len(), 6);
        assert!(
            cluster.set().is_voter(&MemberId::try_new("member-f").expect("valid")),
            "the newcomer votes immediately"
        );

        // The catch-up ran eagerly: the newcomer serves the document
        // the cluster held BEFORE it joined.
        let (value, _) = cluster.client_read("member-f", "doc-1").expect("the newcomer reads");
        assert!(value.is_some(), "the newcomer serves the pre-join state");

        // Writes commit across the expanded shape, and a duplicate
        // expansion refuses typed.
        cluster
            .client_write("member-f", &write("op-2", "doc-2"), LeaderlessLevel::RegionalQuorum)
            .expect("the expanded cluster serves writes");
        assert_eq!(
            cluster.expand(MemberSpec { name: "member-f".into(), region: "us-1".into() }).err(),
            Some(super::ClusterError::InvalidShape),
            "a duplicate name refuses"
        );

        // The restart: a fresh host over the same root verifies every
        // member's identity and serves the expanded, converged state.
        drop(cluster);
        let mut reopened = ClusterHost::open(
            &dir,
            &[
                MemberSpec { name: "member-a".into(), region: "eu-1".into() },
                MemberSpec { name: "member-b".into(), region: "eu-1".into() },
                MemberSpec { name: "member-c".into(), region: "eu-1".into() },
                MemberSpec { name: "member-d".into(), region: "us-1".into() },
                MemberSpec { name: "member-e".into(), region: "us-1".into() },
                MemberSpec { name: "member-f".into(), region: "us-1".into() },
            ],
            "tenant-a",
        )
        .expect("reopens over the expanded shape");
        for document in ["doc-1", "doc-2"] {
            let (value, _) = reopened.client_read("member-f", document).expect("reads");
            assert!(value.is_some(), "{document} survives the restart");
        }
        reopened
            .client_write("member-a", &write("op-3", "doc-3"), LeaderlessLevel::RegionalQuorum)
            .expect("writes serve after the restart");
        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn membership_steps_through_overlapping_epochs() {
        let dir = root("epoch");
        let mut cluster = ClusterHost::create(
            &dir,
            "cell-a",
            ClusterMode::Active,
            "created",
            &spec(),
            "tenant-a",
        )
        .expect("creates");

        // The next epoch drops member-e from us-1: the old and new
        // regional quorums still overlap in both regions.
        let mut voters: BTreeMap<RegionId, Vec<MemberId>> = BTreeMap::new();
        voters.insert(
            RegionId::try_new("eu-1").expect("valid"),
            vec![
                MemberId::try_new("member-a").expect("valid"),
                MemberId::try_new("member-b").expect("valid"),
                MemberId::try_new("member-c").expect("valid"),
            ],
        );
        voters.insert(
            RegionId::try_new("us-1").expect("valid"),
            vec![MemberId::try_new("member-d").expect("valid")],
        );
        let next = ReplicaSet::new(2, voters);
        let receipt = cluster.step_epoch(next).expect("the overlapping step accepts");
        assert_eq!(receipt.to(), 2);
        assert_eq!(receipt.from(), 1);
        assert_eq!(cluster.set().epoch(), 2);

        // Writes still commit under the new epoch.
        cluster
            .client_write("member-d", &write("op-2", "doc-2"), LeaderlessLevel::RegionalQuorum)
            .expect("the new epoch serves writes");
        let _ = std::fs::remove_dir_all(&dir);
    }
}
