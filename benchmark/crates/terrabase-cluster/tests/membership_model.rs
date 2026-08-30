//! Bounded-exhaustive model checking of membership (replica-set
//! epoch) transitions — qualification gap-sweep row 15
//! (`docs/evidence/qualification-gap-sweep.md`), GOAL.md
//! "Correctness and simulation": membership transitions require
//! model-based or formal state-machine checking in addition to
//! ordinary tests.
//!
//! The model drives the REAL `terrabase-cluster` durability types —
//! [`Membership`] validation and
//! [`DurabilityCoordinator::advance_membership`] fencing, `submit`,
//! `record_ack`, `set_member_active`, `set_region_reachable` —
//! through every interleaving of configuration transitions, commits
//! on both sides of a transition window, fault toggles, and stale
//! probes, up to the bounds recorded in
//! `docs/evidence/model-checking-lane.md`.
//!
//! ## Modeled system and the transition window
//!
//! Two real `DurabilityCoordinator`s represent two members of one
//! replica set that observe a configuration change at different
//! times: the window where side 0 still operates the OLD
//! configuration and side 1 already operates the NEW one — the
//! overlapping-quorum window row 15 names. Both sides walk ONE
//! shared configuration chain (the serialized-installation protocol
//! assumption; see "Seams" below): a side at the chain tail may mint
//! the next configuration — through its own real
//! `advance_membership`, which refuses stale versions and
//! non-overlapping shapes — and a side behind the tail may only
//! catch up to the already-agreed next entry.
//!
//! ## Invariants
//!
//! - **INV-A (no split replica set / quorum intersection):** whenever
//!   the two live sides hold DIFFERENT effective configurations, the
//!   two configurations share at least one member: two
//!   configurations that can commit concurrently are never disjoint.
//!   Every consecutive chain pair also overlaps (the old quorum
//!   boundary is never lost by a step).
//! - **INV-B (identity/counter never reused):** one `(generation,
//!   epoch)` pair is never bound to two different member sets in a
//!   run, and both counters strictly increase along the chain.
//! - **INV-C (stale-epoch operations are typed refusals):** an
//!   acknowledgment pinned at a superseded version refuses with
//!   `StaleMembership`, an operation pinned at a superseded version
//!   refuses with `StaleOperation` at the fencing boundary, a stale
//!   or disjoint advance refuses with `StaleVersion`/`NoOverlap`
//!   exactly when it must — and never with a receipt. Every receipt
//!   names exactly the configuration that issued it and contributors
//!   seated in it.
//!
//! ## Seams, stated honestly
//!
//! - `DurabilityCoordinator` is not `Clone` and its internal
//!   acknowledgment ledger cannot be re-seeded through the public API
//!   after a version advance, so the model rebuilds each side from a
//!   canonical summary before every step. Operations pinned at a
//!   superseded version are probed at the fencing boundary (inside
//!   the advance step, against the live coordinator that still holds
//!   them) and then retired from the summary.
//! - Stored acknowledgments of members that are currently
//!   deactivated cannot be re-seeded either; the summary drops them
//!   and the next `submit` re-establishes them idempotently (the
//!   public API is duplicate-tolerant), so completion behavior is
//!   unchanged.
//! - The serialized chain is a PROTOCOL assumption, not something
//!   this crate's API enforces across coordinators. The negative
//!   controls demonstrate the checker detects a disjoint fresh
//!   install (split replica set) and a version-counter rebind when
//!   that assumption is dropped.

#[path = "model_checker.rs"]
mod model_checker;

use std::collections::{BTreeMap, BTreeSet};

use model_checker::{explore, BoundedModel, Bounds, Violation};
use terrabase::replication::{MemberId, RegionId};
use terrabase_cluster::{
    DurabilityCoordinator, DurabilityError, DurabilityFailure, DurabilityLevel, DurableAck,
    Membership, MembershipError, RegionMembers,
};

/// One configuration in the shared chain, as a canonical summary.
#[derive(Clone, Debug, Eq, PartialEq)]
struct ConfigDesc {
    generation: u64,
    epoch: u64,
    /// Region name -> exactly two sorted seat names.
    seats: Vec<(String, Vec<String>)>,
}

impl ConfigDesc {
    fn build(&self) -> Membership {
        let regions = self
            .seats
            .iter()
            .map(|(region, seats)| {
                RegionMembers::try_new(region, [seats[0].as_str(), seats[1].as_str()])
                    .expect("model shapes keep two distinct seats")
            })
            .collect();
        Membership::new(self.generation, self.epoch, regions)
            .expect("model shapes are validated at mint time")
    }

    fn version(&self) -> (u64, u64) {
        (self.generation, self.epoch)
    }

    fn members(&self) -> BTreeSet<String> {
        self.seats.iter().flat_map(|(_, seats)| seats.iter().cloned()).collect()
    }

    fn region_of(&self, member: &str) -> String {
        self.seats
            .iter()
            .find(|(_, seats)| seats.iter().any(|seat| seat == member))
            .map(|(region, _)| region.clone())
            .expect("member of the configuration has a region")
    }
}

/// One operation's per-side durable state (the rebuild mirror).
#[derive(Clone, Debug, Default, Eq, PartialEq)]
struct OpSide {
    /// The version at which this side first durably stored the
    /// operation; `None` until something was stored.
    first: Option<(u64, u64)>,
    /// Members whose acknowledgments are stored on this side.
    acked: BTreeSet<String>,
}

/// The canonical model summary. Everything the two real coordinators
/// will do next is a function of these fields.
#[derive(Clone, Debug, Default)]
struct ModelState {
    chain: Vec<ConfigDesc>,
    pos: [usize; 2],
    /// member -> active, per side; absent means active.
    active: [BTreeMap<String, bool>; 2],
    /// region -> reachable, per side; absent means reachable.
    reachable: [BTreeMap<String, bool>; 2],
    ops: BTreeMap<u32, [OpSide; 2]>,
    next_op: u32,
    /// Observations of the LAST transition only — checked
    /// transitionally and deliberately excluded from `key`.
    observations: Vec<Observation>,
}

/// A structured observation of one real API outcome.
#[derive(Clone, Debug, Eq, PartialEq)]
enum Observation {
    Receipt {
        side: usize,
        op: u32,
        generation: u64,
        epoch: u64,
        contributors: Vec<String>,
    },
    SubmitFailure {
        side: usize,
        op: u32,
        stale_operation: bool,
    },
    AdvanceAccepted {
        side: usize,
        generation: u64,
        epoch: u64,
        retired_ops: Vec<u32>,
        all_retired_refused_stale: bool,
        /// Operations retired WITHOUT a live fencing probe: their
        /// entire acknowledgment set sits on deactivated members, so
        /// the rebuilt coordinator cannot hold them (see the seams in
        /// the module docs). The real refusal for that shape is
        /// proven straight-line in the focused drill.
        retired_unprobed_ops: Vec<u32>,
    },
    AdvanceRefused {
        side: usize,
        generation: u64,
        epoch: u64,
        refused_stale_version: bool,
        refused_no_overlap: bool,
        expect_stale_version: bool,
        expect_no_overlap: bool,
    },
    StaleAckRefused {
        side: usize,
        stale_membership: bool,
    },
    FreshInstall {
        side: usize,
        generation: u64,
        epoch: u64,
    },
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum OpChoice {
    Fresh,
    Known(u32),
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum AdvanceTarget {
    /// Adopt the already-agreed next chain entry.
    CatchUp,
    /// Mint chain entry `shape` at version candidate `version` from
    /// the acting side's current version.
    Mint { shape: usize, version: usize },
}

#[derive(Clone, Debug, Eq, PartialEq)]
enum Step {
    Submit {
        side: usize,
        op: OpChoice,
        level: DurabilityLevel,
    },
    Advance {
        side: usize,
        target: AdvanceTarget,
    },
    ToggleMember {
        side: usize,
        member: String,
        active: bool,
    },
    ToggleRegion {
        side: usize,
        region: String,
        reachable: bool,
    },
    ProbeStaleAck {
        side: usize,
    },
    /// Negative control only: install a configuration on side 1 by
    /// DIRECT construction, bypassing `advance_membership`.
    InstallFresh {
        shape: usize,
        version: usize,
    },
}

/// Version candidates applied as deltas to the acting side's current
/// `(generation, epoch)`. Only `(+1, +1)` can be accepted; the others
/// exist to be refused typed.
const VERSION_CANDIDATES: [(u64, u64); 4] = [(1, 1), (1, 0), (0, 1), (0, 0)];

/// The membership model. `shapes` is the configuration universe
/// (version-less seat layouts); `allow_fresh_install` widens the step
/// set for the negative controls.
struct MembershipModel {
    shapes: Vec<Vec<(String, Vec<String>)>>,
    faults: bool,
    max_ops: usize,
    initial_shape: usize,
    allow_fresh_install: bool,
    /// Negative controls: restrict `InstallFresh` to one version
    /// candidate so the intended invariant is the one that fires.
    fresh_install_version: Option<usize>,
    /// Negative controls: offer ONLY the rogue `InstallFresh` step —
    /// the minimal space in which the control's violation is the
    /// shortest one the walk can find.
    fresh_install_only: bool,
}

impl MembershipModel {
    fn desc(&self, shape: usize, version: (u64, u64)) -> ConfigDesc {
        ConfigDesc { generation: version.0, epoch: version.1, seats: self.shapes[shape].clone() }
    }

    fn is_active(&self, state: &ModelState, side: usize, member: &str) -> bool {
        state.active[side].get(member).copied().unwrap_or(true)
    }

    fn is_reachable(&self, state: &ModelState, side: usize, region: &str) -> bool {
        state.reachable[side].get(region).copied().unwrap_or(true)
    }

    /// Rebuilds one side's real coordinator from the canonical
    /// summary. Seams: acknowledgments pinned at superseded versions
    /// cannot (and need not) be re-seeded — see the module docs.
    fn rebuild(&self, state: &ModelState, side: usize) -> DurabilityCoordinator {
        let desc = &state.chain[state.pos[side]];
        let mut coordinator = DurabilityCoordinator::new(desc.build());
        for (member, active) in &state.active[side] {
            if desc.members().contains(member) && !*active {
                coordinator
                    .set_member_active(&MemberId::try_new(member).expect("valid member"), false)
                    .expect("flagged member is seated");
            }
        }
        for (region, reachable) in &state.reachable[side] {
            if desc.seats.iter().any(|(name, _)| name == region) && !*reachable {
                coordinator
                    .set_region_reachable(&RegionId::try_new(region).expect("valid region"), false)
                    .expect("flagged region is selected");
            }
        }
        let version = desc.version();
        for (op, sides) in &state.ops {
            if sides[side].first == Some(version) {
                for member in &sides[side].acked {
                    if !self.is_active(state, side, member) {
                        continue;
                    }
                    coordinator
                        .record_ack(DurableAck::new(
                            format!("op-{op}"),
                            MemberId::try_new(member).expect("valid member"),
                            RegionId::try_new(&desc.region_of(member)).expect("valid region"),
                            version.0,
                            version.1,
                        ))
                        .expect("current-version acknowledgment replays");
                }
            }
        }
        coordinator
    }

    /// The submit coordinator seat: the first ACTIVE seat of the
    /// side's current configuration, or the first seat at all when
    /// every seat is down (the real API then answers
    /// `InvalidCoordinator`).
    fn coordinator_member(&self, state: &ModelState, side: usize) -> String {
        let members: Vec<String> = state.chain[state.pos[side]].members().into_iter().collect();
        members
            .iter()
            .find(|member| self.is_active(state, side, member))
            .cloned()
            .unwrap_or_else(|| members[0].clone())
    }
}

impl BoundedModel for MembershipModel {
    type State = ModelState;
    type Step = Step;

    fn initial(&self) -> ModelState {
        let chain = vec![self.desc(self.initial_shape, (1, 1))];
        ModelState { chain, ..ModelState::default() }
    }

    fn steps(&self, state: &ModelState) -> Vec<Step> {
        let mut steps = Vec::new();
        if self.fresh_install_only {
            let versions = match self.fresh_install_version {
                Some(version) => vec![version],
                None => (0..VERSION_CANDIDATES.len()).collect(),
            };
            for shape in 0..self.shapes.len() {
                for version in &versions {
                    steps.push(Step::InstallFresh { shape, version: *version });
                }
            }
            return steps;
        }
        for side in [0usize, 1] {
            let current = state.chain[state.pos[side]].version();
            // Submits: a fresh operation at both levels, plus the most
            // recent current-version operation at both levels.
            if state.ops.len() < self.max_ops {
                for level in [DurabilityLevel::RegionalDurable, DurabilityLevel::GlobalDurable] {
                    steps.push(Step::Submit { side, op: OpChoice::Fresh, level });
                }
            }
            let known = state
                .ops
                .iter()
                .rev()
                .find(|(_, sides)| sides[side].first == Some(current))
                .map(|(op, _)| *op);
            if let Some(op) = known {
                for level in [DurabilityLevel::RegionalDurable, DurabilityLevel::GlobalDurable] {
                    steps.push(Step::Submit { side, op: OpChoice::Known(op), level });
                }
            }
            // Advances: catch up to the agreed next entry, or (at the
            // tail) mint any shape at any version candidate.
            if state.pos[side] + 1 < state.chain.len() {
                steps.push(Step::Advance { side, target: AdvanceTarget::CatchUp });
            } else {
                for shape in 0..self.shapes.len() {
                    for version in 0..VERSION_CANDIDATES.len() {
                        steps.push(Step::Advance {
                            side,
                            target: AdvanceTarget::Mint { shape, version },
                        });
                    }
                }
            }
            // Faults and stale probes.
            if self.faults {
                for member in state.chain[state.pos[side]].members() {
                    let active = self.is_active(state, side, &member);
                    steps.push(Step::ToggleMember { side, member, active: !active });
                }
                for (region, _) in &state.chain[state.pos[side]].seats {
                    let reachable = self.is_reachable(state, side, region);
                    steps.push(Step::ToggleRegion {
                        side,
                        region: region.clone(),
                        reachable: !reachable,
                    });
                }
            }
            if state.pos[side] > 0 {
                steps.push(Step::ProbeStaleAck { side });
            }
        }
        if self.allow_fresh_install {
            let versions = match self.fresh_install_version {
                Some(version) => vec![version],
                None => (0..VERSION_CANDIDATES.len()).collect(),
            };
            for shape in 0..self.shapes.len() {
                for version in &versions {
                    steps.push(Step::InstallFresh { shape, version: *version });
                }
            }
        }
        steps
    }

    fn apply(&self, state: &ModelState, step: &Step) -> ModelState {
        let mut next = state.clone();
        next.observations.clear();
        match step {
            Step::Submit { side, op, level } => {
                let op_id = match op {
                    OpChoice::Fresh => {
                        let id = next.next_op;
                        next.next_op += 1;
                        next.ops.entry(id).or_default();
                        id
                    }
                    OpChoice::Known(id) => *id,
                };
                let mut coordinator = self.rebuild(&next, *side);
                let member = MemberId::try_new(&self.coordinator_member(&next, *side))
                    .expect("valid member");
                let outcome = coordinator.submit(&format!("op-{op_id}"), &member, *level);
                let version = next.chain[next.pos[*side]].version();
                let record = &mut next.ops.get_mut(&op_id).expect("op registered")[*side];
                match outcome {
                    Ok(receipt) => {
                        next.observations.push(Observation::Receipt {
                            side: *side,
                            op: op_id,
                            generation: receipt.generation(),
                            epoch: receipt.epoch(),
                            contributors: receipt
                                .contributors()
                                .iter()
                                .map(|ack| ack.member().as_str().to_owned())
                                .collect(),
                        });
                        record.first = Some(version);
                        for contributor in receipt.contributors() {
                            record.acked.insert(contributor.member().as_str().to_owned());
                        }
                    }
                    Err(failure) => {
                        let stale_operation =
                            matches!(failure, DurabilityFailure::StaleOperation { .. });
                        next.observations.push(Observation::SubmitFailure {
                            side: *side,
                            op: op_id,
                            stale_operation,
                        });
                        if let Some(observation) = failure.observation() {
                            if !observation.stored().is_empty() {
                                record.first = Some(version);
                                for ack in observation.stored() {
                                    record.acked.insert(ack.member().as_str().to_owned());
                                }
                            }
                        }
                    }
                }
            }
            Step::Advance { side, target } => {
                let current = next.chain[next.pos[*side]].version();
                let (target_desc, from_tail) = match target {
                    AdvanceTarget::CatchUp => (next.chain[next.pos[*side] + 1].clone(), false),
                    AdvanceTarget::Mint { shape, version } => {
                        let delta = VERSION_CANDIDATES[*version];
                        (self.desc(*shape, (current.0 + delta.0, current.1 + delta.1)), true)
                    }
                };
                // The expected refusal, computed from the real rule:
                // both counters must strictly increase, then the new
                // configuration must overlap the old one.
                let target_version = target_desc.version();
                let expect_stale_version =
                    target_version.0 <= current.0 || target_version.1 <= current.1;
                let expect_no_overlap = !expect_stale_version
                    && target_desc
                        .members()
                        .intersection(&next.chain[next.pos[*side]].members())
                        .next()
                        .is_none();
                let mut coordinator = self.rebuild(&next, *side);
                match coordinator.advance_membership(target_desc.build()) {
                    Err(error) => {
                        next.observations.push(Observation::AdvanceRefused {
                            side: *side,
                            generation: target_desc.generation,
                            epoch: target_desc.epoch,
                            refused_stale_version: matches!(
                                error,
                                MembershipError::StaleVersion { .. }
                            ),
                            refused_no_overlap: matches!(error, MembershipError::NoOverlap),
                            expect_stale_version,
                            expect_no_overlap,
                        });
                    }
                    Ok(()) => {
                        // The fencing boundary, live: every operation
                        // this side pinned at the OLD version whose
                        // acknowledgments survived the rebuild must now
                        // refuse typed. The probe seat comes from the
                        // NEW configuration (submit checks the seat
                        // before the operation version). Operations
                        // whose entire acknowledgment set sits on
                        // deactivated members could not be re-seeded
                        // into the rebuilt coordinator; they are
                        // retired without a live probe — the real
                        // refusal for that shape is proven straight-
                        // line in the focused drill below.
                        let probe_seat = MemberId::try_new(
                            target_desc.members().first().expect("configuration has members"),
                        )
                        .expect("valid member");
                        let mut retired_ops = Vec::new();
                        let mut retired_unprobed_ops = Vec::new();
                        let mut all_retired_refused_stale = true;
                        for (op, sides) in next.ops.iter() {
                            if sides[*side].first == Some(current) {
                                if sides[*side].acked.is_empty() {
                                    retired_unprobed_ops.push(*op);
                                    continue;
                                }
                                let refused = matches!(
                                    coordinator.submit(
                                        &format!("op-{op}"),
                                        &probe_seat,
                                        DurabilityLevel::RegionalDurable
                                    ),
                                    Err(DurabilityFailure::StaleOperation { .. })
                                );
                                if !refused {
                                    all_retired_refused_stale = false;
                                }
                                retired_ops.push(*op);
                            }
                        }
                        for op in retired_unprobed_ops.iter().chain(retired_ops.iter()) {
                            next.ops.get_mut(op).expect("op registered")[*side] = OpSide::default();
                        }
                        // Summary: extend or walk the chain; a real
                        // advance resets every fault flag on that side.
                        if from_tail {
                            next.chain.push(target_desc.clone());
                        }
                        next.pos[*side] =
                            if from_tail { next.chain.len() - 1 } else { next.pos[*side] + 1 };
                        next.active[*side].clear();
                        next.reachable[*side].clear();
                        next.observations.push(Observation::AdvanceAccepted {
                            side: *side,
                            generation: target_desc.generation,
                            epoch: target_desc.epoch,
                            retired_ops,
                            all_retired_refused_stale,
                            retired_unprobed_ops,
                        });
                    }
                }
            }
            Step::ToggleMember { side, member, active } => {
                let mut coordinator = self.rebuild(&next, *side);
                coordinator
                    .set_member_active(&MemberId::try_new(member).expect("valid member"), *active)
                    .expect("toggle targets a seated member");
                next.active[*side].insert(member.clone(), *active);
                // Seam: acknowledgments of a now-inactive member cannot
                // be re-seeded on rebuild; the next submit restores
                // them idempotently. See the module docs.
                if !*active {
                    for sides in next.ops.values_mut() {
                        sides[*side].acked.remove(member);
                    }
                }
            }
            Step::ToggleRegion { side, region, reachable } => {
                let mut coordinator = self.rebuild(&next, *side);
                coordinator
                    .set_region_reachable(
                        &RegionId::try_new(region).expect("valid region"),
                        *reachable,
                    )
                    .expect("toggle targets a selected region");
                next.reachable[*side].insert(region.clone(), *reachable);
            }
            Step::ProbeStaleAck { side } => {
                let old = next.chain[next.pos[*side] - 1].clone();
                let mut coordinator = self.rebuild(&next, *side);
                let member =
                    MemberId::try_new(old.members().first().expect("configuration has members"))
                        .expect("valid member");
                let region =
                    RegionId::try_new(&old.region_of(member.as_str())).expect("valid region");
                let outcome = coordinator.record_ack(DurableAck::new(
                    "stale-probe".to_owned(),
                    member,
                    region,
                    old.generation,
                    old.epoch,
                ));
                next.observations.push(Observation::StaleAckRefused {
                    side: *side,
                    stale_membership: matches!(
                        outcome,
                        Err(DurabilityError::StaleMembership { .. })
                    ),
                });
            }
            Step::InstallFresh { shape, version } => {
                // Negative control: bypass advance_membership entirely
                // and construct side 1's coordinator directly.
                let current = next.chain[next.pos[1]].version();
                let delta = VERSION_CANDIDATES[*version];
                let desc = self.desc(*shape, (current.0 + delta.0, current.1 + delta.1));
                let mut coordinator = DurabilityCoordinator::new(desc.build());
                // The freshly installed side can commit immediately.
                let seat =
                    MemberId::try_new(desc.members().first().expect("configuration has members"))
                        .expect("valid member");
                assert!(
                    coordinator
                        .submit("fresh-side-write", &seat, DurabilityLevel::RegionalDurable)
                        .is_ok(),
                    "the fresh install must be able to commit — that is what makes it a split"
                );
                next.chain.push(desc.clone());
                next.pos[1] = next.chain.len() - 1;
                next.active[1].clear();
                next.reachable[1].clear();
                next.observations.push(Observation::FreshInstall {
                    side: 1,
                    generation: desc.generation,
                    epoch: desc.epoch,
                });
            }
        }
        next
    }

    fn key(&self, state: &ModelState) -> String {
        // Observations are per-transition and excluded: two states
        // with equal summaries behave identically under every future
        // step, which is exactly the memoization contract.
        format!(
            "{:?}|{:?}|{:?}|{:?}|{:?}|{}",
            state.chain, state.pos, state.active, state.reachable, state.ops, state.next_op
        )
    }

    fn check_state(&self, state: &ModelState) -> Result<(), String> {
        // INV-B: both counters strictly increase along the chain, and
        // one version pair is bound to at most one member set.
        for (index, desc) in state.chain.iter().enumerate() {
            if index > 0 {
                let previous = &state.chain[index - 1];
                if desc.generation <= previous.generation || desc.epoch <= previous.epoch {
                    return Err(format!(
                        "INV-B: chain step {index} does not advance both counters: {:?} -> {:?}",
                        previous.version(),
                        desc.version()
                    ));
                }
                // INV-A (chain form): a step never loses the old quorum
                // boundary.
                if desc.members().intersection(&previous.members()).next().is_none() {
                    return Err(format!(
                        "INV-A: chain step {index} loses the old quorum boundary: {:?} -> {:?}",
                        previous.members(),
                        desc.members()
                    ));
                }
            }
            for other in &state.chain[index + 1..] {
                if desc.version() == other.version() && desc.members() != other.members() {
                    return Err(format!(
                        "INV-B: version counter {:?} reused for different member sets",
                        desc.version()
                    ));
                }
            }
        }
        // INV-A (window form): the two live sides never hold disjoint
        // configurations.
        let left = &state.chain[state.pos[0]];
        let right = &state.chain[state.pos[1]];
        if left != right && left.members().intersection(&right.members()).next().is_none() {
            return Err(format!(
                "INV-A: split replica set — two live configurations with disjoint quorums: \
                 {:?} at {:?} vs {:?} at {:?}",
                left.members(),
                left.version(),
                right.members(),
                right.version()
            ));
        }
        Ok(())
    }

    fn check_transition(
        &self,
        before: &ModelState,
        _step: &Step,
        after: &ModelState,
    ) -> Result<(), String> {
        for observation in &after.observations {
            match observation {
                Observation::Receipt { side, op, generation, epoch, contributors } => {
                    let current = before.chain[before.pos[*side]].clone();
                    if (*generation, *epoch) != current.version() {
                        return Err(format!(
                            "INV-C: receipt for op-{op} names version ({generation}, {epoch}) \
                             but the committing configuration is {:?}",
                            current.version()
                        ));
                    }
                    let members = current.members();
                    if contributors.iter().any(|member| !members.contains(member)) {
                        return Err(format!(
                            "INV-C: receipt for op-{op} names contributors outside the \
                             committing configuration: {contributors:?}"
                        ));
                    }
                }
                Observation::SubmitFailure { op, stale_operation, .. } => {
                    if *stale_operation {
                        return Err(format!(
                            "INV-C: op-{op} was refused StaleOperation although it is pinned \
                             at the current version — the sweep only submits current-version \
                             operations"
                        ));
                    }
                }
                Observation::AdvanceAccepted { side, all_retired_refused_stale, .. } => {
                    if !*all_retired_refused_stale {
                        return Err(format!(
                            "INV-C: an operation pinned at the superseded version did not \
                             refuse StaleOperation after side {side} advanced"
                        ));
                    }
                    if after.pos[*side] <= before.pos[*side] {
                        return Err(format!(
                            "an accepted advance did not move side {side} forward in the chain"
                        ));
                    }
                }
                Observation::AdvanceRefused {
                    side,
                    generation,
                    epoch,
                    refused_stale_version,
                    refused_no_overlap,
                    expect_stale_version,
                    expect_no_overlap,
                } => {
                    if !*expect_stale_version && !*expect_no_overlap {
                        return Err(format!(
                            "INV-C: advance to ({generation}, {epoch}) on side {side} was \
                             refused although it is version-increasing and overlapping"
                        ));
                    }
                    if *refused_stale_version != *expect_stale_version
                        || *refused_no_overlap != *expect_no_overlap
                    {
                        return Err(format!(
                            "INV-C: advance to ({generation}, {epoch}) on side {side} refused \
                             with the wrong typed error (stale_version: {} of expected {}, \
                             no_overlap: {} of expected {})",
                            refused_stale_version,
                            expect_stale_version,
                            refused_no_overlap,
                            expect_no_overlap
                        ));
                    }
                }
                Observation::StaleAckRefused { stale_membership, .. } => {
                    if !*stale_membership {
                        return Err(
                            "INV-C: an acknowledgment pinned at a superseded version did not \
                             refuse StaleMembership"
                                .to_owned(),
                        );
                    }
                }
                Observation::FreshInstall { .. } => {}
            }
        }
        Ok(())
    }
}

/// All two-seat shapes over a member pool in one region.
fn single_region_shapes(pool: &[&str]) -> Vec<Vec<(String, Vec<String>)>> {
    let mut shapes = Vec::new();
    for left in 0..pool.len() {
        for right in (left + 1)..pool.len() {
            let mut seats = vec![pool[left].to_owned(), pool[right].to_owned()];
            seats.sort_unstable();
            shapes.push(vec![("r1".to_owned(), seats)]);
        }
    }
    shapes
}

/// r1 pairs over `local` members, each optionally plus an r2 pair
/// over `remote` members (additions, retirements, and the erasure of
/// a whole region).
fn two_region_shapes(local: &[&str], remote: &[&str]) -> Vec<Vec<(String, Vec<String>)>> {
    let mut local_pairs: Vec<Vec<String>> = Vec::new();
    for left in 0..local.len() {
        for right in (left + 1)..local.len() {
            let mut seats = vec![local[left].to_owned(), local[right].to_owned()];
            seats.sort_unstable();
            local_pairs.push(seats);
        }
    }
    let mut remote_pair: Vec<String> =
        remote.iter().copied().map(|member| member.to_owned()).collect();
    remote_pair.sort_unstable();
    let mut shapes = Vec::new();
    for r1 in &local_pairs {
        shapes.push(vec![("r1".to_owned(), r1.clone())]);
        shapes.push(vec![("r1".to_owned(), r1.clone()), ("r2".to_owned(), remote_pair.clone())]);
    }
    shapes
}

/// The exhaustive single-region sweep: every initial shape, every
/// trace of transitions up to the depth bound, no faults (faults live
/// in the two-region sweep below). `!hit_state_cap` keeps the
/// exhaustive-to-depth claim honest: if the state cap cut a trace the
/// test fails instead of quietly claiming coverage. `truncated` is
/// expected here — the version counters are unbounded, so longer
/// traces always exist beyond the depth bound.
#[test]
fn membership_transitions_are_exhaustively_checked_single_region() {
    let shapes = single_region_shapes(&["m0", "m1", "m2"]);
    let mut total_states = 0;
    let mut total_transitions = 0;
    let mut deepest = 0;
    for initial in 0..shapes.len() {
        let model = MembershipModel {
            shapes: shapes.clone(),
            faults: false,
            max_ops: 1,
            initial_shape: initial,
            allow_fresh_install: false,
            fresh_install_version: None,
            fresh_install_only: false,
        };
        let report = explore(&model, Bounds { max_depth: 6, max_states: 20_000 })
            .unwrap_or_else(|violation| panic!("membership invariant violated: {violation:?}"));
        assert!(
            !report.hit_state_cap,
            "exhaustive-to-depth claim broken by the state cap: {report:?}"
        );
        assert!(
            report.distinct_states >= 100,
            "suspiciously small space from initial {initial}: {report:?}"
        );
        total_states += report.distinct_states;
        total_transitions += report.transitions_fired;
        deepest = deepest.max(report.max_depth_reached);
    }
    println!(
        "row-15 single-region sweep: initials={} distinct_states={} transitions={} depth={}",
        shapes.len(),
        total_states,
        total_transitions,
        deepest
    );
}

/// The overlapping-window sweep with faults: two Regions, region
/// additions and erasures, member failures and WAN partitions
/// interleaved with the transition window, exhaustive over every
/// trace up to the depth bound. Both sides commit through real
/// receipts while the window is open.
#[test]
fn membership_transition_windows_hold_with_faults_two_region() {
    let shapes = two_region_shapes(&["a", "b", "c"], &["d", "e"]);
    let model = MembershipModel {
        shapes,
        faults: true,
        max_ops: 1,
        initial_shape: 0,
        allow_fresh_install: false,
        fresh_install_version: None,
        fresh_install_only: false,
    };
    let report = explore(&model, Bounds { max_depth: 5, max_states: 150_000 })
        .unwrap_or_else(|violation| panic!("membership invariant violated: {violation:?}"));
    assert!(!report.hit_state_cap, "exhaustive-to-depth claim broken by the state cap: {report:?}");
    assert!(report.distinct_states >= 500, "suspiciously small space: {report:?}");
    println!(
        "row-15 two-region fault sweep: distinct_states={} transitions={} depth={}",
        report.distinct_states, report.transitions_fired, report.max_depth_reached
    );
}

/// Negative control A: installing a DISJOINT configuration by direct
/// construction (bypassing `advance_membership`) is exactly the split
/// replica set INV-A exists to catch — the checker must flag it, with
/// a trace ending in the rogue install that can commit. The pool of
/// four members is what makes a disjoint two-seat shape possible.
#[test]
fn negative_control_disjoint_install_is_detected_as_a_split() {
    let shapes = single_region_shapes(&["m0", "m1", "m2", "m3"]);
    let model = MembershipModel {
        shapes,
        faults: false,
        max_ops: 1,
        initial_shape: 0,
        allow_fresh_install: true,
        fresh_install_version: Some(0),
        fresh_install_only: true,
    };
    let violation: Violation<MembershipModel> =
        explore(&model, Bounds { max_depth: 3, max_states: 5_000 })
            .expect_err("a disjoint fresh install must violate the window-intersection invariant");
    assert!(
        violation.message.contains("INV-A"),
        "expected the split-brain (INV-A) violation, got: {}",
        violation.message
    );
    assert!(
        matches!(violation.trace.last(), Some(Step::InstallFresh { .. })),
        "the trace must end in the rogue install: {:?}",
        violation.trace
    );
}

/// Negative control B: binding a `(generation, epoch)` pair that the
/// run already used to a DIFFERENT member set is the counter reuse
/// INV-B exists to catch — reported as a chain step that fails to
/// advance the counters onto a differently-composed configuration.
#[test]
fn negative_control_version_rebind_is_detected() {
    let shapes = single_region_shapes(&["m0", "m1", "m2"]);
    let model = MembershipModel {
        shapes,
        faults: false,
        max_ops: 1,
        initial_shape: 0,
        allow_fresh_install: true,
        fresh_install_version: Some(3),
        fresh_install_only: true,
    };
    let violation: Violation<MembershipModel> =
        explore(&model, Bounds { max_depth: 3, max_states: 5_000 })
            .expect_err("rebinding a used version pair to different members must violate INV-B");
    assert!(
        violation.message.contains("INV-B"),
        "expected the counter-reuse (INV-B) violation, got: {}",
        violation.message
    );
    assert!(
        matches!(violation.trace.last(), Some(Step::InstallFresh { .. })),
        "the trace must end in the rogue install: {:?}",
        violation.trace
    );
}

/// The focused straight-line drill of every fencing refusal named by
/// row 15, on one live coordinator across one overlapping transition.
#[test]
fn focused_stale_epoch_refusals_are_typed_across_an_overlapping_transition() {
    let old = ConfigDesc {
        generation: 3,
        epoch: 7,
        seats: vec![("r1".to_owned(), vec!["m0".to_owned(), "m1".to_owned()])],
    }
    .build();
    let next = ConfigDesc {
        generation: 4,
        epoch: 8,
        seats: vec![("r1".to_owned(), vec!["m1".to_owned(), "m2".to_owned()])],
    }
    .build();
    let mut coordinator = DurabilityCoordinator::new(old);
    let m0 = MemberId::try_new("m0").expect("valid");
    let m1 = MemberId::try_new("m1").expect("valid");
    let m2 = MemberId::try_new("m2").expect("valid");
    let r1 = RegionId::try_new("r1").expect("valid");

    // An operation pinned at the old version, partially stored.
    coordinator
        .record_ack(DurableAck::new("inflight".to_owned(), m0.clone(), r1.clone(), 3, 7))
        .expect("current-version acknowledgment stores");

    // The advance matrix: every non-increasing shape refuses typed.
    for (generation, epoch) in [(3, 7), (4, 7), (3, 8), (2, 9), (4, 6)] {
        let stale = ConfigDesc {
            generation,
            epoch,
            seats: vec![("r1".to_owned(), vec!["m1".to_owned(), "m2".to_owned()])],
        }
        .build();
        assert_eq!(
            coordinator.advance_membership(stale),
            Err(MembershipError::StaleVersion { generation, epoch }),
            "({generation}, {epoch}) must refuse StaleVersion"
        );
    }
    // A disjoint shape at increasing versions refuses NoOverlap.
    let disjoint = ConfigDesc {
        generation: 9,
        epoch: 9,
        seats: vec![("r1".to_owned(), vec!["m2".to_owned(), "m3".to_owned()])],
    }
    .build();
    assert_eq!(coordinator.advance_membership(disjoint), Err(MembershipError::NoOverlap));

    // The accepted overlapping advance (old quorum m0/m1, new m1/m2).
    coordinator.advance_membership(next).expect("the overlapping transition is accepted");

    // The in-flight operation refuses typed at the boundary.
    assert_eq!(
        coordinator.submit("inflight", &m1, DurabilityLevel::RegionalDurable),
        Err(DurabilityFailure::StaleOperation {
            operation: "inflight".to_owned(),
            generation: 3,
            epoch: 7
        })
    );
    // A stale acknowledgment refuses typed.
    assert!(matches!(
        coordinator.record_ack(DurableAck::new("after".to_owned(), m1.clone(), r1.clone(), 3, 7)),
        Err(DurabilityError::StaleMembership { .. })
    ));
    // A duplicate acknowledgment in the NEW configuration refuses
    // typed as a duplicate, not as a second store.
    coordinator
        .record_ack(DurableAck::new("after".to_owned(), m1.clone(), r1.clone(), 4, 8))
        .expect("current-version acknowledgment stores");
    assert!(matches!(
        coordinator.record_ack(DurableAck::new("after".to_owned(), m1.clone(), r1.clone(), 4, 8)),
        Err(DurabilityError::DuplicateAcknowledgement { .. })
    ));
    // A retired member cannot ack into the new configuration: m0 is
    // no longer selected, so its acknowledgment refuses UnknownMember.
    assert!(matches!(
        coordinator.record_ack(DurableAck::new("after".to_owned(), m0, r1.clone(), 4, 8)),
        Err(DurabilityError::UnknownMember { .. })
    ));
    // The new configuration commits through the overlapping member.
    let receipt = coordinator
        .submit("new-epoch-write", &m2, DurabilityLevel::RegionalDurable)
        .expect("the new configuration commits");
    assert_eq!((receipt.generation(), receipt.epoch()), (4, 8));
    let _ = m2;
}
