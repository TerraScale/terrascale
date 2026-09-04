//! Deterministic contextual bandit selection for automatic indexes.
//!
//! The production loop deliberately has no clock or random-number dependency.
//! A selector advances a logical tick for each selection call, and callers
//! supply measured reward and maintenance cost after an activation window.
//! This keeps replay and durable recovery bit-for-bit deterministic.

use std::collections::BTreeMap;

use super::{Candidate, CandidateKind, CandidateSource};

/// Per-collection write budget expressed as maintenance work over base work.
#[derive(Clone, Copy, Debug, PartialEq)]
pub(crate) struct WriteBudget {
    base_cost: f64,
    maintenance_cost: f64,
    visible_indexes: usize,
}

impl WriteBudget {
    /// Starts an empty accounting window.
    pub(crate) const fn new(base_cost: f64) -> Self {
        Self { base_cost, maintenance_cost: 0.0, visible_indexes: 0 }
    }

    /// Adds measured maintenance work and one visible index.
    pub(crate) fn account(&mut self, maintenance_cost: f64) {
        let cost = finite_nonnegative(maintenance_cost);
        self.maintenance_cost = self.maintenance_cost + cost;
        self.visible_indexes = self.visible_indexes.saturating_add(1);
    }

    /// Tries to add one index, leaving the budget unchanged on refusal.
    pub(crate) fn try_account(&mut self, maintenance_cost: f64) -> bool {
        if !self.would_allow(maintenance_cost) {
            return false;
        }
        self.account(maintenance_cost);
        true
    }

    /// Returns whether another candidate can fit under ADR 0045's budget.
    #[must_use]
    pub(crate) fn allows(&self) -> bool {
        self.base_cost.is_finite()
            && self.base_cost >= 0.0
            && self.maintenance_cost.is_finite()
            && self.budget_limit().is_finite()
            && self.maintenance_cost <= self.budget_limit()
            && self.visible_indexes < super::later::MAX_VISIBLE_PER_COLLECTION
    }

    /// Returns whether an index with this maintenance cost can be added.
    #[must_use]
    pub(crate) fn would_allow(&self, maintenance_cost: f64) -> bool {
        let cost = finite_nonnegative_option(maintenance_cost);
        self.allows()
            && cost.is_some_and(|cost| {
                let next = self.maintenance_cost + cost;
                next.is_finite() && next <= self.budget_limit()
            })
    }

    /// Returns the remaining maintenance budget.
    #[must_use]
    pub(crate) fn remaining(&self) -> f64 {
        let limit = self.budget_limit();
        if !limit.is_finite() || !self.maintenance_cost.is_finite() {
            return 0.0;
        }
        (limit - self.maintenance_cost).max(0.0)
    }

    /// Returns the base write cost.
    #[must_use]
    pub(crate) const fn base_cost(&self) -> f64 {
        self.base_cost
    }

    /// Returns accounted maintenance cost.
    #[must_use]
    pub(crate) const fn maintenance_cost(&self) -> f64 {
        self.maintenance_cost
    }

    /// Returns the number of visible indexes.
    #[must_use]
    pub(crate) const fn visible_indexes(&self) -> usize {
        self.visible_indexes
    }

    fn budget_limit(&self) -> f64 {
        self.base_cost * super::later::WRITE_BUDGET_MULTIPLE
    }
}

/// Measured reward used by the contextual bandit implementation.
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub(crate) struct MeasuredReward {
    /// Logical work saved by the index. Negative values are retained as a
    /// regression signal and therefore are not clamped.
    pub(crate) work_saved: f64,
    /// Uncertainty estimate for the reward, in the same units as reward.
    pub(crate) uncertainty: f64,
}

/// Maximum bytes retained by one selector's arm table by default.
pub(crate) const DEFAULT_MAX_MEMORY_BYTES: usize = 1 << 20;
/// Maximum age in logical selection ticks before a candidate is force-explored.
pub(crate) const STARVATION_TICKS: u64 = 32;
const SNAPSHOT_MAGIC: &[u8; 4] = b"TBAB";
const SNAPSHOT_VERSION: u8 = 1;
const COLD_START_COST: f64 = 1.0;
/// Tie margin for bandit scores: score gaps below this count as ties so float
/// noise cannot flip deterministic key-order replay. Existing margins (cold
/// start at infinity, converged rewards near 1.0 and 100.0) exceed it by
/// orders of magnitude.
const SCORE_TIE_EPSILON: f64 = f64::EPSILON;
const ARM_FIXED_BYTES: usize = 64;
const MAX_SNAPSHOT_ARMS: usize = 65_536;
const MAX_SNAPSHOT_STRING_BYTES: usize = 16 * 1024;

/// Port for selecting one candidate under the write budget.
pub(crate) trait BanditSelector {
    /// Chooses the next candidate, or none when budget/uncertainty says wait.
    fn select<'a>(
        &mut self,
        candidates: &'a [Candidate],
        budget: &WriteBudget,
    ) -> Option<&'a Candidate>;
}

/// Errors from an invalid observed measurement.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) enum BanditUpdateError {
    /// Reward, uncertainty, or cost was NaN/infinite or negative where not
    /// meaningful.
    InvalidMeasurement,
    /// The candidate's state would exceed the configured memory bound.
    MemoryBound,
}

/// Errors from decoding or applying durable selector state.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) enum SnapshotError {
    /// The byte stream has an unknown header/version or malformed value.
    InvalidEncoding,
    /// The snapshot contains more state than the selector can retain.
    MemoryBound,
}

/// A durable copy of one candidate's measured arm state.
#[derive(Clone, Debug, PartialEq)]
pub(crate) struct ArmSnapshot {
    key: CandidateKey,
    pulls: u64,
    mean_reward: f64,
    mean_uncertainty: f64,
    mean_cost: f64,
    last_selected_tick: u64,
}

/// Durable selector state. The vector is encoded in candidate-key order.
#[derive(Clone, Debug, PartialEq)]
pub(crate) struct BanditSnapshot {
    tick: u64,
    total_pulls: u64,
    arms: Vec<ArmSnapshot>,
}

impl BanditSnapshot {
    /// Encodes a versioned, deterministic, bounded binary representation.
    #[must_use]
    pub(crate) fn encode(&self) -> Vec<u8> {
        let mut out = Vec::with_capacity(32);
        out.extend_from_slice(SNAPSHOT_MAGIC);
        out.push(SNAPSHOT_VERSION);
        put_u64(&mut out, self.tick);
        put_u64(&mut out, self.total_pulls);
        // Saturating: the arm table is memory-bounded and the length is
        // pre-clamped; the fallback keeps snapshot encoding total.
        put_u32(
            &mut out,
            u32::try_from(self.arms.len().min(u32::MAX as usize)).unwrap_or(u32::MAX),
        );
        for arm in self.arms.iter().take(u32::MAX as usize) {
            arm.key.encode_into(&mut out);
            put_u64(&mut out, arm.pulls);
            put_u64(&mut out, arm.mean_reward.to_bits());
            put_u64(&mut out, arm.mean_uncertainty.to_bits());
            put_u64(&mut out, arm.mean_cost.to_bits());
            put_u64(&mut out, arm.last_selected_tick);
        }
        out
    }

    /// Decodes a snapshot without allocating from attacker-controlled lengths.
    pub(crate) fn decode(bytes: &[u8]) -> Result<Self, SnapshotError> {
        let mut reader = Reader::new(bytes);
        if reader.take(4)? != SNAPSHOT_MAGIC || reader.u8()? != SNAPSHOT_VERSION {
            return Err(SnapshotError::InvalidEncoding);
        }
        let tick = reader.u64()?;
        let total_pulls = reader.u64()?;
        let count = usize::try_from(reader.u32()?).map_err(|_| SnapshotError::InvalidEncoding)?;
        if count > MAX_SNAPSHOT_ARMS {
            return Err(SnapshotError::MemoryBound);
        }
        let mut arms: Vec<ArmSnapshot> = Vec::with_capacity(count);
        for _ in 0..count {
            let key = CandidateKey::decode_from(&mut reader)?;
            let arm = ArmSnapshot {
                key,
                pulls: reader.u64()?,
                mean_reward: f64::from_bits(reader.u64()?),
                mean_uncertainty: f64::from_bits(reader.u64()?),
                mean_cost: f64::from_bits(reader.u64()?),
                last_selected_tick: reader.u64()?,
            };
            if !arm.valid() || arms.last().is_some_and(|last| last.key >= arm.key) {
                return Err(SnapshotError::InvalidEncoding);
            }
            arms.push(arm);
        }
        if !reader.is_empty() {
            return Err(SnapshotError::InvalidEncoding);
        }
        Ok(Self { tick, total_pulls, arms })
    }
}

/// Measured statistics for one candidate.
#[derive(Clone, Copy, Debug, PartialEq)]
pub(crate) struct CandidateStats {
    /// Number of observed activation windows.
    pub(crate) pulls: u64,
    /// Mean measured work saved.
    pub(crate) mean_reward: f64,
    /// Mean supplied uncertainty.
    pub(crate) mean_uncertainty: f64,
    /// Mean measured maintenance cost.
    pub(crate) mean_cost: f64,
}

#[derive(Clone, Debug, Eq, Ord, PartialEq, PartialOrd)]
struct CandidateKey {
    collection: String,
    fields: Vec<String>,
    kind: CandidateKind,
    source: CandidateSource,
    needs_descriptor: bool,
}

impl CandidateKey {
    fn of(candidate: &Candidate) -> Self {
        Self {
            collection: candidate.collection().to_owned(),
            fields: candidate.fields().into_iter().map(str::to_owned).collect(),
            kind: candidate.kind(),
            source: candidate.source(),
            needs_descriptor: candidate.needs_descriptor(),
        }
    }

    fn encode_into(&self, out: &mut Vec<u8>) {
        put_string(out, &self.collection);
        // Saturating: field lists are bounded by validation and pre-clamped;
        // the fallback keeps snapshot encoding total.
        put_u32(out, u32::try_from(self.fields.len().min(u32::MAX as usize)).unwrap_or(u32::MAX));
        for field in self.fields.iter().take(u32::MAX as usize) {
            put_string(out, field);
        }
        out.push(match self.kind {
            CandidateKind::BTree => 0,
            CandidateKind::Fts => 1,
        });
        out.push(match self.source {
            CandidateSource::Workload => 0,
            CandidateSource::AccessRule => 1,
        });
        out.push(u8::from(self.needs_descriptor));
    }

    fn decode_from(reader: &mut Reader<'_>) -> Result<Self, SnapshotError> {
        let collection = reader.string()?;
        let count = usize::try_from(reader.u32()?).map_err(|_| SnapshotError::InvalidEncoding)?;
        if count > 1_024 {
            return Err(SnapshotError::MemoryBound);
        }
        let mut fields = Vec::with_capacity(count);
        for _ in 0..count {
            fields.push(reader.string()?);
        }
        let kind = match reader.u8()? {
            0 => CandidateKind::BTree,
            1 => CandidateKind::Fts,
            _ => return Err(SnapshotError::InvalidEncoding),
        };
        let source = match reader.u8()? {
            0 => CandidateSource::Workload,
            1 => CandidateSource::AccessRule,
            _ => return Err(SnapshotError::InvalidEncoding),
        };
        let needs_descriptor = match reader.u8()? {
            0 => false,
            1 => true,
            _ => return Err(SnapshotError::InvalidEncoding),
        };
        Ok(Self { collection, fields, kind, source, needs_descriptor })
    }

    fn memory_bytes(&self) -> usize {
        ARM_FIXED_BYTES
            .saturating_add(self.collection.len())
            .saturating_add(self.fields.iter().map(String::len).sum::<usize>())
    }
}

#[derive(Clone, Copy, Debug, PartialEq)]
struct ArmState {
    pulls: u64,
    mean_reward: f64,
    mean_uncertainty: f64,
    mean_cost: f64,
    last_selected_tick: u64,
}

impl ArmState {
    fn new() -> Self {
        Self {
            pulls: 0,
            mean_reward: 0.0,
            mean_uncertainty: 0.0,
            mean_cost: COLD_START_COST,
            last_selected_tick: 0,
        }
    }

    fn update(&mut self, reward: MeasuredReward, cost: f64) {
        self.pulls = self.pulls.saturating_add(1);
        if self.pulls == 0 {
            return;
        }
        // Saturating: pull counts grow by one per observed window and stay
        // far below u32 range in practice; the fallback keeps the running
        // mean total.
        let n = f64::from(u32::try_from(self.pulls).unwrap_or(u32::MAX));
        self.mean_reward += (reward.work_saved - self.mean_reward) / n;
        self.mean_uncertainty += (reward.uncertainty - self.mean_uncertainty) / n;
        self.mean_cost += (cost - self.mean_cost) / n;
        self.mean_reward = finite_or_zero(self.mean_reward);
        self.mean_uncertainty = finite_nonnegative(self.mean_uncertainty);
        self.mean_cost = finite_nonnegative(self.mean_cost);
    }

    fn snapshot(self, key: CandidateKey) -> ArmSnapshot {
        ArmSnapshot {
            key,
            pulls: self.pulls,
            mean_reward: self.mean_reward,
            mean_uncertainty: self.mean_uncertainty,
            mean_cost: self.mean_cost,
            last_selected_tick: self.last_selected_tick,
        }
    }
}

/// A C²UCB selector with bounded state and deterministic tie-breaking.
#[derive(Debug)]
pub(crate) struct C2UcbSelector {
    arms: BTreeMap<CandidateKey, ArmState>,
    tick: u64,
    total_pulls: u64,
    exploration: f64,
    max_memory_bytes: usize,
    memory_bytes: usize,
}

/// Short name used by callers that treat the selector as the C²UCB oracle.
pub(crate) type C2Ucb = C2UcbSelector;

impl Default for C2UcbSelector {
    fn default() -> Self {
        Self::new()
    }
}

impl C2UcbSelector {
    /// Creates a selector with ADR 0045's stable default exploration weight.
    #[must_use]
    pub(crate) fn new() -> Self {
        Self::with_config(1.0, DEFAULT_MAX_MEMORY_BYTES)
    }

    /// Creates a selector with an injected exploration weight and memory cap.
    #[must_use]
    pub(crate) fn with_config(exploration: f64, max_memory_bytes: usize) -> Self {
        Self {
            arms: BTreeMap::new(),
            tick: 0,
            total_pulls: 0,
            exploration: finite_nonnegative(exploration),
            max_memory_bytes,
            memory_bytes: 0,
        }
    }

    /// Returns the number of retained arm records.
    #[must_use]
    pub(crate) fn len(&self) -> usize {
        self.arms.len()
    }

    /// Returns currently accounted selector memory.
    #[must_use]
    pub(crate) const fn memory_bytes(&self) -> usize {
        self.memory_bytes
    }

    /// Records one measured reward/cost observation.
    pub(crate) fn update(
        &mut self,
        candidate: &Candidate,
        reward: MeasuredReward,
        maintenance_cost: f64,
    ) -> Result<(), BanditUpdateError> {
        if !reward.work_saved.is_finite()
            || !reward.uncertainty.is_finite()
            || reward.uncertainty < 0.0
            || !maintenance_cost.is_finite()
            || maintenance_cost < 0.0
        {
            return Err(BanditUpdateError::InvalidMeasurement);
        }
        let key = CandidateKey::of(candidate);
        if !self.arms.contains_key(&key) {
            self.ensure_arm(&key)?;
        }
        let arm = self.arms.get_mut(&key).expect("inserted arm exists");
        arm.update(reward, maintenance_cost);
        self.total_pulls = self.total_pulls.saturating_add(1);
        Ok(())
    }

    /// Alias emphasizing that updates are observations from execution.
    pub(crate) fn observe(
        &mut self,
        candidate: &Candidate,
        reward: MeasuredReward,
        maintenance_cost: f64,
    ) -> Result<(), BanditUpdateError> {
        self.update(candidate, reward, maintenance_cost)
    }

    /// Returns measured statistics for a candidate, if retained.
    #[must_use]
    pub(crate) fn stats(&self, candidate: &Candidate) -> Option<CandidateStats> {
        self.arms.get(&CandidateKey::of(candidate)).map(|arm| CandidateStats {
            pulls: arm.pulls,
            mean_reward: arm.mean_reward,
            mean_uncertainty: arm.mean_uncertainty,
            mean_cost: arm.mean_cost,
        })
    }

    /// Takes a deterministic durable snapshot of this selector.
    #[must_use]
    pub(crate) fn snapshot(&self) -> BanditSnapshot {
        BanditSnapshot {
            tick: self.tick,
            total_pulls: self.total_pulls,
            arms: self.arms.iter().map(|(key, arm)| arm.snapshot(key.clone())).collect(),
        }
    }

    /// Encodes this selector's durable state.
    #[must_use]
    pub(crate) fn encode_snapshot(&self) -> Vec<u8> {
        self.snapshot().encode()
    }

    /// Restores a previously decoded snapshot, atomically replacing state.
    pub(crate) fn restore(&mut self, snapshot: BanditSnapshot) -> Result<(), SnapshotError> {
        let mut arms = BTreeMap::new();
        let mut memory = 0_usize;
        for record in snapshot.arms {
            if !record.valid() {
                return Err(SnapshotError::InvalidEncoding);
            }
            memory = memory.saturating_add(record.key.memory_bytes());
            let key = record.key.clone();
            if arms.insert(key, record.into_state()).is_some() {
                return Err(SnapshotError::InvalidEncoding);
            }
        }
        if memory > self.max_memory_bytes {
            return Err(SnapshotError::MemoryBound);
        }
        self.arms = arms;
        self.tick = snapshot.tick;
        self.total_pulls = snapshot.total_pulls;
        self.memory_bytes = memory;
        Ok(())
    }

    /// Decodes and restores durable state, leaving this selector unchanged on
    /// failure.
    pub(crate) fn restore_encoded(&mut self, bytes: &[u8]) -> Result<(), SnapshotError> {
        let snapshot = BanditSnapshot::decode(bytes)?;
        self.restore(snapshot)
    }

    fn ensure_arm(&mut self, key: &CandidateKey) -> Result<(), BanditUpdateError> {
        if self.memory_bytes.saturating_add(key.memory_bytes()) > self.max_memory_bytes {
            return Err(BanditUpdateError::MemoryBound);
        }
        self.memory_bytes = self.memory_bytes.saturating_add(key.memory_bytes());
        self.arms.insert(key.clone(), ArmState::new());
        Ok(())
    }

    fn score(&self, arm: ArmState) -> f64 {
        if arm.pulls == 0 {
            return f64::INFINITY;
        }
        // Saturating: pull counts grow by one per observed window and stay far
        // below u32 range in practice; the fallback keeps confidence finite.
        let trials =
            f64::from(u32::try_from(self.total_pulls.saturating_add(2)).unwrap_or(u32::MAX));
        let pulls = f64::from(u32::try_from(arm.pulls).unwrap_or(u32::MAX));
        let confidence = (trials.ln() / pulls).sqrt();
        arm.mean_reward + self.exploration * confidence + arm.mean_uncertainty
    }
}

impl BanditSelector for C2UcbSelector {
    fn select<'a>(
        &mut self,
        candidates: &'a [Candidate],
        budget: &WriteBudget,
    ) -> Option<&'a Candidate> {
        if candidates.is_empty() || !budget.allows() {
            return None;
        }
        self.tick = self.tick.saturating_add(1);
        let mut selected: Option<(usize, CandidateKey, f64, u64)> = None;
        for (index, candidate) in candidates.iter().enumerate() {
            let key = CandidateKey::of(candidate);
            // Register candidates as they enter the oracle so cold-start arms
            // are bounded too; an unbounded input slice must not grow state.
            if !self.arms.contains_key(&key) && self.ensure_arm(&key).is_err() {
                continue;
            }
            let arm = self.arms.get(&key).copied().unwrap_or_else(ArmState::new);
            let cost = if arm.pulls == 0 { COLD_START_COST } else { arm.mean_cost };
            if !budget.would_allow(cost) {
                continue;
            }
            let age = self.tick.saturating_sub(arm.last_selected_tick);
            let score = self.score(arm);
            let better = match selected.as_ref() {
                None => true,
                Some((_, best_key, best_score, best_age)) => {
                    if age >= STARVATION_TICKS && *best_age < STARVATION_TICKS {
                        true
                    } else if (age >= STARVATION_TICKS) == (*best_age >= STARVATION_TICKS)
                        && age > *best_age
                    {
                        true
                    } else if score > *best_score {
                        true
                    } else {
                        // Epsilon tie-break: gaps within float noise count as ties so
                        // replay stays on deterministic key order. Infinities tie by
                        // bits so cold-start arms still rotate; NaN is excluded
                        // upstream by finitized means and the cold-start infinity.
                        (score.to_bits() == best_score.to_bits()
                            || (score - *best_score).abs() < SCORE_TIE_EPSILON)
                            && key < *best_key
                    }
                }
            };
            if better {
                selected = Some((index, key, score, age));
            }
        }
        let (index, key, _, _) = selected?;
        if let Some(arm) = self.arms.get_mut(&key) {
            arm.last_selected_tick = self.tick;
        }
        candidates.get(index)
    }
}

fn finite_nonnegative(value: f64) -> f64 {
    finite_nonnegative_option(value).unwrap_or(0.0)
}

fn finite_nonnegative_option(value: f64) -> Option<f64> {
    value.is_finite().then_some(value.max(0.0))
}

fn finite_or_zero(value: f64) -> f64 {
    value.is_finite().then_some(value).unwrap_or(0.0)
}

fn put_u32(out: &mut Vec<u8>, value: u32) {
    out.extend_from_slice(&value.to_le_bytes());
}

fn put_u64(out: &mut Vec<u8>, value: u64) {
    out.extend_from_slice(&value.to_le_bytes());
}

fn put_string(out: &mut Vec<u8>, value: &str) {
    let bytes = value.as_bytes();
    let length = bytes.len().min(u32::MAX as usize);
    // Saturating: pre-clamped above; the fallback keeps encoding total.
    put_u32(out, u32::try_from(length).unwrap_or(u32::MAX));
    out.extend_from_slice(&bytes[..length]);
}

struct Reader<'a> {
    bytes: &'a [u8],
    position: usize,
}

impl<'a> Reader<'a> {
    const fn new(bytes: &'a [u8]) -> Self {
        Self { bytes, position: 0 }
    }

    fn take(&mut self, length: usize) -> Result<&'a [u8], SnapshotError> {
        let end = self.position.checked_add(length).ok_or(SnapshotError::InvalidEncoding)?;
        let value = self.bytes.get(self.position..end).ok_or(SnapshotError::InvalidEncoding)?;
        self.position = end;
        Ok(value)
    }

    fn u8(&mut self) -> Result<u8, SnapshotError> {
        Ok(*self.take(1)?.first().ok_or(SnapshotError::InvalidEncoding)?)
    }

    fn u32(&mut self) -> Result<u32, SnapshotError> {
        Ok(u32::from_le_bytes(
            self.take(4)?.try_into().map_err(|_| SnapshotError::InvalidEncoding)?,
        ))
    }

    fn u64(&mut self) -> Result<u64, SnapshotError> {
        Ok(u64::from_le_bytes(
            self.take(8)?.try_into().map_err(|_| SnapshotError::InvalidEncoding)?,
        ))
    }

    fn string(&mut self) -> Result<String, SnapshotError> {
        let length = usize::try_from(self.u32()?).map_err(|_| SnapshotError::InvalidEncoding)?;
        if length > MAX_SNAPSHOT_STRING_BYTES {
            return Err(SnapshotError::MemoryBound);
        }
        String::from_utf8(self.take(length)?.to_vec()).map_err(|_| SnapshotError::InvalidEncoding)
    }

    const fn is_empty(&self) -> bool {
        self.position == self.bytes.len()
    }
}

impl ArmSnapshot {
    fn valid(&self) -> bool {
        self.mean_reward.is_finite()
            && self.mean_uncertainty.is_finite()
            && self.mean_uncertainty >= 0.0
            && self.mean_cost.is_finite()
            && self.mean_cost >= 0.0
    }

    fn into_state(self) -> ArmState {
        ArmState {
            pulls: self.pulls,
            mean_reward: self.mean_reward,
            mean_uncertainty: self.mean_uncertainty,
            mean_cost: self.mean_cost,
            last_selected_tick: self.last_selected_tick,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::query::optimizer::auto_index::{
        CompressConfig, LogicalCost, PredicateKind, Template, TemplatePredicate, TimedObservation,
        candidates_for_template, tuned_workload,
    };

    fn candidates() -> Vec<Candidate> {
        let now = std::time::Instant::now();
        let template = Template::try_new(
            "users",
            vec![TemplatePredicate::try_new("tenant", PredicateKind::Equality).unwrap()],
            Vec::new(),
            Some(vec!["name".to_owned()]),
        )
        .unwrap();
        let observations =
            [TimedObservation::new(template, LogicalCost::new(10, 90), 1, None, now)];
        let workload = tuned_workload(&observations, now, CompressConfig::default());
        let weighted = workload
            .get("users")
            .and_then(|templates| templates.first())
            .expect("observed template is retained");
        candidates_for_template(weighted, &BTreeMap::new(), &std::collections::BTreeSet::new())
    }

    #[test]
    fn cold_start_then_converges_to_high_reward() {
        let mut selector = C2UcbSelector::new();
        let candidates = candidates();
        let mut budget = WriteBudget::new(100.0);
        let first = selector.select(&candidates, &budget).unwrap().clone();
        selector
            .update(&first, MeasuredReward { work_saved: 100.0, uncertainty: 0.0 }, 1.0)
            .unwrap();
        let second = selector.select(&candidates, &budget).unwrap().clone();
        selector
            .update(&second, MeasuredReward { work_saved: 1.0, uncertainty: 0.0 }, 1.0)
            .unwrap();
        for _ in 0..100 {
            let selected = selector.select(&candidates, &budget).unwrap().clone();
            let reward = if selected == first { 100.0 } else { 1.0 };
            selector
                .update(&selected, MeasuredReward { work_saved: reward, uncertainty: 0.0 }, 1.0)
                .unwrap();
        }
        assert_eq!(selector.select(&candidates, &budget).unwrap(), &first);
        budget.account(200.0);
        assert!(selector.select(&candidates, &budget).is_none());
    }

    #[test]
    fn budget_refusal_and_overflow_are_fail_closed() {
        let mut budget = WriteBudget::new(1.0);
        assert!(budget.try_account(2.0));
        assert!(!budget.try_account(1.0));
        budget.account(f64::MAX);
        assert!(!budget.allows());
        let candidate = candidates().remove(0);
        let mut selector = C2UcbSelector::new();
        assert_eq!(
            selector.update(
                &candidate,
                MeasuredReward { work_saved: f64::NAN, uncertainty: 0.0 },
                1.0
            ),
            Err(BanditUpdateError::InvalidMeasurement)
        );
    }

    #[test]
    fn snapshot_restore_replays_identically() {
        let candidates = candidates();
        let mut first = C2UcbSelector::with_config(0.75, 4096);
        first
            .update(&candidates[0], MeasuredReward { work_saved: 4.0, uncertainty: 0.2 }, 2.0)
            .unwrap();
        let encoded = first.encode_snapshot();
        let mut second = C2UcbSelector::with_config(0.75, 4096);
        second.restore_encoded(&encoded).unwrap();
        assert_eq!(first.snapshot(), second.snapshot());
        let budget = WriteBudget::new(10.0);
        assert_eq!(first.select(&candidates, &budget), second.select(&candidates, &budget));
    }

    #[test]
    fn deterministic_ties_and_starvation_resistance() {
        let candidates = candidates();
        let mut selector = C2UcbSelector::with_config(0.0, 4096);
        let budget = WriteBudget::new(10.0);
        let first = selector.select(&candidates, &budget).unwrap().clone();
        selector.update(&first, MeasuredReward { work_saved: 1.0, uncertainty: 0.0 }, 1.0).unwrap();
        let second = selector.select(&candidates, &budget).unwrap().clone();
        assert_ne!(first, second);
        selector
            .update(&second, MeasuredReward { work_saved: 1.0, uncertainty: 0.0 }, 1.0)
            .unwrap();
        for _ in 0..STARVATION_TICKS {
            let chosen = selector.select(&candidates, &budget).unwrap().clone();
            selector
                .update(&chosen, MeasuredReward { work_saved: 1.0, uncertainty: 0.0 }, 1.0)
                .unwrap();
        }
        assert!(selector.len() >= 2);
    }
}
