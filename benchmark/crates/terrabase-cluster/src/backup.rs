//! The backup membership shape (P5): the non-voting, bulk-HDD
//! replication targets a backup cluster is made of.
//!
//! The frozen backup-cluster contract: a backup member never mints a
//! writer identity, never originates a CRDT operation, and never votes
//! — it receives replication. `BackupAck` is an ADDITIONAL optional
//! acknowledgement level: the requested level (regional or global)
//! succeeds first, then the write is durable at 2 of the backup's 3
//! members; a timeout yields the partial outcome and NEVER rolls back
//! what the active cluster already committed. Idempotent retry by
//! operation identity: the same operation re-fanned replays the
//! recorded receipts.

use std::path::Path;

use terrabase_replication_core::compose::ComposedWrite;
use terrabase_v2::types::DocumentValue;

use crate::host::MemberSpec;
use crate::identity::{ClusterIdentity, ClusterMode};
use crate::member::ClusterMember;

/// The backup acknowledgement policy: how many of the backup's members
/// must durably hold the write for the FULL outcome.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct BackupPolicy {
    /// The minimum durable backup acks (the contract's 2 of 3).
    pub min_acks: usize,
}

impl Default for BackupPolicy {
    fn default() -> Self {
        Self { min_acks: 2 }
    }
}

/// The backup acknowledgement outcome of one write.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum BackupOutcome {
    /// At least the policy's minimum of backup members durably hold
    /// the write.
    Full {
        /// How many backup members acknowledged.
        acks: usize,
    },
    /// Fewer than the minimum acknowledged: the active write stands
    /// (it is never rolled back), and the receipt names the partial
    /// state honestly.
    Partial {
        /// How many backup members acknowledged.
        acks: usize,
    },
}

impl BackupOutcome {
    /// How many backup members acknowledged.
    #[must_use]
    pub const fn acks(self) -> usize {
        match self {
            Self::Full { acks } | Self::Partial { acks } => acks,
        }
    }

    /// Whether the full outcome was reached.
    #[must_use]
    pub const fn is_full(self) -> bool {
        matches!(self, Self::Full { .. })
    }
}

/// The backup target set: three backup-mode members under their own
/// immutable backup identity, receiving replication.
pub struct BackupSet {
    identity: ClusterIdentity,
    members: Vec<ClusterMember>,
}

impl BackupSet {
    /// Creates a backup target set at `root`: the backup identity
    /// (mode frozen to backup — terminal) and one backup member per
    /// spec.
    ///
    /// # Errors
    ///
    /// Returns the identity's or the members' typed failures.
    pub fn create(
        root: &Path,
        cluster: &str,
        creation_record: &str,
        spec: &[MemberSpec],
        tenant: &str,
    ) -> Result<Self, crate::member::MemberError> {
        let identity = ClusterIdentity::create(cluster, ClusterMode::Backup, creation_record)
            .map_err(crate::member::MemberError::Identity)?;
        identity.pin(root).map_err(crate::member::MemberError::Identity)?;
        let mut members = Vec::new();
        for member_spec in spec {
            let location = root.join("members").join(&member_spec.name);
            identity.pin(&location).map_err(crate::member::MemberError::Identity)?;
            members.push(ClusterMember::create(
                &location,
                cluster,
                ClusterMode::Backup,
                creation_record,
                &member_spec.name,
                tenant,
            )?);
        }
        Ok(Self { identity, members })
    }

    /// The backup identity — mode backup, terminal.
    #[must_use]
    pub fn identity(&self) -> &ClusterIdentity {
        &self.identity
    }

    /// The set's replication position: the FURTHEST-BEHIND member's —
    /// what a last-resort reader would see, named honestly.
    #[must_use]
    pub fn position(&mut self) -> u64 {
        self.members.iter().map(ClusterMember::position).min().unwrap_or(0)
    }

    /// Reads one document through the first backup member — the
    /// backup's last-resort read of the frozen routing order.
    #[must_use]
    pub fn read(
        &mut self,
        document: &str,
    ) -> Option<std::collections::BTreeMap<String, DocumentValue>> {
        self.members.first_mut().and_then(|member| member.read(document).ok().flatten())
    }

    /// How many backup members the set holds.
    #[must_use]
    pub fn len(&self) -> usize {
        self.members.len()
    }

    /// Whether the set holds no members.
    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.members.is_empty()
    }

    /// Fans one durably-committed active write to every backup member
    /// and awaits the policy's minimum: the FULL outcome when enough
    /// acks land, the PARTIAL outcome otherwise — the active write is
    /// never rolled back either way. The operation identity is the
    /// retry identity: re-fanning the same operation replays each
    /// member's recorded receipt. A delete fans as its tombstone.
    ///
    /// # Errors
    ///
    /// Never: a backup member's failure counts as a missing ack, not
    /// an error — the outcome is the honesty, not a refusal.
    pub fn await_ack(
        &mut self,
        write: &terrabase_replication_core::compose::ComposedWrite,
        policy: BackupPolicy,
    ) -> BackupOutcome {
        let mut acks = 0;
        for member in &mut self.members {
            let landed = match write {
                ComposedWrite::Put { operation, document, body } => {
                    member.accept_replicated_write(operation, document, body).is_ok()
                }
                ComposedWrite::Delete { operation, document } => {
                    member.accept_replicated_delete(operation, document).is_ok()
                }
            };
            if landed {
                acks += 1;
            }
        }
        if acks >= policy.min_acks {
            BackupOutcome::Full { acks }
        } else {
            BackupOutcome::Partial { acks }
        }
    }
}

#[cfg(test)]
mod tests {
    use std::collections::BTreeMap;

    use terrabase_replication_core::compose::ComposedWrite;
    use terrabase_v2::types::DocumentValue;

    use super::{BackupOutcome, BackupPolicy, BackupSet};
    use crate::host::MemberSpec;

    fn root(tag: &str) -> std::path::PathBuf {
        let dir = std::env::temp_dir().join(format!(
            "tbv2-cluster-backup-{tag}-{}-{}",
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
        (1..=3)
            .map(|index| MemberSpec { name: format!("backup-{index}"), region: "bulk-hdd".into() })
            .collect()
    }

    fn put(operation: &str, document: &str) -> ComposedWrite {
        ComposedWrite::Put {
            operation: operation.to_owned(),
            document: document.to_owned(),
            body: BTreeMap::from([("status".to_owned(), DocumentValue::String("open".to_owned()))]),
        }
    }

    #[test]
    fn two_of_three_backup_acks_answer_the_full_outcome() {
        let dir = root("full");
        let mut set = BackupSet::create(&dir, "cell-backup", "created", &spec(), "tenant-a")
            .expect("the backup set creates");
        assert_eq!(set.identity().mode(), crate::identity::ClusterMode::Backup);
        assert_eq!(set.len(), 3);

        let outcome = set.await_ack(&put("op-1", "doc-1"), BackupPolicy::default());
        assert_eq!(outcome, BackupOutcome::Full { acks: 3 });
        assert!(outcome.is_full());

        // The idempotent re-fan replays: the same outcome, the same
        // durable state, no second application.
        let replay = set.await_ack(&put("op-1", "doc-1"), BackupPolicy::default());
        assert!(replay.is_full());
        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn below_the_minimum_answers_partial_and_the_active_write_stands() {
        let dir = root("partial");
        let mut set = BackupSet::create(&dir, "cell-backup", "created", &spec(), "tenant-a")
            .expect("creates");
        // A policy no set can satisfy: 4 of 3. The outcome is the
        // honest partial, never a refusal and never a rollback.
        let outcome = set.await_ack(&put("op-1", "doc-1"), BackupPolicy { min_acks: 4 });
        assert_eq!(outcome, BackupOutcome::Partial { acks: 3 });
        assert!(!outcome.is_full(), "partial names the truth: all three hold it");
        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn a_delete_fans_as_its_tombstone() {
        let dir = root("delete");
        let mut set = BackupSet::create(&dir, "cell-backup", "created", &spec(), "tenant-a")
            .expect("creates");
        set.await_ack(&put("op-1", "doc-1"), BackupPolicy::default());
        let outcome = set.await_ack(
            &ComposedWrite::Delete { operation: "op-2".into(), document: "doc-1".into() },
            BackupPolicy::default(),
        );
        assert!(outcome.is_full(), "the tombstone lands on all three");
        let _ = std::fs::remove_dir_all(&dir);
    }
}
