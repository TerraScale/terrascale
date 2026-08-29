//! One cluster member under an immutable identity (P5).
//!
//! A [`ClusterMember`] is one durable [`ComposedMember`] (engine plus
//! same-peer CRDT document) pinned to a cluster identity: an ACTIVE
//! member serves client writes and reads through the leaderless
//! coordinators, and a BACKUP member receives replication while
//! refusing every client mutation permanently — the typed
//! [`MutationRefusal::ClusterReadOnly`] carrying its replication
//! position, the frozen backup-cluster contract §3 word for word. A
//! backup member never mints a writer identity and never votes.

use std::path::{Path, PathBuf};

use terrabase_replication_core::compose::ComposedMember;

use crate::identity::{ClusterIdentity, ClusterMode, IdentityError};

/// One cluster member: the identity it verifies at every start, the
/// durable composed state it serves or replicates, and the mode
/// enforcement the identity freezes.
pub struct ClusterMember {
    identity: ClusterIdentity,
    location: PathBuf,
    member: Option<ComposedMember>,
}

/// The typed refusal a client mutation meets on a backup member —
/// permanent, with the replication position attached so tooling can
/// report how far the backup has converged.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct MutationRefusal {
    /// The member's replication position at the refusal.
    pub position: u64,
}

impl ClusterMember {
    /// Opens one member at `location`, verifying the pinned identity
    /// first — a member never starts under an identity it cannot
    /// verify.
    ///
    /// # Errors
    ///
    /// Returns the identity's typed failure when no identity is pinned
    /// or the pinned record does not verify, and the composed member's
    /// failure when its durable state cannot open.
    pub fn open(
        location: &Path,
        member_name: &str,
        tenant_name: &str,
    ) -> Result<Self, MemberError> {
        let identity = ClusterIdentity::open(location).map_err(MemberError::Identity)?;
        // The durable state lives beside the identity record, in its own
        // directory: a fresh LSM location refuses any pre-existing file,
        // and the identity file must sit at the location's root.
        let state = location.join("state");
        let member = ComposedMember::open(member_name, &state, tenant_name)
            .map_err(|_| MemberError::StateUnavailable)?;
        Ok(Self { identity, location: location.to_path_buf(), member: Some(member) })
    }

    /// Creates a cluster's FIRST member: the identity is created with
    /// its frozen mode, pinned at the location, and the member opens
    /// under it — the one-step creation entry point.
    ///
    /// # Errors
    ///
    /// Returns the identity's creation failure or the state open
    /// failure.
    pub fn create(
        location: &Path,
        cluster: &str,
        mode: ClusterMode,
        creation_record: &str,
        member_name: &str,
        tenant_name: &str,
    ) -> Result<Self, MemberError> {
        let identity = ClusterIdentity::create(cluster, mode, creation_record)
            .map_err(MemberError::Identity)?;
        identity.pin(location).map_err(MemberError::Identity)?;
        let state = location.join("state");
        let member = ComposedMember::open(member_name, &state, tenant_name)
            .map_err(|_| MemberError::StateUnavailable)?;
        Ok(Self { identity, location: location.to_path_buf(), member: Some(member) })
    }

    /// The verified identity.
    #[must_use]
    pub fn identity(&self) -> &ClusterIdentity {
        &self.identity
    }

    /// The member's mode — the identity's, frozen at creation.
    #[must_use]
    pub fn mode(&self) -> ClusterMode {
        self.identity.mode()
    }

    /// The durable location.
    #[must_use]
    pub fn location(&self) -> &Path {
        &self.location
    }

    /// The member's replication position: its engine's storage
    /// sequence (zero while the composed state is lent to a
    /// coordinator — positions are read outside coordinator calls).
    #[must_use]
    pub fn position(&self) -> u64 {
        self.member.as_ref().map_or(0, ComposedMember::position)
    }

    /// Accepts one client write through the composed state — or refuses
    /// it permanently on a backup member with the position attached.
    ///
    /// # Errors
    ///
    /// Returns [`MemberError::MutationRefused`] on a backup member and
    /// the composed member's failure for anything else.
    pub fn accept_client_write(
        &mut self,
        operation: &str,
        document: &str,
        body: &std::collections::BTreeMap<String, terrabase_v2::types::DocumentValue>,
    ) -> Result<terrabase_v2::CommitReceipt, MemberError> {
        if self.mode() == ClusterMode::Backup {
            // The permanent refusal: the backup never serves a client
            // mutation, whatever the operation, forever.
            return Err(MemberError::MutationRefused(MutationRefusal {
                position: self.position(),
            }));
        }
        self.composed_mut()
            .accept_put(operation, document, body)
            .map_err(|_| MemberError::StateRefused)
    }

    /// Accepts one replicated write — the ONLY write path a backup
    /// member serves (its replication intake, never a client's).
    ///
    /// # Errors
    ///
    /// Returns the composed member's failure.
    pub fn accept_replicated_write(
        &mut self,
        operation: &str,
        document: &str,
        body: &std::collections::BTreeMap<String, terrabase_v2::types::DocumentValue>,
    ) -> Result<terrabase_v2::CommitReceipt, MemberError> {
        self.composed_mut()
            .accept_put(operation, document, body)
            .map_err(|_| MemberError::StateRefused)
    }

    /// Accepts one replicated delete — the backup member's tombstone
    /// intake, the delete half of its replication path.
    ///
    /// # Errors
    ///
    /// Returns the composed member's failure.
    pub fn accept_replicated_delete(
        &mut self,
        operation: &str,
        document: &str,
    ) -> Result<terrabase_v2::CommitReceipt, MemberError> {
        self.composed_mut()
            .accept_delete(operation, document)
            .map_err(|_| MemberError::StateRefused)
    }

    /// Lends the underlying composed member — the coordinators take
    /// `&mut [ComposedMember]`, and the cluster host drives them.
    ///
    /// # Panics
    ///
    /// Panics only if the composed state is already lent out — the
    /// cluster host's take/return discipline prevents that.
    pub fn composed_mut(&mut self) -> &mut ComposedMember {
        self.member.as_mut().expect("the composed state is present")
    }

    /// Takes the composed state out for one coordinator call.
    ///
    /// # Panics
    ///
    /// Panics only on a double take — the cluster host's discipline.
    pub(crate) fn take_composed(&mut self) -> ComposedMember {
        self.member.take().expect("the composed state is present")
    }

    /// Returns the composed state after one coordinator call.
    pub(crate) fn return_composed(&mut self, member: ComposedMember) {
        self.member = Some(member);
    }

    /// Enumerates the member's visible documents with their merged
    /// bodies — the restore observation surface.
    ///
    /// # Errors
    ///
    /// Never fails today: the native CRDT enumeration is total. The
    /// result type keeps the seam stable for the durable enumeration.
    pub fn documents(
        &mut self,
    ) -> Result<
        std::collections::BTreeMap<
            String,
            std::collections::BTreeMap<String, terrabase_v2::types::DocumentValue>,
        >,
        MemberError,
    > {
        Ok(self.composed_mut().documents())
    }

    /// Reads one document through the composed state. Both modes read:
    /// the backup serves last-resort reads per the contract's routing
    /// order.
    ///
    /// # Errors
    ///
    /// Returns the composed member's failure.
    pub fn read(
        &mut self,
        document: &str,
    ) -> Result<
        Option<std::collections::BTreeMap<String, terrabase_v2::types::DocumentValue>>,
        MemberError,
    > {
        self.composed_mut().visible(document).map_err(|_| MemberError::StateRefused)
    }
}

/// The member's typed failures.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum MemberError {
    /// The identity failed to create, open, or verify.
    Identity(IdentityError),
    /// The composed durable state could not open.
    StateUnavailable,
    /// The composed state refused the operation.
    StateRefused,
    /// A client mutation on a backup member: permanent, position
    /// attached.
    MutationRefused(MutationRefusal),
}

#[cfg(test)]
mod tests {
    use std::collections::BTreeMap;

    use terrabase_v2::types::DocumentValue;

    use super::{ClusterMember, ClusterMode, MemberError};
    use crate::identity::IdentityError;

    fn location(tag: &str) -> std::path::PathBuf {
        let dir = std::env::temp_dir().join(format!(
            "tbv2-cluster-member-{tag}-{}-{}",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .map_or(0, |elapsed| elapsed.as_nanos())
        ));
        let _ = std::fs::remove_dir_all(&dir);
        std::fs::create_dir_all(&dir).unwrap();
        dir
    }

    fn body() -> BTreeMap<String, DocumentValue> {
        BTreeMap::from([("status".to_owned(), DocumentValue::String("open".to_owned()))])
    }

    #[test]
    fn one_step_creation_pins_the_identity_and_serves_as_active() {
        let dir = location("create");
        let mut member = ClusterMember::create(
            &dir,
            "cell-a",
            ClusterMode::Active,
            "created",
            "member-1",
            "tenant-a",
        )
        .expect("the cluster's first member creates in one step");
        assert_eq!(member.mode(), ClusterMode::Active);

        let receipt = member
            .accept_client_write("op-1", "doc-1", &body())
            .expect("an active member serves client writes");
        assert!(receipt.sequence().get() > 0);
        assert!(member.read("doc-1").expect("reads").is_some());
        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn a_backup_member_refuses_every_client_mutation_permanently_with_its_position() {
        let dir = location("backup");
        let mut member = ClusterMember::create(
            &dir,
            "cell-backup",
            ClusterMode::Backup,
            "created",
            "member-1",
            "tenant-a",
        )
        .expect("creates as backup");

        let first = member
            .accept_client_write("op-1", "doc-1", &body())
            .expect_err("the first client mutation refuses");
        let MemberError::MutationRefused(refusal) = first else {
            panic!("the refusal is typed, got {first:?}");
        };
        assert_eq!(refusal.position, member.position(), "the position is attached");

        // The second, and the hundredth: permanent, identically typed.
        for attempt in 2..=5 {
            let again = member
                .accept_client_write(&format!("op-{attempt}"), "doc-1", &body())
                .expect_err("every attempt refuses");
            assert!(matches!(again, MemberError::MutationRefused(_)));
        }
        assert!(member.read("doc-1").expect("reads").is_none(), "nothing was written");

        // The replication intake is the one path that writes.
        member
            .accept_replicated_write("op-replicated", "doc-1", &body())
            .expect("replication writes");
        assert!(member.read("doc-1").expect("reads").is_some());
        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn a_member_never_opens_under_an_identity_it_cannot_verify() {
        let dir = location("unverified");
        let identity_dir = dir.join("identity");
        std::fs::create_dir_all(&identity_dir).unwrap();
        ClusterMember::create(
            &identity_dir,
            "cell-a",
            ClusterMode::Active,
            "created",
            "member-1",
            "tenant-a",
        )
        .expect("creates");
        // Tamper: the member start must refuse, not serve.
        let record = identity_dir.join("cluster-identity");
        let text = std::fs::read_to_string(&record).unwrap();
        std::fs::write(&record, text.replace("mode: active", "mode: backup")).unwrap();
        assert!(matches!(
            ClusterMember::open(&identity_dir, "member-1", "tenant-a").err(),
            Some(MemberError::Identity(IdentityError::Tampered))
        ));
        let _ = std::fs::remove_dir_all(&dir);
    }
}
