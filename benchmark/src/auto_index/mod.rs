//! Durable automatic-index coordinator (ADR 0045 / D-31).
//!
//! This module is the production boundary between the index advisor and the
//! `SQLite` engine.  It deliberately contains no process-local authority: a
//! coordinator can only move an index through its lifecycle when the caller
//! supplies all required durable hooks.  In particular, an absent journal,
//! publication transaction, planner visibility hook, or logical-cost hook is
//! reported as [`CoordinatorError::MissingHook`] and the transition is
//! refused.
//!
//! The query optimizer may use the value types in this module to derive a
//! workload and recommendation.  The storage adapter owns bytes, `SQLite`
//! transactions, and physical indexes.  The adapter methods documented on
//! [`StorageAdapter`] are the exact integration contract; they are kept
//! small so a storage failure cannot be mistaken for a successful lifecycle
//! transition.

use crate::storage::StoragePort;
use std::collections::{BTreeMap, BTreeSet, VecDeque};

/// Maximum templates retained by one database observer.
pub const OBSERVATION_CAPACITY: usize = 4096;
/// Maximum templates in the compressed 95% workload.
pub const COMPRESSED_CAPACITY: usize = 256;
/// Maximum candidate indexes retained per collection.
pub const CANDIDATE_CAPACITY: usize = 32;
/// Maximum visible indexes per collection.
pub const MAX_VISIBLE_INDEXES: usize = 12;
/// Maintenance budget relative to measured base write work.
pub const WRITE_BUDGET_MULTIPLE: f64 = 2.0;
/// Logical-work regression guard.
pub const REGRESSION_FRACTION: f64 = 0.05;
/// Minimum shadow samples per side for common templates.
pub const MIN_SHADOW_SAMPLES: usize = 20;
/// Maximum samples retained and evaluated per side of a shadow comparison.
pub const MAX_SHADOW_SAMPLES: usize = 4096;
/// Minimum improvement required for activation.
pub const MIN_IMPROVEMENT_FRACTION: f64 = 0.20;
/// Retention before a hidden physical index is dropped.
pub const DROP_GRACE_MILLIS: u64 = 24 * 60 * 60 * 1000;
/// Retention before an unused index is hidden.
pub const RETIREMENT_MILLIS: u64 = 7 * 24 * 60 * 60 * 1000;

/// Which production hook a coordinator operation needs.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum HookKind {
    /// Durable lifecycle journal load and append.
    Journal,
    /// Atomic write-transaction catch-up and extraction maintenance.
    PublicationTransaction,
    /// `SQLite` planner visibility and plan-pin installation.
    Planner,
    /// `SQLite` logical pages/rows execution counters.
    LogicalCost,
    /// Bounded sequence-ordered backfill.
    Backfill,
}

/// Presence of the production hooks.  The engine passes this value at open;
/// a false bit is an explicit configuration error, never a permission to
/// fall back to an in-memory coordinator.
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
#[allow(
    clippy::struct_excessive_bools,
    reason = "five explicit hook-presence bits; a bitmask struct keeps open-time validation total"
)]
pub struct HookAvailability {
    /// Lifecycle journal is durable and replayable.
    pub journal: bool,
    /// Write publication transaction maintains descriptors and indexes.
    pub publication_transaction: bool,
    /// Planner can hide/show indexes and install pins.
    pub planner: bool,
    /// Statement counters provide pages and rows.
    pub logical_cost: bool,
    /// Backfill reads contiguous journal sequence ranges.
    pub backfill: bool,
}

impl HookAvailability {
    /// Validates that all D-31 hooks are installed.
    ///
    /// # Errors
    ///
    /// Returns [`CoordinatorError::MissingHook`] for the first absent hook.
    pub fn validate(self) -> Result<(), CoordinatorError> {
        if !self.journal {
            return Err(CoordinatorError::MissingHook(HookKind::Journal));
        }
        if !self.publication_transaction {
            return Err(CoordinatorError::MissingHook(HookKind::PublicationTransaction));
        }
        if !self.planner {
            return Err(CoordinatorError::MissingHook(HookKind::Planner));
        }
        if !self.logical_cost {
            return Err(CoordinatorError::MissingHook(HookKind::LogicalCost));
        }
        if !self.backfill {
            return Err(CoordinatorError::MissingHook(HookKind::Backfill));
        }
        Ok(())
    }
}

/// A typed error from an unavailable or rejecting integration hook.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum HookError {
    /// The requested operation is not installed in the member.
    Unavailable,
    /// The hook rejected the operation without changing durable state.
    Refused,
    /// The hook supplied a sequence that violates the coordinator contract.
    SequenceConflict,
}

/// Errors from the durable coordinator.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum CoordinatorError {
    /// A required hook was absent.  No state transition was attempted.
    MissingHook(HookKind),
    /// A hook returned a typed failure.
    Hook(HookError),
    /// A lifecycle transition was not legal for the current state.
    InvalidTransition,
    /// A watermark moved backwards, skipped a sequence, or exceeded target.
    InvalidWatermark,
    /// The candidate would violate the collection write budget or cap.
    WriteBudgetExceeded,
    /// Shadow evidence was malformed, capped, or insufficient.
    InsufficientEvidence,
    /// A journal event was duplicated with different content or reordered.
    JournalConflict,
}

impl From<HookError> for CoordinatorError {
    fn from(error: HookError) -> Self {
        Self::Hook(error)
    }
}

/// Logical work measured by `SQLite`'s statement counters.
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct LogicalCost {
    /// `SQLite` pages read.
    pub pages_read: u64,
    /// `SQLite` rows examined.
    pub rows_examined: u64,
}

impl LogicalCost {
    /// Creates a logical-cost sample.
    #[must_use]
    pub const fn new(pages_read: u64, rows_examined: u64) -> Self {
        Self { pages_read, rows_examined }
    }
    /// Returns pages plus rows, saturating on overflow.
    #[must_use]
    pub const fn total(self) -> u64 {
        self.pages_read.saturating_add(self.rows_examined)
    }
}

/// One normalized predicate in a SQL template.
#[derive(Clone, Debug, Eq, Ord, PartialEq, PartialOrd)]
pub struct Predicate {
    /// Extracted field name.
    pub field: String,
    /// Whether this is equality, range, or full-text matching.
    pub kind: PredicateKind,
}

/// SQL predicate classes understood by candidate derivation.
#[derive(Clone, Copy, Debug, Eq, Ord, PartialEq, PartialOrd)]
pub enum PredicateKind {
    /// `=`, `IN`, `IS`, or equivalent equality predicate.
    Equality,
    /// One range predicate (`<`, `<=`, `>`, `>=`, `BETWEEN`).
    Range,
    /// `SQLite` FTS5 `MATCH`.
    FullText,
}

/// A literal-free normalized SQL workload template.
#[derive(Clone, Debug, Eq, Ord, PartialEq, PartialOrd)]
pub struct Template {
    /// Collection/table name.
    pub collection: String,
    /// Predicates in canonical SQL order.
    pub predicates: Vec<Predicate>,
    /// `ORDER BY` fields in canonical order.
    pub ordering: Vec<String>,
    /// Projected extracted fields, sorted for identity; `None` means body.
    pub projection: Option<Vec<String>>,
}

impl Template {
    /// Returns whether the template includes FTS5 matching.
    #[must_use]
    pub fn has_full_text(&self) -> bool {
        self.predicates.iter().any(|p| p.kind == PredicateKind::FullText)
    }
}

/// A bounded execution observation.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct Observation {
    /// Tenant whose validated read produced this observation.
    pub tenant: String,
    /// Normalized SQL template.
    pub template: Template,
    /// Logical `SQLite` work.
    pub cost: LogicalCost,
    /// Rows returned.
    pub rows_returned: u64,
    /// Index used, if any.
    pub index: Option<String>,
    /// Durable observation timestamp in milliseconds.
    pub at_millis: u64,
}

/// A bounded workload observer.  Values are copied only into fixed sketches;
/// document bodies never enter this structure.
#[derive(Clone, Debug)]
pub struct WorkloadSketch {
    ring: VecDeque<Observation>,
    columns: BTreeMap<(String, String), ColumnSketch>,
    capacity: usize,
}

impl Default for WorkloadSketch {
    fn default() -> Self {
        Self {
            ring: VecDeque::with_capacity(OBSERVATION_CAPACITY),
            columns: BTreeMap::new(),
            capacity: OBSERVATION_CAPACITY,
        }
    }
}

impl WorkloadSketch {
    /// Creates a bounded observer, clamping capacity to the engine bound.
    #[must_use]
    pub fn new(capacity: usize) -> Self {
        Self { capacity: capacity.clamp(1, OBSERVATION_CAPACITY), ..Self::default() }
    }
    /// Records an execution and evicts the oldest observation at the bound.
    pub fn observe(&mut self, observation: Observation) {
        self.ring.push_back(observation);
        while self.ring.len() > self.capacity {
            self.ring.pop_front();
        }
    }
    /// Adds an already-extracted value to the per-column sketches.
    pub fn observe_value(&mut self, collection: &str, field: &str, value: &[u8]) {
        self.columns.entry((collection.to_owned(), field.to_owned())).or_default().observe(value);
    }
    /// Returns retained observations in chronological order.
    pub fn observations(&self) -> impl Iterator<Item = &Observation> {
        self.ring.iter()
    }
    /// Returns a column sketch, if one has observed values.
    #[must_use]
    pub fn column(&self, collection: &str, field: &str) -> Option<&ColumnSketch> {
        self.columns.get(&(collection.to_owned(), field.to_owned()))
    }
    /// Compresses observations into the heaviest templates covering `share`.
    #[must_use]
    #[allow(clippy::cast_precision_loss, reason = "compression weight; rounding is noise")]
    pub fn compress(
        &self,
        tenant: &str,
        now_millis: u64,
        window_millis: u64,
        share: f64,
    ) -> Vec<WeightedTemplate> {
        if window_millis == 0 || !(0.0 < share && share <= 1.0) {
            return Vec::new();
        }
        let mut weights: BTreeMap<Template, (u64, u64)> = BTreeMap::new();
        for observation in &self.ring {
            if observation.tenant != tenant
                || now_millis.saturating_sub(observation.at_millis) > window_millis
            {
                continue;
            }
            let row = weights.entry(observation.template.clone()).or_default();
            row.0 = row.0.saturating_add(1);
            row.1 = row.1.saturating_add(observation.cost.total());
        }
        let mut by_collection: BTreeMap<String, Vec<WeightedTemplate>> = BTreeMap::new();
        for (template, (frequency, work)) in weights {
            // Saturating: frequencies are bounded by `OBSERVATION_CAPACITY`;
            // the fallback keeps the ranking weight total for oversized inputs.
            let frequency_f = f64::from(u32::try_from(frequency).unwrap_or(u32::MAX));
            let divisor = f64::from(u32::try_from(frequency.max(1)).unwrap_or(u32::MAX));
            let weight = frequency_f * (work as f64 / divisor);
            by_collection.entry(template.collection.clone()).or_default().push(WeightedTemplate {
                template,
                frequency,
                weight,
            });
        }
        let mut result = Vec::new();
        for (_, mut entries) in by_collection {
            entries.sort_by(|a, b| {
                b.weight.total_cmp(&a.weight).then_with(|| a.template.cmp(&b.template))
            });
            let total: f64 = entries.iter().map(|e| e.weight).sum();
            let target = total * share;
            let mut covered = 0.0;
            for item in entries {
                if result.len() >= COMPRESSED_CAPACITY {
                    break;
                }
                covered += item.weight;
                result.push(item);
                // Allow the exact 95% boundary despite decimal rounding of
                // the frequency×cost product.
                if covered + 1e-12 * total >= target {
                    break;
                }
            }
        }
        result
    }
}

/// A compressed template and its frequency/cost weight.
#[derive(Clone, Debug, PartialEq)]
pub struct WeightedTemplate {
    /// Normalized template.
    pub template: Template,
    /// Number of executions in the window.
    pub frequency: u64,
    /// Frequency multiplied by average logical work.
    pub weight: f64,
}

/// Bounded distinct-count and histogram sketch for one extracted field.
#[derive(Clone, Debug)]
pub struct ColumnSketch {
    registers: [u8; 64],
    histogram: BTreeMap<Vec<u8>, u64>,
    total: u64,
}

impl Default for ColumnSketch {
    fn default() -> Self {
        Self { registers: [0; 64], histogram: BTreeMap::new(), total: 0 }
    }
}

impl ColumnSketch {
    /// Observes a value without retaining it in the distinct sketch.
    pub fn observe(&mut self, value: &[u8]) {
        let hash = fnv(value);
        // Saturating: the register table is masked below, so the fallback
        // keeps index derivation total on targets with 32-bit pointers.
        let slot = usize::try_from(hash).unwrap_or(usize::MAX) & 63;
        let rank = (hash >> 6).leading_zeros().saturating_sub(6).saturating_add(1).min(64) as u8;
        self.registers[slot] = self.registers[slot].max(rank.max(1));
        self.total = self.total.saturating_add(1);
        if value.len() <= 256 {
            let next = self.histogram.get(value).copied().unwrap_or(0).saturating_add(1);
            self.histogram.insert(value.to_vec(), next);
            while self.histogram.len() > 128 {
                if let Some(key) =
                    self.histogram.iter().min_by_key(|(_, count)| **count).map(|(k, _)| k.clone())
                {
                    self.histogram.remove(&key);
                } else {
                    break;
                }
            }
        }
    }
    /// Returns an approximate distinct count.
    #[must_use]
    pub fn distinct(&self) -> f64 {
        // Saturating: the register table holds 64 entries; the fallback keeps
        // the estimator total for oversized inputs.
        let mut zero_count = 0_u32;
        for register in self.registers {
            zero_count += u32::from(register == 0);
        }
        let zero = f64::from(zero_count);
        let harmonic: f64 = self.registers.iter().map(|r| 2.0_f64.powi(-i32::from(*r))).sum();
        if harmonic == 0.0 {
            return f64::INFINITY;
        }
        let raw = 0.673 * 4096.0 / harmonic;
        if raw <= 160.0 && zero > 0.0 { 64.0 * (64.0 / zero).ln() } else { raw }
    }
    /// Number of values observed.
    #[must_use]
    pub const fn total(&self) -> u64 {
        self.total
    }
}

fn fnv(bytes: &[u8]) -> u64 {
    let mut hash = 0xcbf2_9ce4_8422_2325_u64;
    for byte in bytes {
        hash = (hash ^ u64::from(*byte)).wrapping_mul(0x0100_0000_01b3);
    }
    hash
}

/// Candidate index kind.
#[derive(Clone, Copy, Debug, Eq, Ord, PartialEq, PartialOrd)]
pub enum CandidateKind {
    /// `SQLite` B-tree index.
    BTree,
    /// `SQLite` FTS5 virtual table.
    Fts,
}

/// One SQL-derived index candidate.
#[derive(Clone, Debug, Eq, Ord, PartialEq, PartialOrd)]
pub struct Candidate {
    /// Collection/table.
    pub collection: String,
    /// Ordered extracted fields.
    pub fields: Vec<String>,
    /// B-tree or FTS5.
    pub kind: CandidateKind,
    /// Whether a missing descriptor must be journaled first.
    pub needs_descriptor: bool,
    /// Estimated maintenance cost per write.
    pub maintenance_cost: u64,
}

/// Derives equality → range → ordering → covering candidates, including FTS5.
#[must_use]
pub fn derive_candidates(template: &Template, extracted: &BTreeSet<String>) -> Vec<Candidate> {
    if template.has_full_text() {
        return template
            .predicates
            .iter()
            .find(|p| p.kind == PredicateKind::FullText)
            .filter(|p| extracted.contains(&p.field))
            .map(|p| {
                vec![Candidate {
                    collection: template.collection.clone(),
                    fields: vec![p.field.clone()],
                    kind: CandidateKind::Fts,
                    needs_descriptor: false,
                    maintenance_cost: 1,
                }]
            })
            .unwrap_or_default();
    }
    let mut fields = Vec::new();
    let mut needs_descriptor = false;
    for predicate in &template.predicates {
        if predicate.kind == PredicateKind::Equality || predicate.kind == PredicateKind::Range {
            if !fields.contains(&predicate.field) {
                fields.push(predicate.field.clone());
                needs_descriptor |= !extracted.contains(&predicate.field);
            }
            if predicate.kind == PredicateKind::Range {
                break;
            }
        }
    }
    for field in &template.ordering {
        if !fields.contains(field) {
            fields.push(field.clone());
            needs_descriptor |= !extracted.contains(field);
        }
    }
    if fields.is_empty() {
        return Vec::new();
    }
    let base = Candidate {
        collection: template.collection.clone(),
        fields: fields.clone(),
        kind: CandidateKind::BTree,
        needs_descriptor,
        maintenance_cost: fields.len() as u64,
    };
    let mut result = vec![base];
    if let Some(projection) = &template.projection {
        let mut covering = fields;
        for field in projection {
            if !covering.contains(field) {
                covering.push(field.clone());
            }
        }
        if covering.len() <= 40 {
            let maintenance_cost = covering.len() as u64;
            result.push(Candidate {
                collection: template.collection.clone(),
                fields: covering,
                kind: CandidateKind::BTree,
                needs_descriptor,
                maintenance_cost,
            });
        }
    }
    result
}

/// Context used by the deterministic C²UCB-style greedy selector.
#[derive(Clone, Copy, Debug, Default)]
pub struct CandidateContext {
    /// Weighted workload benefit.
    pub benefit: f64,
    /// Uncertainty bonus (deliberate exploration).
    pub uncertainty: f64,
    /// Estimated collection selectivity.
    pub selectivity: f64,
    /// Collection row count.
    pub rows: u64,
}

/// Write-budget accounting for one collection.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct WriteBudget {
    /// Base write work.
    pub base_cost: f64,
    /// Current visible-index maintenance work.
    pub maintenance_cost: f64,
    /// Number of visible indexes.
    pub visible_indexes: usize,
}

impl WriteBudget {
    /// Returns whether an index with `cost` may be activated.
    #[must_use]
    pub fn allows(self, cost: f64) -> bool {
        self.base_cost.is_finite()
            && self.base_cost >= 0.0
            && cost.is_finite()
            && cost >= 0.0
            && self.visible_indexes < MAX_VISIBLE_INDEXES
            && self.maintenance_cost + cost <= self.base_cost * WRITE_BUDGET_MULTIPLE
    }
    /// Accounts one activated index.
    ///
    /// # Errors
    ///
    /// Returns [`CoordinatorError::WriteBudgetExceeded`] when `cost` does not
    /// fit the collection budget or index cap.
    pub fn account(&mut self, cost: f64) -> Result<(), CoordinatorError> {
        if !self.allows(cost) {
            return Err(CoordinatorError::WriteBudgetExceeded);
        }
        self.maintenance_cost += cost;
        self.visible_indexes += 1;
        Ok(())
    }
}

/// Selects the highest uncertainty-weighted benefit candidate that fits.
///
/// # Errors
///
/// Returns [`CoordinatorError::InvalidTransition`] when `candidates` and
/// `contexts` have different lengths.
pub fn select_candidate<'a>(
    candidates: &'a [Candidate],
    contexts: &[CandidateContext],
    budget: WriteBudget,
) -> Result<Option<&'a Candidate>, CoordinatorError> {
    if candidates.len() != contexts.len() {
        return Err(CoordinatorError::InvalidTransition);
    }
    candidates
        .iter()
        .zip(contexts)
        .enumerate()
        // Saturating: maintenance costs are bounded by the candidate field
        // limit; the fallback keeps selection total for oversized inputs.
        .filter(|(_, (candidate, _))| {
            budget.allows(f64::from(u32::try_from(candidate.maintenance_cost).unwrap_or(u32::MAX)))
        })
        .max_by(|(ia, (_, a)), (ib, (_, b))| {
            let sa = a.benefit + a.uncertainty * (1.0 + a.selectivity.max(0.0));
            let sb = b.benefit + b.uncertainty * (1.0 + b.selectivity.max(0.0));
            sa.total_cmp(&sb).then_with(|| ib.cmp(ia))
        })
        .map(|(_, (candidate, _))| candidate)
        .map_or(Ok(None), |candidate| Ok(Some(candidate)))
}

/// Exact disjoint plan for an invisible index's backfill tail.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct HybridPlan {
    /// Last sequence read from the index.
    pub index_through: u64,
    /// First sequence read from the base collection.
    pub scan_from: u64,
    /// Last sequence in the bounded scan tail.
    pub scan_through: u64,
}

/// Lifecycle phase persisted for every candidate.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum Phase {
    /// Workload observation; initial phase for new index entries.
    Observing,
    /// Candidate chosen and awaiting an invisible build.
    Selected,
    /// Invisible backfill toward the snapshot target.
    Building,
    /// Fully covered build under shadow verification.
    Shadowing,
    /// Planner-visible serving index.
    Active,
    /// Hidden but physically retained for the drop grace period.
    Invisible,
    /// Terminal dropped or rejected index.
    Abandoned,
}

/// Durable state of one candidate index.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct IndexState {
    /// Stable physical identity.
    pub id: String,
    /// Collection/table.
    pub collection: String,
    /// Lifecycle phase.
    pub phase: Phase,
    /// Build target sequence.
    pub target: u64,
    /// Last atomically applied sequence.
    pub covered: u64,
    /// Planner visibility bit (must be false until Active).
    pub visible: bool,
    /// Generation used to invalidate pins.
    pub generation: u64,
    /// Last usage timestamp.
    pub last_used_millis: u64,
    /// Timestamp hidden for delayed physical drop.
    pub invisible_since_millis: Option<u64>,
}

impl IndexState {
    /// Returns a hybrid plan when the uncovered tail fits the scan cap.
    #[must_use]
    pub fn hybrid_plan(&self, scan_cap: u64) -> Option<HybridPlan> {
        if self.phase != Phase::Building || self.covered >= self.target {
            return None;
        }
        let tail = self.target - self.covered;
        (tail <= scan_cap).then(|| HybridPlan {
            index_through: self.covered,
            scan_from: self.covered.saturating_add(1),
            scan_through: self.target,
        })
    }
}

/// Welch evidence for one template/candidate shadow replay.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct WelchEvidence {
    /// Baseline mean total work.
    pub baseline_mean: f64,
    /// Candidate mean total work.
    pub candidate_mean: f64,
    /// Fractional improvement.
    pub improvement: f64,
    /// Welch statistic.
    pub statistic: f64,
    /// Hard pages/rows/p95 guard crossed.
    pub regression: bool,
}

/// Shadow outcome.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ShadowOutcome {
    /// Significant improvement without regression; make the index visible.
    Activate,
    /// Regression guard crossed; activate with baseline plan pins.
    ActivateWithPins,
    /// Insufficient evidence; do not activate.
    Abandon,
}

/// Evaluates bounded logical-cost samples using a conservative Welch gate.
///
/// # Errors
///
/// Returns [`CoordinatorError::InsufficientEvidence`] when either side has
/// fewer than [`MIN_SHADOW_SAMPLES`] or more than [`MAX_SHADOW_SAMPLES`]
/// samples, a sample's total overflows, or the baseline mean is zero.
#[allow(
    clippy::cast_precision_loss,
    reason = "bounded logical counters; precision is below measurement noise"
)]
pub fn verify_shadow(
    baseline: &[LogicalCost],
    candidate: &[LogicalCost],
) -> Result<(ShadowOutcome, WelchEvidence), CoordinatorError> {
    if !(MIN_SHADOW_SAMPLES..=MAX_SHADOW_SAMPLES).contains(&baseline.len())
        || !(MIN_SHADOW_SAMPLES..=MAX_SHADOW_SAMPLES).contains(&candidate.len())
        || baseline
            .iter()
            .chain(candidate)
            .any(|sample| sample.pages_read.checked_add(sample.rows_examined).is_none())
    {
        return Err(CoordinatorError::InsufficientEvidence);
    }
    let mean = |samples: &[LogicalCost]| {
        let count = samples.len() as f64;
        samples.iter().map(|s| s.total() as f64).sum::<f64>() / count
    };
    let bm = mean(baseline);
    let cm = mean(candidate);
    if !bm.is_finite() || !cm.is_finite() || bm <= 0.0 {
        return Err(CoordinatorError::InsufficientEvidence);
    }
    let variance = |samples: &[LogicalCost], m: f64| {
        let count = samples.len() as f64;
        samples
            .iter()
            .map(|s| {
                let d = s.total() as f64 - m;
                d * d
            })
            .sum::<f64>()
            / (count - 1.0)
    };
    let baseline_count = baseline.len() as f64;
    let candidate_count = candidate.len() as f64;
    let se = variance(baseline, bm) / baseline_count + variance(candidate, cm) / candidate_count;
    let statistic = if se == 0.0 {
        if (bm - cm).abs() < f64::EPSILON { 0.0 } else { f64::INFINITY }
    } else {
        (bm - cm).abs() / se.sqrt()
    };
    if !statistic.is_finite() && (bm - cm).abs() < f64::EPSILON {
        return Err(CoordinatorError::InsufficientEvidence);
    }
    let improvement = (bm - cm) / bm;
    let regression = metric_regresses(baseline, candidate, LogicalCost::total)
        || metric_regresses(baseline, candidate, |sample| sample.pages_read)
        || metric_regresses(baseline, candidate, |sample| sample.rows_examined);
    let evidence =
        WelchEvidence { baseline_mean: bm, candidate_mean: cm, improvement, statistic, regression };
    let significant = statistic >= 2.0;
    let outcome = if regression {
        ShadowOutcome::ActivateWithPins
    } else if significant && improvement >= MIN_IMPROVEMENT_FRACTION {
        ShadowOutcome::Activate
    } else {
        ShadowOutcome::Abandon
    };
    Ok((outcome, evidence))
}

/// Exact five-percent guards for bounded integer measurements. A reduction
/// in rows cannot hide increased page work, or vice versa. At 4096 samples,
/// cross-multiplied sums fit in u128 even for `u64::MAX` measurements.
fn metric_regresses(
    baseline: &[LogicalCost],
    candidate: &[LogicalCost],
    metric: fn(LogicalCost) -> u64,
) -> bool {
    let sum = |samples: &[LogicalCost]| {
        samples.iter().map(|sample| u128::from(metric(*sample))).sum::<u128>()
    };
    let p95 = |samples: &[LogicalCost]| {
        let mut values: Vec<u64> = samples.iter().map(|sample| metric(*sample)).collect();
        values.sort_unstable();
        u128::from(values[(values.len() * 95).div_ceil(100) - 1])
    };
    sum(candidate) * baseline.len() as u128 * 100 > sum(baseline) * candidate.len() as u128 * 105
        || p95(candidate) * 100 > p95(baseline) * 105
}

/// A durable lifecycle event.  The adapter must append it before the caller
/// exposes the resulting state to the planner.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct LifecycleEvent {
    /// Monotone per-database event sequence.
    pub sequence: u64,
    /// Affected index identity.
    pub index: String,
    /// New phase.
    pub phase: Phase,
    /// Build watermark, if applicable.
    pub watermark: Option<u64>,
    /// Build target sequence, present on the initial `Building` event.
    pub target: Option<u64>,
    /// Candidate/evidence digest.
    pub evidence_digest: [u8; 32],
    /// Durable timestamp.
    pub at_millis: u64,
}

/// Exact storage hooks required by the coordinator.
pub trait StorageAdapter {
    /// Loads lifecycle events from the publication journal in sequence order.
    ///
    /// # Errors
    ///
    /// Returns [`HookError`] when the journal is unavailable, refuses the
    /// load, or reports a conflicting sequence.
    fn load_lifecycle(&mut self) -> Result<Vec<LifecycleEvent>, HookError>;
    /// Appends one event atomically with any descriptor/catalog mutation.
    ///
    /// # Errors
    ///
    /// Returns [`HookError`] when the journal is unavailable or refuses the
    /// append without changing durable state.
    fn append_lifecycle(&mut self, event: &LifecycleEvent) -> Result<(), HookError>;
    /// Applies a bounded contiguous batch to an invisible index.
    ///
    /// # Errors
    ///
    /// Returns [`HookError`] when backfill is unavailable, refused, or
    /// reports a sequence conflict.
    fn backfill_batch(&mut self, index: &str, first: u64, last: u64) -> Result<(), HookError>;
    /// Catches up and verifies the final tail in the same write transaction.
    ///
    /// # Errors
    ///
    /// Returns [`HookError`] when the publication transaction is unavailable,
    /// refused, or reports a sequence conflict.
    fn catch_up_and_verify(
        &mut self,
        index: &str,
        covered: u64,
        target: u64,
    ) -> Result<u64, HookError>;
    /// Changes planner visibility in the same durable catalog.
    ///
    /// # Errors
    ///
    /// Returns [`HookError`] when the planner hook is unavailable or refuses
    /// the visibility change.
    fn set_visible(&mut self, index: &str, visible: bool) -> Result<(), HookError>;
    /// Drops physical pages after the invisible grace period.
    ///
    /// # Errors
    ///
    /// Returns [`HookError`] when the storage hook is unavailable or refuses
    /// the physical drop.
    fn drop_physical(&mut self, index: &str) -> Result<(), HookError>;
    /// Installs/removes a template-specific baseline plan pin.
    ///
    /// # Errors
    ///
    /// Returns [`HookError`] when the planner hook is unavailable or refuses
    /// the pin change.
    fn set_plan_pin(&mut self, template: &str, index: &str, pinned: bool) -> Result<(), HookError>;
    /// Records logical pages/rows for a normalized template.
    ///
    /// # Errors
    ///
    /// Returns [`HookError`] when the logical-cost hook is unavailable or
    /// refuses the record.
    fn record_logical_cost(
        &mut self,
        template: &Template,
        cost: LogicalCost,
    ) -> Result<(), HookError>;
    /// Runs a hypothetical compile check without mutating the live schema.
    ///
    /// # Errors
    ///
    /// Returns [`HookError`] when the planner hook is unavailable or refuses
    /// the hypothetical compile.
    fn compile_hypothetical(
        &mut self,
        template: &Template,
        candidate: &Candidate,
    ) -> Result<bool, HookError>;
}

/// A coordinator with durable lifecycle replay and fail-closed transitions.
pub struct Coordinator<A> {
    adapter: A,
    states: BTreeMap<String, IndexState>,
    next_sequence: u64,
}

// ---------------------------------------------------------------------------
// Engine integration surface
// ---------------------------------------------------------------------------

/// The lifecycle phase exposed by the Engine adapter.  This is deliberately
/// separate from the query optimizer's compatibility lifecycle enum: the
/// values below are stable wire-facing facts and are persisted with the
/// database rather than inferred from a process-local catalog.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum LifecyclePhase {
    /// Workload is being observed.
    Observing,
    /// Candidate was selected for evaluation.
    Selected,
    /// Physical index is being built invisibly.
    Building,
    /// Candidate is under shadow verification.
    Shadowing,
    /// Index is visible to the planner.
    Active,
    /// Index is hidden but retained for delayed removal.
    Invisible,
    /// Candidate was rejected or physically removed.
    Abandoned,
}

/// One durable lifecycle index view returned by the Engine.
#[derive(Clone, Debug, Eq, PartialEq)]
#[allow(
    clippy::struct_excessive_bools,
    reason = "frozen D-31 lifecycle view; each bool is an independent planner proof and splitting it breaks the wire shape"
)]
pub struct LifecycleIndexView {
    /// Stable candidate/index identity.
    pub id: String,
    /// Owning tenant.
    pub tenant: String,
    /// Collection containing the index.
    pub collection: String,
    /// Ordered extracted fields.
    pub fields: Vec<String>,
    /// Current lifecycle phase.
    pub phase: LifecyclePhase,
    /// Lifecycle generation used for optimistic commands.
    pub generation: u64,
    /// Last covered publication sequence.
    pub covered_watermark: u64,
    /// Build boundary, when one exists.
    pub target_watermark: Option<u64>,
    /// Planner visibility.
    pub visible: bool,
    /// Whether customer control pinned this index.
    pub pinned: bool,
    /// Whether its shape is forbidden.
    pub forbidden: bool,
    /// Estimated maintenance units per write.
    pub maintenance_cost: u64,
    /// Last durable use timestamp.
    pub last_used_millis: Option<u64>,
    /// Proof that publication maintenance is atomic with writes.
    pub zero_stale_reads: bool,
    /// Proof that the visible plan passed the five-percent guard.
    pub regression_free: bool,
}

/// One bounded recommendation returned by the Engine.
#[derive(Clone, Debug, PartialEq)]
pub struct LifecycleRecommendation {
    /// Stable candidate identity.
    pub candidate_id: String,
    /// Owning tenant.
    pub tenant: String,
    /// Collection for the candidate.
    pub collection: String,
    /// Ordered fields proposed for the index.
    pub fields: Vec<String>,
    /// Deterministic score from observed frequency and measured work.
    pub score: f64,
    /// Estimated write maintenance cost.
    pub maintenance_cost: u64,
    /// Current lifecycle generation.
    pub generation: u64,
    /// Whether an extracted descriptor must be installed.
    pub needs_descriptor: bool,
}

/// A bounded lifecycle snapshot.
#[derive(Clone, Debug, PartialEq)]
pub struct LifecycleSnapshot {
    /// Current lifecycle generation.
    pub generation: u64,
    /// Whether records were omitted by the requested bound.
    pub truncated: bool,
    /// Index records in deterministic identity order.
    pub indexes: Vec<LifecycleIndexView>,
}

/// A bounded recommendation page.
#[derive(Clone, Debug, PartialEq)]
pub struct RecommendationSnapshot {
    /// Current lifecycle generation.
    pub generation: u64,
    /// Whether records were omitted by the requested bound.
    pub truncated: bool,
    /// Recommendations in descending score order.
    pub recommendations: Vec<LifecycleRecommendation>,
}

/// One durable lifecycle audit event.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct LifecycleAuditEntry {
    /// Monotone journal sequence.
    pub sequence: u64,
    /// Affected index, if any.
    pub index_id: Option<String>,
    /// Action/transition name.
    pub action: String,
    /// Generation at the event.
    pub generation: u64,
    /// Evidence digest attached to the event.
    pub evidence_digest: Option<[u8; 32]>,
    /// Durable event timestamp.
    pub at_millis: u64,
}

/// Before/after logical-cost evidence retained for one candidate.
#[derive(Clone, Debug, PartialEq)]
pub struct ShadowEvidence {
    /// Candidate/index identity.
    pub index_id: String,
    /// Generation the evidence belongs to.
    pub generation: u64,
    /// Digest of the exact normalized template measured by the shadow worker.
    pub template_digest: [u8; 32],
    /// Baseline logical samples `(pages, rows)`.
    pub baseline: Vec<LogicalCost>,
    /// Candidate logical samples `(pages, rows)`.
    pub candidate: Vec<LogicalCost>,
    /// Fractional reduction in total work.
    pub improvement: f64,
    /// Whether any five-percent guard was crossed.
    pub regression: bool,
    /// Digest committed with the lifecycle event.
    pub digest: [u8; 32],
}

/// Engine-owned lifecycle state replayed from the database WAL-backed journal.
#[derive(Debug)]
pub(crate) struct LifecycleStore {
    generations: BTreeMap<String, u64>,
    next_sequence: u64,
    indexes: BTreeMap<(String, String), LifecycleIndexView>,
    forbidden: BTreeSet<(String, String)>,
    audits: VecDeque<(String, String, LifecycleAuditEntry)>,
    evidence: BTreeMap<(String, String, [u8; 32]), ShadowEvidence>,
    pins: BTreeSet<(String, String, [u8; 32])>,
}

const LIFECYCLE_MAX_FRAME: usize = 512 * 1024;
const LIFECYCLE_MAX_AUDIT: usize = 4096;
/// Database-wide evidence bound, independent of the number of tenants.
const LIFECYCLE_MAX_EVIDENCE: usize = 256;

#[derive(Clone, Copy)]
enum LifecycleDetail<'a> {
    Install,
    Forbid,
    Retire,
    Evidence(&'a ShadowEvidence),
    Pin(&'a [u8; 32]),
}

impl LifecycleDetail<'_> {
    fn kind(&self) -> u8 {
        match self {
            Self::Install => 1,
            Self::Forbid => 2,
            Self::Retire => 3,
            Self::Evidence(_) => 4,
            Self::Pin(_) => 5,
        }
    }
}

impl LifecycleStore {
    /// Opens and replays the lifecycle journal for one durable location.
    pub(crate) fn open(storage: &dyn StoragePort) -> Result<Self, ()> {
        let mut store = Self {
            generations: BTreeMap::new(),
            next_sequence: 1,
            indexes: BTreeMap::new(),
            forbidden: BTreeSet::new(),
            audits: VecDeque::new(),
            evidence: BTreeMap::new(),
            pins: BTreeSet::new(),
        };
        loop {
            let page = storage.index_lifecycle_events(store.next_sequence, 64)?;
            if page.is_empty() {
                return Ok(store);
            }
            for payload in page {
                store.replay(&payload)?;
            }
        }
    }

    fn replay(&mut self, payload: &[u8]) -> Result<(), ()> {
        let mut cursor = ByteCursor::new(payload);
        let sequence = cursor.u64()?;
        if sequence != self.next_sequence {
            return Err(());
        }
        self.next_sequence = sequence.checked_add(1).ok_or(())?;
        let kind = cursor.byte()?;
        let generation = cursor.u64()?;
        let tenant = cursor.string()?;
        let id = cursor.string()?;
        if tenant.is_empty()
            || id.is_empty()
            || self.generation(&tenant).checked_add(1) != Some(generation)
        {
            return Err(());
        }
        self.generations.insert(tenant.clone(), generation);
        let key = (tenant.clone(), id.clone());
        let collection = cursor.string()?;
        let count = usize::from(u16::from_le_bytes(cursor.take(2)?.try_into().map_err(|_| ())?));
        let mut fields = Vec::new();
        for _ in 0..count {
            fields.push(cursor.string()?);
        }
        let at_millis = cursor.u64()?;
        let digest = cursor.array32()?;
        match kind {
            1 => {
                self.indexes.insert(
                    key.clone(),
                    cursor.lifecycle_view(
                        (tenant.clone(), id.clone(), collection.clone()),
                        fields,
                        generation,
                    )?,
                );
            }
            2 => {
                self.forbidden.insert(key.clone());
                if let Some(view) = self.indexes.get_mut(&key) {
                    view.forbidden = true;
                    view.generation = generation;
                }
            }
            3 => {
                let view = self.indexes.get_mut(&key).ok_or(())?;
                view.phase = LifecyclePhase::Abandoned;
                view.visible = false;
                view.generation = generation;
            }
            4 => {
                let template_digest = cursor.array32()?;
                let baseline = cursor.samples()?;
                let candidate = cursor.samples()?;
                let view = self.indexes.get(&key).ok_or(())?;
                if view.collection != collection || view.fields != fields {
                    return Err(());
                }
                validate_evidence_view(view)?;
                let evidence =
                    make_evidence(&tenant, &id, generation, template_digest, baseline, candidate)?;
                if evidence.digest != digest {
                    return Err(());
                }
                let evidence_key = self.evidence_key(&tenant, &id, template_digest)?;
                self.evidence.insert(evidence_key, evidence);
                self.indexes.get_mut(&key).ok_or(())?.generation = generation;
            }
            5 => {
                let template_digest = cursor.array32()?;
                let view = self.indexes.get_mut(&key).ok_or(())?;
                if view.phase != LifecyclePhase::Active || !view.visible {
                    return Err(());
                }
                view.pinned = true;
                view.generation = generation;
                self.pins.insert((tenant.clone(), id.clone(), template_digest));
            }
            _ => return Err(()),
        }
        if cursor.offset != payload.len() {
            return Err(());
        }
        if kind != 4 && kind != 5 {
            self.invalidate_evidence(&key.0, &key.1);
        }
        self.audits.push_back((
            tenant,
            collection,
            LifecycleAuditEntry {
                sequence,
                index_id: (!id.is_empty()).then_some(id),
                action: match kind {
                    1 => "transition",
                    2 => "forbid",
                    3 => "retire",
                    4 => "evidence",
                    5 => "pin",
                    _ => return Err(()),
                }
                .to_owned(),
                generation,
                evidence_digest: Some(digest),
                at_millis,
            },
        ));
        while self.audits.len() > LIFECYCLE_MAX_AUDIT {
            self.audits.pop_front();
        }
        Ok(())
    }

    fn append(
        &mut self,
        storage: &mut dyn StoragePort,
        detail: LifecycleDetail<'_>,
        view: Option<&LifecycleIndexView>,
        identity: (&str, &str),
        at_millis: u64,
        digest: [u8; 32],
    ) -> Result<u64, ()> {
        let (tenant, id) = identity;
        let kind = detail.kind();
        if tenant.is_empty()
            || id.is_empty()
            || view.is_some_and(|view| view.tenant != tenant || view.id != id)
        {
            return Err(());
        }
        let sequence = self.next_sequence;
        let generation = self.generation(tenant).checked_add(1).ok_or(())?;
        let next_sequence = sequence.checked_add(1).ok_or(())?;
        let (collection, phase, covered, target, pinned, forbidden) = view.map_or_else(
            || ("", LifecyclePhase::Observing, 0, None, false, true),
            |view| {
                (
                    view.collection.as_str(),
                    view.phase,
                    view.covered_watermark,
                    view.target_watermark,
                    view.pinned,
                    view.forbidden,
                )
            },
        );
        let mut payload = Vec::new();
        payload.extend_from_slice(&sequence.to_le_bytes());
        payload.push(kind);
        payload.extend_from_slice(&generation.to_le_bytes());
        put_string(&mut payload, tenant)?;
        put_string(&mut payload, id)?;
        put_string(&mut payload, collection)?;
        let fields = view.map_or(&[][..], |view| view.fields.as_slice());
        payload.extend_from_slice(&u16::try_from(fields.len()).map_err(|_| ())?.to_le_bytes());
        for field in fields {
            put_string(&mut payload, field)?;
        }
        payload.extend_from_slice(&at_millis.to_le_bytes());
        payload.extend_from_slice(&digest);
        if kind == 1 {
            payload.push(phase_to_byte(phase));
            payload.extend_from_slice(&covered.to_le_bytes());
            match target {
                Some(value) => {
                    payload.push(1);
                    payload.extend_from_slice(&value.to_le_bytes());
                }
                None => payload.push(0),
            }
            payload.push(u8::from(pinned));
            payload.push(u8::from(forbidden));
            let view = view.ok_or(())?;
            payload.extend_from_slice(&view.maintenance_cost.to_le_bytes());
            match view.last_used_millis {
                Some(value) => {
                    payload.push(1);
                    payload.extend_from_slice(&value.to_le_bytes());
                }
                None => payload.push(0),
            }
            payload.push(u8::from(view.visible));
            payload.push(u8::from(view.zero_stale_reads));
            payload.push(u8::from(view.regression_free));
        }
        match detail {
            LifecycleDetail::Evidence(evidence) => {
                payload.extend_from_slice(&evidence.template_digest);
                put_samples(&mut payload, &evidence.baseline)?;
                put_samples(&mut payload, &evidence.candidate)?;
            }
            LifecycleDetail::Pin(template_digest) => payload.extend_from_slice(template_digest),
            _ => {}
        }
        if payload.len() > LIFECYCLE_MAX_FRAME {
            return Err(());
        }
        storage.append_index_lifecycle_event(sequence, &payload)?;
        if kind != 4 && view.is_none_or(|value| value.phase != LifecyclePhase::Active) {
            self.invalidate_evidence(tenant, id);
        }
        self.next_sequence = next_sequence;
        self.generations.insert(tenant.to_owned(), generation);
        self.audits.push_back((
            tenant.to_owned(),
            collection.to_owned(),
            LifecycleAuditEntry {
                sequence,
                index_id: (!id.is_empty()).then_some(id.to_owned()),
                action: match kind {
                    1 => "transition",
                    2 => "forbid",
                    3 => "retire",
                    4 => "evidence",
                    5 => "pin",
                    _ => "unknown",
                }
                .to_owned(),
                generation,
                evidence_digest: Some(digest),
                at_millis,
            },
        ));
        while self.audits.len() > LIFECYCLE_MAX_AUDIT {
            self.audits.pop_front();
        }
        Ok(sequence)
    }

    /// Returns the current durable lifecycle generation.
    pub(crate) fn generation(&self, tenant: &str) -> u64 {
        self.generations.get(tenant).copied().unwrap_or(0)
    }

    /// Returns a bounded tenant/collection snapshot.
    pub(crate) fn snapshot(
        &self,
        tenant: &str,
        collection: Option<&str>,
        limit: usize,
    ) -> LifecycleSnapshot {
        let mut indexes: Vec<_> = self
            .indexes
            .range((tenant.to_owned(), String::new())..)
            .take_while(|((owner, _), _)| owner == tenant)
            .map(|(_, index)| index)
            .filter(|index| collection.is_none_or(|name| index.collection == name))
            .take(limit.saturating_add(1))
            .cloned()
            .collect();
        let truncated = indexes.len() > limit;
        indexes.truncate(limit);
        LifecycleSnapshot { generation: self.generation(tenant), truncated, indexes }
    }

    /// Looks up one tenant-owned record without allocating a catalog snapshot.
    pub(crate) fn index(&self, tenant: &str, index_id: &str) -> Option<&LifecycleIndexView> {
        self.indexes.get(&(tenant.to_owned(), index_id.to_owned()))
    }

    /// Returns a bounded durable audit page.
    pub(crate) fn audit(
        &self,
        tenant: &str,
        collection: Option<&str>,
        index_id: Option<&str>,
        limit: usize,
    ) -> Vec<LifecycleAuditEntry> {
        self.audits
            .iter()
            .filter(|(owner, event_collection, entry)| {
                owner == tenant
                    && collection
                        .is_none_or(|name| !event_collection.is_empty() && event_collection == name)
                    && index_id.is_none_or(|id| entry.index_id.as_deref() == Some(id))
            })
            .rev()
            .take(limit)
            .map(|(_, _, entry)| entry.clone())
            .collect()
    }

    /// Returns retained evidence, if a shadow worker has recorded it.
    pub(crate) fn evidence(
        &self,
        tenant: &str,
        index_id: &str,
        template_digest: [u8; 32],
        limit: usize,
    ) -> Option<ShadowEvidence> {
        let mut evidence =
            self.evidence.get(&(tenant.to_owned(), index_id.to_owned(), template_digest))?.clone();
        evidence.baseline.truncate(limit);
        evidence.candidate.truncate(limit);
        Some(evidence)
    }

    /// Returns the digest of the oldest retained template evidence for an
    /// index. Evidence keys are ordered, making the public aggregate endpoint
    /// deterministic while preserving the exact template identity on disk.
    pub(crate) fn evidence_digest(&self, tenant: &str, index_id: &str) -> Option<[u8; 32]> {
        self.evidence
            .range((tenant.to_owned(), index_id.to_owned(), [0; 32])..)
            .take_while(|((owner, candidate, _), _)| owner == tenant && candidate == index_id)
            .map(|((_, _, digest), _)| *digest)
            .next()
    }

    fn invalidate_evidence(&mut self, tenant: &str, id: &str) {
        self.evidence.retain(|(owner, candidate, _), _| owner != tenant || candidate != id);
    }

    fn evidence_key(
        &self,
        tenant: &str,
        id: &str,
        template: [u8; 32],
    ) -> Result<(String, String, [u8; 32]), ()> {
        let key = (tenant.to_owned(), id.to_owned(), template);
        if self.evidence.len() >= LIFECYCLE_MAX_EVIDENCE && !self.evidence.contains_key(&key) {
            return Err(());
        }
        Ok(key)
    }

    /// Persists and installs one lifecycle view after the caller has checked
    /// its generation and budget.  Persistence precedes the visible memory
    /// mutation, so a failed fsync cannot report a successful transition.
    pub(crate) fn install(
        &mut self,
        storage: &mut dyn StoragePort,
        mut view: LifecycleIndexView,
        at_millis: u64,
        digest: [u8; 32],
    ) -> Result<(), ()> {
        view.generation = self.generation(&view.tenant).checked_add(1).ok_or(())?;
        self.append(
            storage,
            LifecycleDetail::Install,
            Some(&view),
            (&view.tenant, &view.id),
            at_millis,
            digest,
        )?;
        self.indexes.insert((view.tenant.clone(), view.id.clone()), view);
        Ok(())
    }

    /// Persists a pin or forbidden-shape control mutation.
    pub(crate) fn control(
        &mut self,
        storage: &mut dyn StoragePort,
        identity: (&str, &str),
        forbidden: bool,
        at_millis: u64,
        digest: [u8; 32],
    ) -> Result<(), ()> {
        let key = (identity.0.to_owned(), identity.1.to_owned());
        let mut view = self.indexes.get(&key).cloned();
        if let Some(view) = &mut view {
            view.generation = self.generation(&view.tenant).checked_add(1).ok_or(())?;
            if forbidden {
                view.forbidden = true;
            } else {
                view.pinned = true;
            }
        } else if !forbidden {
            return Err(());
        }
        self.append(
            storage,
            if forbidden { LifecycleDetail::Forbid } else { LifecycleDetail::Install },
            view.as_ref(),
            identity,
            at_millis,
            digest,
        )?;
        if forbidden {
            self.forbidden.insert(key.clone());
        }
        if let Some(view) = view {
            self.indexes.insert(key, view);
        }
        Ok(())
    }

    /// Persists one exact normalized template pin without collapsing it into
    /// the index-wide customer pin flag.
    pub(crate) fn pin_template(
        &mut self,
        storage: &mut dyn StoragePort,
        identity: (&str, &str),
        template: &str,
        at_millis: u64,
    ) -> Result<(), ()> {
        if template.is_empty() || template.len() > 4096 {
            return Err(());
        }
        let key = (identity.0.to_owned(), identity.1.to_owned());
        let mut view = self.indexes.get(&key).cloned().ok_or(())?;
        if view.phase != LifecyclePhase::Active || !view.visible {
            return Err(());
        }
        let digest = *blake3::hash(template.as_bytes()).as_bytes();
        if self.pins.contains(&(key.0.clone(), key.1.clone(), digest)) {
            return Ok(());
        }
        view.pinned = true;
        self.append(
            storage,
            LifecycleDetail::Pin(&digest),
            Some(&view),
            identity,
            at_millis,
            digest,
        )?;
        view.generation = self.generation(identity.0);
        self.indexes.insert(key.clone(), view);
        self.pins.insert((key.0, key.1, digest));
        Ok(())
    }

    /// Persists an index retirement and removes it from the serving view.
    pub(crate) fn retire_index(
        &mut self,
        storage: &mut dyn StoragePort,
        identity: (&str, &str),
        at_millis: u64,
        digest: [u8; 32],
    ) -> Result<(), ()> {
        let key = (identity.0.to_owned(), identity.1.to_owned());
        let mut view = self.indexes.get(&key).cloned().ok_or(())?;
        view.phase = LifecyclePhase::Abandoned;
        view.visible = false;
        view.generation = self.generation(&view.tenant).checked_add(1).ok_or(())?;
        self.append(storage, LifecycleDetail::Retire, Some(&view), identity, at_millis, digest)?;
        self.indexes.insert(key, view);
        Ok(())
    }

    /// Commits bounded measurements without changing planner visibility.
    /// The caller supplies the observed generation; derived fields and the
    /// evidence digest are recomputed from the measurements before persistence.
    #[allow(
        dead_code,
        reason = "TODO E-4b: the shadow worker that records paired baseline/candidate samples is not built yet"
    )]
    pub(crate) fn record_evidence(
        &mut self,
        storage: &mut dyn StoragePort,
        tenant: &str,
        evidence: ShadowEvidence,
        at_millis: u64,
    ) -> Result<ShadowEvidence, ()> {
        if evidence.generation != self.generation(tenant) {
            return Err(());
        }
        let key = (tenant.to_owned(), evidence.index_id.clone());
        let evidence_key =
            self.evidence_key(tenant, &evidence.index_id, evidence.template_digest)?;
        let mut view = self.indexes.get(&key).cloned().ok_or(())?;
        validate_evidence_view(&view)?;
        let generation = evidence.generation.checked_add(1).ok_or(())?;
        let evidence = make_evidence(
            tenant,
            &evidence.index_id,
            generation,
            evidence.template_digest,
            evidence.baseline,
            evidence.candidate,
        )?;
        self.append(
            storage,
            LifecycleDetail::Evidence(&evidence),
            Some(&view),
            (tenant, &evidence.index_id),
            at_millis,
            evidence.digest,
        )?;
        view.generation = generation;
        self.indexes.insert(key.clone(), view);
        self.evidence.insert(evidence_key, evidence.clone());
        Ok(evidence)
    }

    /// Returns whether a shape has been forbidden by customer control.
    pub(crate) fn is_forbidden(&self, tenant: &str, id: &str) -> bool {
        self.forbidden.contains(&(tenant.to_owned(), id.to_owned()))
    }
}

fn validate_evidence_view(view: &LifecycleIndexView) -> Result<(), ()> {
    if view.phase != LifecyclePhase::Shadowing
        || view.visible
        || view.forbidden
        || view.target_watermark != Some(view.covered_watermark)
    {
        return Err(());
    }
    Ok(())
}

fn make_evidence(
    tenant: &str,
    index_id: &str,
    generation: u64,
    template_digest: [u8; 32],
    baseline: Vec<LogicalCost>,
    candidate: Vec<LogicalCost>,
) -> Result<ShadowEvidence, ()> {
    if template_digest == [0; 32]
        || baseline.len() != candidate.len()
        || baseline.len() > MAX_SHADOW_SAMPLES
        || baseline
            .iter()
            .chain(&candidate)
            .any(|sample| sample.pages_read.checked_add(sample.rows_examined).is_none())
    {
        return Err(());
    }
    let (_, verified) = verify_shadow(&baseline, &candidate).map_err(|_| ())?;
    let mut bytes = b"terrabase-shadow-evidence-v1".to_vec();
    put_string(&mut bytes, tenant)?;
    put_string(&mut bytes, index_id)?;
    bytes.extend_from_slice(&generation.to_le_bytes());
    bytes.extend_from_slice(&template_digest);
    put_samples(&mut bytes, &baseline)?;
    put_samples(&mut bytes, &candidate)?;
    Ok(ShadowEvidence {
        index_id: index_id.to_owned(),
        generation,
        template_digest,
        baseline,
        candidate,
        improvement: verified.improvement,
        regression: verified.regression,
        digest: *blake3::hash(&bytes).as_bytes(),
    })
}

fn put_samples(out: &mut Vec<u8>, samples: &[LogicalCost]) -> Result<(), ()> {
    if samples.len() > MAX_SHADOW_SAMPLES {
        return Err(());
    }
    out.extend_from_slice(&u16::try_from(samples.len()).map_err(|_| ())?.to_le_bytes());
    for sample in samples {
        out.extend_from_slice(&sample.pages_read.to_le_bytes());
        out.extend_from_slice(&sample.rows_examined.to_le_bytes());
    }
    Ok(())
}

struct ByteCursor<'a> {
    bytes: &'a [u8],
    offset: usize,
}
impl<'a> ByteCursor<'a> {
    fn lifecycle_view(
        &mut self,
        (tenant, id, collection): (String, String, String),
        fields: Vec<String>,
        generation: u64,
    ) -> Result<LifecycleIndexView, ()> {
        Ok(LifecycleIndexView {
            id,
            tenant,
            collection,
            fields,
            phase: phase_from_byte(self.byte()?)?,
            generation,
            covered_watermark: self.u64()?,
            target_watermark: self.optional_u64()?,
            pinned: self.boolean()?,
            forbidden: self.boolean()?,
            maintenance_cost: self.u64()?,
            last_used_millis: self.optional_u64()?,
            visible: self.boolean()?,
            zero_stale_reads: self.boolean()?,
            regression_free: self.boolean()?,
        })
    }

    fn new(bytes: &'a [u8]) -> Self {
        Self { bytes, offset: 0 }
    }
    fn take(&mut self, count: usize) -> Result<&'a [u8], ()> {
        let end = self.offset.checked_add(count).ok_or(())?;
        let value = self.bytes.get(self.offset..end).ok_or(())?;
        self.offset = end;
        Ok(value)
    }
    fn byte(&mut self) -> Result<u8, ()> {
        Ok(*self.take(1)?.first().ok_or(())?)
    }
    fn boolean(&mut self) -> Result<bool, ()> {
        match self.byte()? {
            0 => Ok(false),
            1 => Ok(true),
            _ => Err(()),
        }
    }
    fn u64(&mut self) -> Result<u64, ()> {
        Ok(u64::from_le_bytes(self.take(8)?.try_into().map_err(|_| ())?))
    }
    fn string(&mut self) -> Result<String, ()> {
        let len = usize::from(u16::from_le_bytes(self.take(2)?.try_into().map_err(|_| ())?));
        String::from_utf8(self.take(len)?.to_vec()).map_err(|_| ())
    }
    fn array32(&mut self) -> Result<[u8; 32], ()> {
        self.take(32)?.try_into().map_err(|_| ())
    }
    fn samples(&mut self) -> Result<Vec<LogicalCost>, ()> {
        let count = usize::from(u16::from_le_bytes(self.take(2)?.try_into().map_err(|_| ())?));
        if count > MAX_SHADOW_SAMPLES {
            return Err(());
        }
        (0..count).map(|_| Ok(LogicalCost::new(self.u64()?, self.u64()?))).collect()
    }
    fn optional_u64(&mut self) -> Result<Option<u64>, ()> {
        Ok(match self.byte()? {
            0 => None,
            1 => Some(self.u64()?),
            _ => return Err(()),
        })
    }
}
fn put_string(out: &mut Vec<u8>, value: &str) -> Result<(), ()> {
    let len = u16::try_from(value.len()).map_err(|_| ())?;
    out.extend_from_slice(&len.to_le_bytes());
    out.extend_from_slice(value.as_bytes());
    Ok(())
}
fn phase_to_byte(phase: LifecyclePhase) -> u8 {
    match phase {
        LifecyclePhase::Observing => 1,
        LifecyclePhase::Selected => 2,
        LifecyclePhase::Building => 3,
        LifecyclePhase::Shadowing => 4,
        LifecyclePhase::Active => 5,
        LifecyclePhase::Invisible => 6,
        LifecyclePhase::Abandoned => 7,
    }
}
fn phase_from_byte(value: u8) -> Result<LifecyclePhase, ()> {
    Ok(match value {
        1 => LifecyclePhase::Observing,
        2 => LifecyclePhase::Selected,
        3 => LifecyclePhase::Building,
        4 => LifecyclePhase::Shadowing,
        5 => LifecyclePhase::Active,
        6 => LifecyclePhase::Invisible,
        7 => LifecyclePhase::Abandoned,
        _ => return Err(()),
    })
}

impl<A: StorageAdapter> Coordinator<A> {
    /// Refuses an unqualified recovery.  Callers must use
    /// [`Self::recover_with_hooks`] so a deployment cannot accidentally make
    /// an in-memory-only coordinator authoritative.
    ///
    /// # Errors
    ///
    /// Always returns [`CoordinatorError::MissingHook`] for
    /// [`HookKind::Journal`].
    pub fn recover(_adapter: A) -> Result<Self, CoordinatorError> {
        Err(CoordinatorError::MissingHook(HookKind::Journal))
    }
    /// Recovers only when the member explicitly reports every required hook.
    ///
    /// # Errors
    ///
    /// Returns [`CoordinatorError::MissingHook`] for an absent hook,
    /// [`CoordinatorError::Hook`] when the journal load fails, and
    /// [`CoordinatorError::JournalConflict`], [`CoordinatorError::InvalidTransition`],
    /// or [`CoordinatorError::InvalidWatermark`] for a conflicting replay event.
    pub fn recover_with_hooks(
        adapter: A,
        hooks: HookAvailability,
    ) -> Result<Self, CoordinatorError> {
        hooks.validate()?;
        let mut adapter = adapter;
        let events = adapter.load_lifecycle().map_err(CoordinatorError::Hook)?;
        let mut coordinator = Self { adapter, states: BTreeMap::new(), next_sequence: 1 };
        for event in events {
            coordinator.apply_event(&event)?;
        }
        Ok(coordinator)
    }
    /// Returns the adapter for integration-owned maintenance work.
    pub fn adapter_mut(&mut self) -> &mut A {
        &mut self.adapter
    }
    /// Returns one recovered index state.
    pub fn state(&self, index: &str) -> Option<&IndexState> {
        self.states.get(index)
    }
    /// Records planner use.  The timestamp is durable when the next journal
    /// event is written, so restart cannot make a rare query look unused.
    ///
    /// # Errors
    ///
    /// Returns [`CoordinatorError::InvalidTransition`] when the index is
    /// unknown or is not a visible [`Phase::Active`] index.
    pub fn note_used(&mut self, index: &str, at_millis: u64) -> Result<(), CoordinatorError> {
        let state = self.states.get(index).ok_or(CoordinatorError::InvalidTransition)?;
        if state.phase != Phase::Active || !state.visible {
            return Err(CoordinatorError::InvalidTransition);
        }
        let covered = state.covered;
        self.append(LifecycleEvent {
            sequence: 0,
            index: index.to_owned(),
            phase: Phase::Active,
            watermark: Some(covered),
            target: None,
            evidence_digest: [0; 32],
            at_millis,
        })
    }
    /// Applies an event to memory after journal replay validation.
    fn apply_event(&mut self, event: &LifecycleEvent) -> Result<(), CoordinatorError> {
        let (state, next_sequence) = self.prepare_event(event)?;
        self.states.insert(event.index.clone(), state);
        self.next_sequence = next_sequence;
        Ok(())
    }

    /// Validates a prospective event without changing memory or storage.
    fn prepare_event(&self, event: &LifecycleEvent) -> Result<(IndexState, u64), CoordinatorError> {
        if event.sequence != self.next_sequence {
            return Err(CoordinatorError::JournalConflict);
        }
        let next_sequence =
            self.next_sequence.checked_add(1).ok_or(CoordinatorError::JournalConflict)?;
        let mut state = self.states.get(&event.index).cloned().unwrap_or_else(|| IndexState {
            id: event.index.clone(),
            collection: String::new(),
            phase: Phase::Observing,
            target: 0,
            covered: 0,
            visible: false,
            generation: 0,
            last_used_millis: 0,
            invisible_since_millis: None,
        });
        let legal = match (state.phase, event.phase) {
            (Phase::Observing, Phase::Selected)
            | (Phase::Selected, Phase::Building)
            | (Phase::Building, Phase::Shadowing)
            | (Phase::Shadowing, Phase::Active | Phase::Abandoned)
            | (Phase::Active, Phase::Invisible)
            | (Phase::Invisible, Phase::Abandoned) => true,
            // A replay may contain an exact duplicate event; sequence
            // ordering still protects against a conflicting duplicate.
            (from, to) if from == to => true,
            _ => false,
        };
        if !legal {
            return Err(CoordinatorError::InvalidTransition);
        }
        if matches!(event.phase, Phase::Shadowing | Phase::Active)
            && (state.target == 0 || state.covered != state.target)
        {
            return Err(CoordinatorError::InvalidWatermark);
        }
        if let Some(target) = event.target {
            if event.phase != Phase::Building
                || target == 0
                || event.watermark != Some(target)
                || state.covered != 0
                || (state.phase == Phase::Building && target != state.target)
            {
                return Err(CoordinatorError::InvalidWatermark);
            }
            state.target = target;
        }
        if event.phase == Phase::Building && state.target == 0 {
            return Err(CoordinatorError::InvalidWatermark);
        }
        if let Some(watermark) = event.watermark {
            if watermark < state.covered
                || (event.phase == Phase::Building && watermark > state.target)
            {
                return Err(CoordinatorError::InvalidWatermark);
            }
            // A target-bearing Building event describes the snapshot boundary;
            // subsequent watermark events describe applied coverage.
            if event.target.is_none() || event.phase != Phase::Building {
                state.covered = watermark;
            }
            state.target = state.target.max(watermark);
        }
        if matches!(event.phase, Phase::Shadowing | Phase::Active)
            && (state.target == 0 || state.covered != state.target)
        {
            return Err(CoordinatorError::InvalidWatermark);
        }
        state.phase = event.phase;
        state.visible = event.phase == Phase::Active;
        if event.phase == Phase::Active {
            state.last_used_millis = state.last_used_millis.max(event.at_millis);
        }
        if event.phase == Phase::Invisible {
            state.invisible_since_millis = Some(event.at_millis);
        }
        Ok((state, next_sequence))
    }
    /// Validates an event, durably appends it, then installs the prepared state.
    /// Validation and append failures leave memory unchanged; invalid events
    /// never reach the journal.
    ///
    /// # Errors
    ///
    /// Returns [`CoordinatorError::Hook`] when the journal append fails, and
    /// [`CoordinatorError::JournalConflict`], [`CoordinatorError::InvalidTransition`],
    /// or [`CoordinatorError::InvalidWatermark`] when replay validation fails.
    pub fn append(&mut self, mut event: LifecycleEvent) -> Result<(), CoordinatorError> {
        event.sequence = self.next_sequence;
        let (state, next_sequence) = self.prepare_event(&event)?;
        self.adapter.append_lifecycle(&event).map_err(CoordinatorError::Hook)?;
        self.states.insert(event.index, state);
        self.next_sequence = next_sequence;
        Ok(())
    }
    /// Starts an invisible build at a durable snapshot boundary.
    ///
    /// # Errors
    ///
    /// Returns [`CoordinatorError::InvalidTransition`] when `target` is zero
    /// or the index is not in [`Phase::Selected`].
    pub fn start_build(
        &mut self,
        index: &str,
        target: u64,
        at_millis: u64,
        digest: [u8; 32],
    ) -> Result<(), CoordinatorError> {
        if target == 0 || self.states.get(index).is_none_or(|state| state.phase != Phase::Selected)
        {
            return Err(CoordinatorError::InvalidTransition);
        }
        self.append(LifecycleEvent {
            sequence: 0,
            index: index.to_owned(),
            phase: Phase::Building,
            watermark: Some(target),
            target: Some(target),
            evidence_digest: digest,
            at_millis,
        })
    }
    /// Applies one contiguous backfill batch and journals its watermark.
    ///
    /// # Errors
    ///
    /// Returns [`CoordinatorError::InvalidTransition`] for an unknown index,
    /// [`CoordinatorError::InvalidWatermark`] when `last` is not past
    /// coverage or exceeds the target, and [`CoordinatorError::Hook`] when
    /// the backfill batch fails.
    pub fn advance_backfill(
        &mut self,
        index: &str,
        last: u64,
        at_millis: u64,
        digest: [u8; 32],
    ) -> Result<(), CoordinatorError> {
        let state = self.states.get(index).ok_or(CoordinatorError::InvalidTransition)?;
        if state.phase != Phase::Building || last <= state.covered || last > state.target {
            return Err(CoordinatorError::InvalidWatermark);
        }
        let first = state.covered.checked_add(1).ok_or(CoordinatorError::InvalidWatermark)?;
        self.adapter.backfill_batch(index, first, last).map_err(CoordinatorError::Hook)?;
        self.append(LifecycleEvent {
            sequence: 0,
            index: index.to_owned(),
            phase: Phase::Building,
            watermark: Some(last),
            target: None,
            evidence_digest: digest,
            at_millis,
        })
    }
    /// Moves a fully covered invisible build into shadow verification.
    ///
    /// # Errors
    ///
    /// Returns [`CoordinatorError::InvalidTransition`] when the index is
    /// unknown or is not a fully covered [`Phase::Building`] build.
    pub fn begin_shadow(
        &mut self,
        index: &str,
        at_millis: u64,
        digest: [u8; 32],
    ) -> Result<(), CoordinatorError> {
        let state = self.states.get(index).ok_or(CoordinatorError::InvalidTransition)?;
        if state.phase != Phase::Building || state.covered != state.target {
            return Err(CoordinatorError::InvalidTransition);
        }
        let covered = state.covered;
        self.append(LifecycleEvent {
            sequence: 0,
            index: index.to_owned(),
            phase: Phase::Shadowing,
            watermark: Some(covered),
            target: None,
            evidence_digest: digest,
            at_millis,
        })
    }
    /// Performs the final same-transaction catch-up and publishes visibility.
    ///
    /// # Errors
    ///
    /// Returns [`CoordinatorError::InvalidTransition`] when the index is not
    /// a fully covered [`Phase::Shadowing`] build,
    /// [`CoordinatorError::Hook`] when catch-up or visibility fails, and
    /// [`CoordinatorError::InvalidWatermark`] when catch-up falls short.
    pub fn activate(
        &mut self,
        index: &str,
        at_millis: u64,
        digest: [u8; 32],
    ) -> Result<(), CoordinatorError> {
        let state = self.states.get(index).ok_or(CoordinatorError::InvalidTransition)?;
        if state.phase != Phase::Shadowing || state.covered != state.target {
            return Err(CoordinatorError::InvalidTransition);
        }
        let covered = self
            .adapter
            .catch_up_and_verify(index, state.covered, state.target)
            .map_err(CoordinatorError::Hook)?;
        if covered < state.target {
            return Err(CoordinatorError::InvalidWatermark);
        }
        self.adapter.set_visible(index, true).map_err(CoordinatorError::Hook)?;
        self.append(LifecycleEvent {
            sequence: 0,
            index: index.to_owned(),
            phase: Phase::Active,
            watermark: Some(covered),
            target: None,
            evidence_digest: digest,
            at_millis,
        })
    }
    /// Installs a baseline pin for one exact normalized template.  Pins are
    /// planner state, never a global disable, and therefore cannot expose a
    /// regressed plan for an unrelated query.
    ///
    /// # Errors
    ///
    /// Returns [`CoordinatorError::InvalidTransition`] when the index is not
    /// a visible [`Phase::Active`] index, and [`CoordinatorError::Hook`]
    /// when the planner refuses the pin.
    pub fn pin_template(&mut self, template: &str, index: &str) -> Result<(), CoordinatorError> {
        let state = self.states.get(index).ok_or(CoordinatorError::InvalidTransition)?;
        if state.phase != Phase::Active || !state.visible {
            return Err(CoordinatorError::InvalidTransition);
        }
        self.adapter.set_plan_pin(template, index, true).map_err(CoordinatorError::Hook)
    }
    /// Hides active indexes that have not been used for the retirement window.
    /// The physical drop remains delayed behind [`DROP_GRACE_MILLIS`].
    pub fn retire_unused(&mut self, now_millis: u64, digest: [u8; 32]) -> Vec<String> {
        let candidates: Vec<String> = self
            .states
            .iter()
            .filter(|(_, state)| {
                state.phase == Phase::Active
                    && state.visible
                    && state.last_used_millis != 0
                    && now_millis.saturating_sub(state.last_used_millis) >= RETIREMENT_MILLIS
            })
            .map(|(id, _)| id.clone())
            .collect();
        let mut retired = Vec::new();
        for index in candidates {
            if self.adapter.set_visible(&index, false).is_err() {
                continue;
            }
            if self
                .append(LifecycleEvent {
                    sequence: 0,
                    index: index.clone(),
                    phase: Phase::Invisible,
                    watermark: self.states.get(&index).map(|state| state.covered),
                    target: None,
                    evidence_digest: digest,
                    at_millis: now_millis,
                })
                .is_ok()
            {
                retired.push(index);
            }
        }
        retired
    }
    /// Retires the narrow side of a verified prefix merge.  The caller must
    /// supply evidence from shadow replay; refusing weak evidence prevents a
    /// catalog-count optimization from becoming a query regression.
    ///
    /// # Errors
    ///
    /// Returns [`CoordinatorError::InvalidTransition`] for identical indexes,
    /// non-finite or weak evidence, or non-visible [`Phase::Active`] inputs,
    /// and [`CoordinatorError::Hook`] when hiding the narrow index fails.
    pub fn merge_prefix(
        &mut self,
        narrow: &str,
        wider: &str,
        read_improvement: f64,
        write_delta: f64,
        at_millis: u64,
        digest: [u8; 32],
    ) -> Result<(), CoordinatorError> {
        if narrow == wider
            || !read_improvement.is_finite()
            || !write_delta.is_finite()
            || read_improvement < REGRESSION_FRACTION
            || write_delta > REGRESSION_FRACTION
        {
            return Err(CoordinatorError::InvalidTransition);
        }
        let narrow_state = self.states.get(narrow).ok_or(CoordinatorError::InvalidTransition)?;
        let wider_state = self.states.get(wider).ok_or(CoordinatorError::InvalidTransition)?;
        if narrow_state.phase != Phase::Active
            || wider_state.phase != Phase::Active
            || !narrow_state.visible
            || !wider_state.visible
        {
            return Err(CoordinatorError::InvalidTransition);
        }
        let covered = narrow_state.covered;
        self.adapter.set_visible(narrow, false).map_err(CoordinatorError::Hook)?;
        self.append(LifecycleEvent {
            sequence: 0,
            index: narrow.to_owned(),
            phase: Phase::Invisible,
            watermark: Some(covered),
            target: None,
            evidence_digest: digest,
            at_millis,
        })
    }
    /// Makes an active index invisible immediately and journals the rollback.
    ///
    /// # Errors
    ///
    /// Returns [`CoordinatorError::InvalidTransition`] when the index is not
    /// [`Phase::Active`], and [`CoordinatorError::Hook`] when hiding fails.
    pub fn rollback(
        &mut self,
        index: &str,
        at_millis: u64,
        digest: [u8; 32],
    ) -> Result<(), CoordinatorError> {
        let state = self.states.get(index).ok_or(CoordinatorError::InvalidTransition)?;
        if state.phase != Phase::Active {
            return Err(CoordinatorError::InvalidTransition);
        }
        let covered = state.covered;
        self.adapter.set_visible(index, false).map_err(CoordinatorError::Hook)?;
        self.append(LifecycleEvent {
            sequence: 0,
            index: index.to_owned(),
            phase: Phase::Invisible,
            watermark: Some(covered),
            target: None,
            evidence_digest: digest,
            at_millis,
        })
    }
    /// Drops an invisible index only after the 24-hour grace period.
    ///
    /// # Errors
    ///
    /// Returns [`CoordinatorError::InvalidTransition`] when the index was
    /// never hidden or the grace period has not elapsed, and
    /// [`CoordinatorError::Hook`] when the physical drop fails.
    pub fn retire(
        &mut self,
        index: &str,
        now_millis: u64,
        digest: [u8; 32],
    ) -> Result<(), CoordinatorError> {
        let state = self.states.get(index).ok_or(CoordinatorError::InvalidTransition)?;
        if state.phase != Phase::Invisible || state.visible {
            return Err(CoordinatorError::InvalidTransition);
        }
        let since = state.invisible_since_millis.ok_or(CoordinatorError::InvalidTransition)?;
        if now_millis.saturating_sub(since) < DROP_GRACE_MILLIS {
            return Err(CoordinatorError::InvalidTransition);
        }
        self.adapter.drop_physical(index).map_err(CoordinatorError::Hook)?;
        let covered = state.covered;
        self.append(LifecycleEvent {
            sequence: 0,
            index: index.to_owned(),
            phase: Phase::Abandoned,
            watermark: Some(covered),
            target: None,
            evidence_digest: digest,
            at_millis: now_millis,
        })
    }
}

#[cfg(test)]
mod tests;
