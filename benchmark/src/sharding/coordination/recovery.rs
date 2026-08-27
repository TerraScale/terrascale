//! Per-shard recovery reporting (SH-05, PB-5040 under
//! `docs/sharding/dispositions.md`).
//!
//! V1 coordinates shard-local replication, backup, restore, and rejoin
//! cleanup through sharded handlers; the parity obligation this seam owns
//! is the receipt shape, not the handler: a recovery observation must
//! identify every Shard and every Member that contributed to it, name the
//! owner's verdict per shard, and state its limits. A
//! [`ShardRecoveryReport`] is exactly that — a validated, secret-safe
//! composition of caller-supplied Member observations against a seated
//! topology, with no invented recovery behavior.
//!
//! The observations a report is assembled from are real engine facts in
//! the qualification lane (a Member "holds" a shard when its engine
//! resolves the shard's probe operations as committed, and its `through`
//! bound is the durable sequence it caught up to). What the report adds is
//! structure: only seated shards and Members enter it, every seated shard
//! must be reported (a recovery receipt that silently skipped a shard
//! would claim more than it observed), and the owner's absence is a
//! recorded verdict, never an error.

use crate::replication::{MemberId, RegionId};
use crate::types::StorageSequence;

use super::super::{ShardId, ShardTopology};

/// One Member's recovery observation for one shard, as the caller observed
/// it against the Member's durable state.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct MemberRecovery {
    member: MemberId,
    holds: bool,
    through: Option<StorageSequence>,
    region: Option<RegionId>,
    failure_domain: Option<String>,
}

impl MemberRecovery {
    /// Observes one Member as holding the shard's data, caught up through
    /// the given durable sequence (`None` when the observer cannot bound
    /// it — reported as unknown, never guessed).
    #[must_use]
    pub fn holds(member: MemberId, through: Option<StorageSequence>) -> Self {
        Self::observed(member, true, through)
    }

    /// Observes one Member as not holding the shard's data (closed, lost,
    /// or restored behind the shard's state).
    #[must_use]
    pub fn absent(member: MemberId) -> Self {
        Self::observed(member, false, None)
    }

    /// Observes one Member's holding verdict directly, with the durable
    /// sequence it is caught up through (`None` when unbounded). This is
    /// the lane-facing constructor: the verdict comes from the observer's
    /// own probe of the Member's durable state, never from this module.
    #[must_use]
    pub fn observed(member: MemberId, holds: bool, through: Option<StorageSequence>) -> Self {
        Self { member, holds, through, region: None, failure_domain: None }
    }

    /// Returns the observed Member.
    #[must_use]
    pub fn member(&self) -> &MemberId {
        &self.member
    }

    /// Reports whether the Member holds the shard's data.
    #[must_use]
    pub const fn holds_data(&self) -> bool {
        self.holds
    }

    /// Returns the durable sequence the Member is caught up through, when
    /// the observation bounded it.
    #[must_use]
    pub const fn through(&self) -> Option<StorageSequence> {
        self.through
    }

    /// Returns the Region the Member sits in, filled by the report from
    /// the seated view.
    #[must_use]
    pub fn region(&self) -> Option<&RegionId> {
        self.region.as_ref()
    }

    /// Returns the Member's failure domain, filled by the report from the
    /// seated view.
    #[must_use]
    pub fn failure_domain(&self) -> Option<&str> {
        self.failure_domain.as_deref()
    }
}

/// One shard's recovery entry: the owner the topology seats and every
/// Member observation for that shard.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ShardRecovery {
    shard: ShardId,
    owner: MemberId,
    holdings: Vec<MemberRecovery>,
}

impl ShardRecovery {
    /// Returns the shard this entry reports.
    #[must_use]
    pub const fn shard(&self) -> ShardId {
        self.shard
    }

    /// Returns the Member the topology seats as the shard's owner.
    #[must_use]
    pub fn owner(&self) -> &MemberId {
        &self.owner
    }

    /// Returns every Member observation for this shard.
    #[must_use]
    pub fn holdings(&self) -> &[MemberRecovery] {
        &self.holdings
    }

    /// Reports whether the seated owner holds the shard's data: the
    /// recovery verdict for one shard.
    #[must_use]
    pub fn owner_holds(&self) -> bool {
        self.holdings.iter().any(|holding| holding.member() == &self.owner && holding.holds_data())
    }

    /// Returns the Members that hold the shard's data, in observation
    /// order.
    #[must_use]
    pub fn members_holding(&self) -> Vec<MemberId> {
        self.holdings
            .iter()
            .filter(|holding| holding.holds_data())
            .map(|holding| holding.member().clone())
            .collect()
    }
}

/// The validated per-shard recovery report over one seated topology:
/// every seated shard's owner verdict and every contributing Member,
/// secret-safe by construction.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ShardRecoveryReport {
    entries: Vec<ShardRecovery>,
}

impl ShardRecoveryReport {
    /// Assembles one report from per-shard Member observations against a
    /// seated topology. The report fills each observation's Region and
    /// failure domain from the view and validates every seat.
    ///
    /// # Errors
    ///
    /// Returns [`RecoveryError::ShardNotSeated`] for a shard the table
    /// does not map, [`RecoveryError::DuplicateShard`] when one shard is
    /// reported twice, [`RecoveryError::ShardMissing`] when a seated shard
    /// was not reported (a recovery receipt may not skip a shard),
    /// [`RecoveryError::MemberNotSeated`] for a Member the view does not
    /// seat, and [`RecoveryError::DuplicateMember`] when one Member is
    /// observed twice for one shard.
    ///
    /// # Panics
    ///
    /// Panics only if the internal completeness invariant fails after
    /// validation (a seated shard with no observation entry); the
    /// [`RecoveryError::ShardMissing`] check makes that unreachable.
    pub fn try_new(
        topology: &ShardTopology,
        observations: &[(ShardId, Vec<MemberRecovery>)],
    ) -> Result<Self, RecoveryError> {
        let seated = topology.table().shards();
        for (shard, holdings) in observations {
            if !seated.contains(shard) {
                return Err(RecoveryError::ShardNotSeated { shard: *shard });
            }
            for (index, observation) in holdings.iter().enumerate() {
                if !topology.view().contains_member(observation.member()) {
                    return Err(RecoveryError::MemberNotSeated {
                        member: observation.member().clone(),
                    });
                }
                if holdings[..index]
                    .iter()
                    .any(|existing| existing.member() == observation.member())
                {
                    return Err(RecoveryError::DuplicateMember {
                        shard: *shard,
                        member: observation.member().clone(),
                    });
                }
            }
        }
        for (index, (shard, _)) in observations.iter().enumerate() {
            if observations[..index].iter().any(|(seen, _)| seen == shard) {
                return Err(RecoveryError::DuplicateShard { shard: *shard });
            }
        }
        for seated_shard in &seated {
            if !observations.iter().any(|(shard, _)| shard == seated_shard) {
                return Err(RecoveryError::ShardMissing { shard: *seated_shard });
            }
        }

        let mut entries = Vec::new();
        for shard in seated {
            let (_, observations) = observations
                .iter()
                .find(|(observed, _)| observed == &shard)
                .expect("the completeness check seated every shard");
            let mut holdings = Vec::new();
            for observation in observations {
                if !topology.view().contains_member(observation.member()) {
                    return Err(RecoveryError::MemberNotSeated {
                        member: observation.member().clone(),
                    });
                }
                if holdings
                    .iter()
                    .any(|existing: &MemberRecovery| existing.member() == observation.member())
                {
                    return Err(RecoveryError::DuplicateMember {
                        shard,
                        member: observation.member().clone(),
                    });
                }
                let enriched = MemberRecovery {
                    member: observation.member.clone(),
                    holds: observation.holds,
                    through: observation.through,
                    region: topology.view().region_of(observation.member()).cloned(),
                    failure_domain: topology
                        .view()
                        .failure_domain_of(observation.member())
                        .map(str::to_owned),
                };
                holdings.push(enriched);
            }
            let owner = topology.owner_of(shard).ok_or(RecoveryError::ShardNotSeated { shard })?;
            entries.push(ShardRecovery { shard, owner, holdings });
        }

        Ok(Self { entries })
    }

    /// Returns every shard entry, in table order.
    #[must_use]
    pub fn entries(&self) -> &[ShardRecovery] {
        &self.entries
    }

    /// Returns the shard entry for one shard, when present.
    #[must_use]
    pub fn entry(&self, shard: ShardId) -> Option<&ShardRecovery> {
        self.entries.iter().find(|entry| entry.shard() == shard)
    }

    /// Reports the recovery verdict for one shard: its seated owner holds
    /// the shard's data. `false` for an unreported shard.
    #[must_use]
    pub fn recovered(&self, shard: ShardId) -> bool {
        self.entry(shard).is_some_and(ShardRecovery::owner_holds)
    }

    /// Returns the shards whose owners hold nothing, in table order.
    #[must_use]
    pub fn unrecovered_shards(&self) -> Vec<ShardId> {
        self.entries.iter().filter(|entry| !entry.owner_holds()).map(ShardRecovery::shard).collect()
    }

    /// Returns the Members that hold one shard's data, in observation
    /// order. Empty for an unreported shard.
    #[must_use]
    pub fn members_holding(&self, shard: ShardId) -> Vec<MemberId> {
        self.entry(shard).map_or_else(Vec::new, ShardRecovery::members_holding)
    }

    /// Returns the recorded limits of every recovery report.
    #[must_use]
    pub fn limits() -> &'static [&'static str] {
        &[
            "a report is an observation composition: catch-up and divergence repair are the replication plane's own qualified machinery",
            "an owner-absent shard is recorded unrecovered, never guessed recovered",
            "a `through` bound is the observed durable sequence at observation time, not a currency claim",
        ]
    }

    /// Renders the report text, one fact per line, secret-safe: shard and
    /// Member identities, verdicts, and bounds only.
    #[must_use]
    pub fn summary(&self) -> String {
        let mut lines = Vec::new();
        lines.push("scenario: sharded-recovery".to_owned());
        for entry in &self.entries {
            let holding = entry.members_holding();
            let holding_text = if holding.is_empty() {
                "(none)".to_owned()
            } else {
                holding
                    .iter()
                    .map(|member| member.as_str().to_owned())
                    .collect::<Vec<_>>()
                    .join(", ")
            };
            lines.push(format!(
                "shard: {} owner: {} recovered: {} held-by: {holding_text}",
                entry.shard(),
                entry.owner(),
                if entry.owner_holds() { "yes" } else { "no" },
            ));
        }
        for limit in Self::limits() {
            lines.push(format!("limit: {limit}"));
        }
        lines.join("\n")
    }
}

/// Typed refusals of recovery-report assembly.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum RecoveryError {
    /// A reported shard is not seated by the topology's table.
    ShardNotSeated {
        /// The unseated shard.
        shard: ShardId,
    },
    /// One shard was reported twice.
    DuplicateShard {
        /// The twice-reported shard.
        shard: ShardId,
    },
    /// A seated shard was not reported: a recovery receipt may not skip a
    /// shard.
    ShardMissing {
        /// The skipped shard.
        shard: ShardId,
    },
    /// A reported Member is not seated by the view.
    MemberNotSeated {
        /// The unseated Member.
        member: MemberId,
    },
    /// One Member was observed twice for one shard.
    DuplicateMember {
        /// The shard with the duplicate observation.
        shard: ShardId,
        /// The twice-observed Member.
        member: MemberId,
    },
}
