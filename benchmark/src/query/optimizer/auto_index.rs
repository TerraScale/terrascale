//! The ADR 0045 auto-indexing engine skeleton: observe and candidates (E-4b).
//!
//! This module implements the first two stages of the loop in ADR 0045
//! `0045-auto-indexing-engine.md`:
//!
//! ```text
//! observe ──▶ compress ──▶ candidates ──▶ compile-check ──▶ select (bandit)
//!    ▲                                                          │
//!    │                                                          ▼
//!  monitor ◀── active ◀── verify (shadow) ◀── build (invisible, budgeted)
//! ```
//!
//! Only observe (section 3.1), compress (3.2), and candidates (3.3) live here.
//! The later stages — hypothetical-index compile check (3.4), the contextual
//! bandit with the write budget (3.5), invisible budgeted build with the
//! watermark (3.6), shadow verification with the Welch t-test and plan pins
//! (3.7), monitor/revert (3.8), and retire/merge (3.9) — are named as frozen
//! constants in [`later`] so the follow-up lanes share one vocabulary, but
//! they have no logic yet. Nothing here changes planning: the existing
//! first-query optimizer in the parent module stays the serving path until
//! the bandit lane wires selection behind the same fail-closed guards.
//!
//! # Observe
//!
//! Every execution records a [`TimedObservation`]: the [`Template`] (literals
//! stripped), the collection, the [`LogicalCost`] (pages read plus rows
//! examined), the rows returned, and which index served it. [`WorkloadObserver`]
//! keeps a bounded in-memory ring per database; the durable flush with the
//! usage-fact wave (D-42) is a later lane and intentionally absent.
//!
//! Per extracted column the write path maintains a [`ColumnSketch`]: a
//! [`DistinctSketch`] (`HyperLogLog`, replacing `ANALYZE` distinct counts) and
//! a [`ValueHistogram`] (a small capped frequency map standing in for the
//! equi-depth histogram). Both update from values the write path already has
//! in hand; neither touches storage.
//!
//! # Compress
//!
//! [`CompressConfig`] carries the sliding window (default 24 h) and the
//! coverage (default 95 %). [`tuned_workload`] weights each template by its
//! total logical cost in the window (`frequency × average cost`, ISUM-style
//! at template granularity) and, per collection, keeps the heaviest prefix
//! covering the configured share. The rest are ignored until they climb.
//!
//! # Candidates
//!
//! [`candidates_for_template`] applies the classic column order — equality
//! predicates first, then the single range predicate, then `ORDER BY` keys,
//! then projected columns as the covering variant — and [`extend_candidate`]
//! grows the covering tail with Extend's rule (add the attribute with the
//! best estimated benefit per index byte, stop when the marginal gain drops
//! below 5 %). Full-text `search` proposes the FTS kind; JSON-path predicates
//! on non-extracted fields set [`Candidate::needs_descriptor`]. Access-rule
//! candidates carry [`CandidateSource::AccessRule`] and skip the bandit. The
//! per-collection set is capped at [`MAX_CANDIDATES_PER_COLLECTION`]; the
//! [`CandidateSet`] evicts the oldest-unused entry past the cap.
//!
//! All estimators in this skeleton are deliberately simple and documented as
//! such: the bandit lane replaces estimated benefit with measured logical
//! work saved, and the compile-check lane replaces the width heuristic with
//! scratch-schema statistics. What is frozen here is the shape of the inputs
//! those lanes consume, not the numbers.

use std::collections::{BTreeMap, BTreeSet, VecDeque};
use std::time::{Duration, Instant};

use crate::operations::RequestValidationError;
use crate::types::DocumentValue;

/// The bounded in-memory observation ring per database (ADR 0045 section 3.1).
///
/// One observer instance owns one database's ring. The engine will own one
/// observer per database later; the flush with the usage-fact wave (D-42) is
/// a follow-up lane.
pub const OBSERVE_RING_CAPACITY: usize = 4096;

/// The number of `HyperLogLog` registers in a [`DistinctSketch`].
///
/// Sixty-four registers fit in 64 bytes and give a standard error of
/// `1.04 / sqrt(64) ≈ 13 %`, enough for a selectivity prior. The bandit lane
/// replaces the estimate with measured rewards; the sketch only has to order
/// candidates, not price them exactly.
pub const SKETCH_HLL_REGISTERS: usize = 64;

/// The maximum distinct value keys a [`ValueHistogram`] retains.
///
/// A small capped map stands in for the equi-depth histogram of section 3.1:
/// enough to estimate equality selectivity for the candidate ordering, small
/// enough to stay engine state. Past the cap the least-frequent key is
/// evicted.
pub const SKETCH_HISTOGRAM_KEYS: usize = 128;

/// The maximum candidates retained per collection (ADR 0045 section 3.3).
pub const MAX_CANDIDATES_PER_COLLECTION: usize = 32;

/// The Extend stop rule (ADR 0045 section 3.3): stop growing a composite when
/// the marginal gain drops below 5 %.
pub const EXTEND_MIN_GAIN_FRACTION: f64 = 0.05;

/// Later-stage frozen vocabulary (ADR 0045 sections 3.4-3.9).
///
/// These constants name the thresholds the follow-up lanes implement so the
/// skeleton and those lanes share one vocabulary. They are not enforced yet.
pub mod later {
    /// The write budget (section 3.5): active-index maintenance on a
    /// collection stays under 2× the base write cost.
    pub const WRITE_BUDGET_MULTIPLE: f64 = 2.0;
    /// The per-collection visible-index cap (section 3.5, typed error names
    /// the cheapest index to drop).
    pub const MAX_VISIBLE_PER_COLLECTION: usize = 12;
    /// Shadow verification (section 3.7): executions required per side of the
    /// Welch t-test on logical cost.
    pub const SHADOW_MIN_EXECUTIONS: usize = 20;
    /// Shadow verification (section 3.7): at least one template must improve
    /// by this fraction to activate.
    pub const SHADOW_MIN_IMPROVEMENT_FRACTION: f64 = 0.20;
    /// Shadow verification (section 3.7): each shadow execution is capped at
    /// past-average logical cost times this multiple (plus a floor).
    pub const SHADOW_COST_CAP_MULTIPLE: f64 = 1.5;
    /// Failed (template set, candidate) pairs are blocklisted this long
    /// (section 3.7).
    pub const BLOCKLIST_DAYS: u64 = 7;
    /// Revert (section 3.8): the physical drop follows the invisible flag
    /// flip after this delay, so a false alarm costs nothing.
    pub const REVERT_DROP_DELAY_HOURS: u64 = 24;
    /// Retirement (section 3.9): an index unused by any template for this
    /// window is made invisible, then dropped.
    pub const RETIRE_UNUSED_DAYS: u64 = 7;
}

/// The predicate class of one template predicate.
///
/// Literals are stripped: two queries differing only in bound values share a
/// template. Ordering keys are recorded separately on the template.
#[derive(Clone, Copy, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub enum PredicateKind {
    /// Equality-shaped: `Equals`, `In`, `AllIn`, `Exists`.
    Equality,
    /// Range-shaped: `GreaterThan`, `GreaterOrEqual`, `LessThan`,
    /// `LessOrEqual`, `Between`. At most one range column enters the btree
    /// prefix (section 3.3).
    Range,
    /// Full-text `Search`: proposes the FTS kind, never a btree column.
    FullText,
    /// Everything else (`NotEquals`, `Missing`, pattern matches): not
    /// index-orderable in this skeleton, carried so compression still weights
    /// the template.
    Other,
}

/// One normalized predicate of a [`Template`]: the field and its class.
#[derive(Clone, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub struct TemplatePredicate {
    /// The validated field name.
    field: String,
    /// The predicate class with literals stripped.
    kind: PredicateKind,
}

impl TemplatePredicate {
    /// Creates one template predicate.
    ///
    /// # Errors
    ///
    /// Returns [`RequestValidationError::EmptyField`] for an empty field and
    /// [`RequestValidationError::InvalidQuery`] for an invalid one, mirroring
    /// the query seam's field-name rule.
    pub fn try_new(field: &str, kind: PredicateKind) -> Result<Self, RequestValidationError> {
        crate::query::spec::validate_field_name(field)?;
        Ok(Self { field: field.to_owned(), kind })
    }

    /// Returns the predicate field.
    #[must_use]
    pub fn field(&self) -> &str {
        &self.field
    }

    /// Returns the predicate class.
    #[must_use]
    pub const fn kind(&self) -> PredicateKind {
        self.kind
    }
}

/// A workload template: one SQL statement with literals replaced by
/// parameters (ADR 0045 section 2).
///
/// The unit of workload for compression and candidates. Collection plus the
/// predicate classes in declaration order, the ordering keys in declaration
/// order, and the projected fields sorted (projection order does not change
/// the shape, predicate and ordering declaration order do).
#[derive(Clone, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub struct Template {
    /// The queried collection.
    collection: String,
    /// The predicates in declaration order, literals stripped.
    predicates: Vec<TemplatePredicate>,
    /// The ordering keys in declaration order.
    ordering: Vec<String>,
    /// The projected fields sorted, or `None` for whole-document reads.
    projection: Option<Vec<String>>,
}

impl Template {
    /// Creates one template from validated parts.
    ///
    /// # Errors
    ///
    /// Returns the field-name validation errors for an invalid collection,
    /// predicate field, ordering key, or projected field.
    pub fn try_new(
        collection: &str,
        predicates: Vec<TemplatePredicate>,
        ordering: Vec<String>,
        projection: Option<Vec<String>>,
    ) -> Result<Self, RequestValidationError> {
        crate::query::spec::validate_field_name(collection)?;
        for key in &ordering {
            crate::query::spec::validate_field_name(key)?;
        }
        if let Some(fields) = &projection {
            for field in fields {
                crate::query::spec::validate_field_name(field)?;
            }
        }
        let mut projection = projection;
        if let Some(fields) = projection.as_mut() {
            fields.sort();
            fields.dedup();
        }
        Ok(Self { collection: collection.to_owned(), predicates, ordering, projection })
    }

    /// Derives the template of one normalized query, stripping literals.
    ///
    /// Crate-only: the normalized model is crate-private, so external
    /// callers build templates through [`Self::try_new`]. The execution-hook
    /// lane calls this on every execution; until that lane lands, the
    /// in-crate test below is the only caller.
    #[allow(dead_code, reason = "wired by the execution-hook lane; exercised by tests")]
    pub(crate) fn of(query: &crate::query::model::NormalizedQuery) -> Self {
        use crate::query::spec::QueryCondition;
        let collection = query
            .source()
            .collection()
            .map_or_else(|| "\u{0}family".to_owned(), |name| name.as_str().to_owned());
        let mut predicates = Vec::new();
        for condition in query.predicate().conditions() {
            let (field, kind) = match condition {
                QueryCondition::Equals { field, .. }
                | QueryCondition::In { field, .. }
                | QueryCondition::AllIn { field, .. }
                | QueryCondition::Exists { field } => (field.as_str(), PredicateKind::Equality),
                QueryCondition::GreaterThan { field, .. }
                | QueryCondition::GreaterOrEqual { field, .. }
                | QueryCondition::LessThan { field, .. }
                | QueryCondition::LessOrEqual { field, .. }
                | QueryCondition::Between { field, .. } => (field.as_str(), PredicateKind::Range),
                QueryCondition::Search { field, .. } => (field.as_str(), PredicateKind::FullText),
                QueryCondition::NotEquals { field, .. }
                | QueryCondition::Missing { field, .. }
                | QueryCondition::StartsWith { field, .. }
                | QueryCondition::EndsWith { field, .. }
                | QueryCondition::Contains { field, .. } => (field.as_str(), PredicateKind::Other),
            };
            // Fields come from a validated query, so construction cannot fail.
            if let Ok(predicate) = TemplatePredicate::try_new(field, kind) {
                predicates.push(predicate);
            }
        }
        let ordering =
            query.ordering().keys().iter().map(|key| key.field().to_owned()).collect::<Vec<_>>();
        let projection = query.projection().selected().map(|fields| {
            let mut sorted = fields.to_vec();
            sorted.sort();
            sorted.dedup();
            sorted
        });
        Self { collection, predicates, ordering, projection }
    }

    /// Returns the templated collection.
    #[must_use]
    pub fn collection(&self) -> &str {
        &self.collection
    }

    /// Returns the template predicates in declaration order.
    #[must_use]
    pub fn predicates(&self) -> &[TemplatePredicate] {
        &self.predicates
    }

    /// Returns the ordering keys in declaration order.
    #[must_use]
    pub fn ordering(&self) -> &[String] {
        &self.ordering
    }

    /// Returns the projected fields sorted, or `None` for whole-document reads.
    #[must_use]
    pub fn projection(&self) -> Option<&[String]> {
        self.projection.as_deref()
    }

    /// Reports whether the template carries a full-text predicate.
    #[must_use]
    pub fn has_full_text(&self) -> bool {
        self.predicates.iter().any(|predicate| predicate.kind() == PredicateKind::FullText)
    }
}

/// The logical cost of one execution (ADR 0045 section 2).
///
/// Pages read plus rows examined, from `sqlite3_stmt_status` once the storage
/// hook lands — never wall time (Azure's lesson: logical metrics have low
/// variance and track plan quality). The skeleton carries both counters so
/// the later hook fills pages without changing the shape.
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct LogicalCost {
    /// Pages read.
    pages_read: u64,
    /// Rows examined.
    rows_examined: u64,
}

impl LogicalCost {
    /// Creates one logical cost.
    #[must_use]
    pub const fn new(pages_read: u64, rows_examined: u64) -> Self {
        Self { pages_read, rows_examined }
    }

    /// Returns pages read.
    #[must_use]
    pub const fn pages_read(self) -> u64 {
        self.pages_read
    }

    /// Returns rows examined.
    #[must_use]
    pub const fn rows_examined(self) -> u64 {
        self.rows_examined
    }

    /// Returns the total logical cost: pages read plus rows examined.
    #[must_use]
    pub const fn total(self) -> u64 {
        self.pages_read.saturating_add(self.rows_examined)
    }
}

/// One observed execution (ADR 0045 section 3.1).
#[derive(Clone, Debug, PartialEq)]
pub struct TimedObservation {
    /// The stripped template.
    template: Template,
    /// The logical cost of the execution.
    cost: LogicalCost,
    /// Rows returned.
    rows_returned: u64,
    /// The serving index name, or `None` for a scan.
    index_used: Option<String>,
    /// When the execution was observed (monotonic clock).
    observed_at: Instant,
}

impl TimedObservation {
    /// Creates one observation.
    #[must_use]
    pub const fn new(
        template: Template,
        cost: LogicalCost,
        rows_returned: u64,
        index_used: Option<String>,
        observed_at: Instant,
    ) -> Self {
        Self { template, cost, rows_returned, index_used, observed_at }
    }

    /// Returns the observed template.
    #[must_use]
    pub const fn template(&self) -> &Template {
        &self.template
    }

    /// Returns the logical cost.
    #[must_use]
    pub const fn cost(&self) -> LogicalCost {
        self.cost
    }

    /// Returns rows returned.
    #[must_use]
    pub const fn rows_returned(&self) -> u64 {
        self.rows_returned
    }

    /// Returns the serving index name, or `None` for a scan.
    #[must_use]
    pub fn index_used(&self) -> Option<&str> {
        self.index_used.as_deref()
    }

    /// Returns when the execution was observed.
    #[must_use]
    pub const fn observed_at(&self) -> Instant {
        self.observed_at
    }
}

/// The bounded in-memory workload ring plus per-column sketches.
///
/// One instance owns one database's observe state. Pushing an execution
/// records the template observation and evicts the oldest entry past
/// [`OBSERVE_RING_CAPACITY`]. Pushing a write updates the column's
/// [`ColumnSketch`] from the value already in hand.
#[derive(Debug, Default)]
pub struct WorkloadObserver {
    /// The observation ring, oldest first.
    ring: VecDeque<TimedObservation>,
    /// Per `(collection, field)` sketches.
    sketches: BTreeMap<(String, String), ColumnSketch>,
}

impl WorkloadObserver {
    /// Creates an empty observer.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// Records one execution, evicting the oldest entry past the ring bound.
    pub fn observe_execution(&mut self, observation: TimedObservation) {
        self.ring.push_back(observation);
        while self.ring.len() > OBSERVE_RING_CAPACITY {
            self.ring.pop_front();
        }
    }

    /// Records one written value for its column sketch.
    pub fn observe_write(&mut self, collection: &str, field: &str, value: &DocumentValue) {
        let key = (collection.to_owned(), field.to_owned());
        self.sketches.entry(key).or_default().observe(value);
    }

    /// Returns the number of retained observations.
    #[must_use]
    pub fn len(&self) -> usize {
        self.ring.len()
    }

    /// Reports whether the ring is empty.
    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.ring.is_empty()
    }

    /// Returns the retained observations, oldest first.
    #[must_use]
    pub fn observations(&self) -> Vec<&TimedObservation> {
        self.ring.iter().collect()
    }

    /// Returns the sketch of one column, when any write was observed.
    #[must_use]
    pub fn sketch(&self, collection: &str, field: &str) -> Option<&ColumnSketch> {
        self.sketches.get(&(collection.to_owned(), field.to_owned()))
    }

    /// Returns the sketches in key order.
    #[must_use]
    pub fn sketches(&self) -> &BTreeMap<(String, String), ColumnSketch> {
        &self.sketches
    }

    /// Drains observations at or before the watermark, oldest first.
    ///
    /// The durable flush with the usage-fact wave (D-42) will drain through
    /// here; until then this is the bounded handoff tests drive.
    pub fn drain_flushed(&mut self) -> Vec<TimedObservation> {
        std::mem::take(&mut self.ring).into()
    }
}

/// Mixes one byte into a running FNV-1a 64-bit hash.
const fn mix_byte(hash: u64, byte: u8) -> u64 {
    const FNV_PRIME: u64 = 0x0000_0100_0000_01b3;
    (hash ^ (byte as u64)).wrapping_mul(FNV_PRIME)
}

/// Hashes bytes with FNV-1a 64 (identity fragment, not a security claim).
fn fnv1a64(bytes: &[u8]) -> u64 {
    const FNV_OFFSET: u64 = 0xcbf2_9ce4_8422_2325;
    let mut hash = FNV_OFFSET;
    for byte in bytes {
        hash = mix_byte(hash, *byte);
    }
    hash
}

/// Returns the canonical sketch key of one document value.
///
/// Deterministic across processes: type-tagged so no two value shapes share
/// a key. Floats hash by bits so `NaN` is stable.
fn canonical_value_key(value: &DocumentValue) -> String {
    match value {
        DocumentValue::Null => "null".to_owned(),
        DocumentValue::Boolean(flag) => format!("boolean:{flag}"),
        DocumentValue::Integer(number) => format!("integer:{number}"),
        DocumentValue::Number(number) => format!("number:{}", number.to_bits()),
        DocumentValue::String(text) => format!("string:{text}"),
        DocumentValue::Array(elements) => {
            let inner = elements.iter().map(canonical_value_key).collect::<Vec<_>>().join(",");
            format!("array:[{inner}]")
        }
        DocumentValue::Object(fields) => {
            let inner = fields
                .iter()
                .map(|(name, item)| format!("{name}={}", canonical_value_key(item)))
                .collect::<Vec<_>>()
                .join(",");
            format!("object:{{{inner}}}")
        }
    }
}

/// A `HyperLogLog` distinct-count sketch (ADR 0045 section 3.1).
///
/// Replaces `ANALYZE` distinct counts on the hot path. Sixty-four registers,
/// FNV-1a 64 hashed keys, standard raw estimate with small-range correction.
/// Standard error is about 13 %: a prior for candidate ordering, never a
/// correctness input.
#[derive(Clone, Debug)]
pub struct DistinctSketch {
    /// One register per bucket: the maximum observed rank.
    registers: Vec<u8>,
    /// Values added (for the linear-counting small-range correction).
    count: u64,
}

impl Default for DistinctSketch {
    fn default() -> Self {
        Self { registers: vec![0; SKETCH_HLL_REGISTERS], count: 0 }
    }
}

impl DistinctSketch {
    /// Creates an empty sketch.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// Adds one document value to the sketch.
    pub fn add_value(&mut self, value: &DocumentValue) {
        let key = canonical_value_key(value);
        self.add_bytes(key.as_bytes());
    }

    /// Adds pre-hashed bytes to the sketch.
    pub fn add_bytes(&mut self, bytes: &[u8]) {
        if self.registers.is_empty() {
            return;
        }
        let hash = fnv1a64(bytes);
        let index = (hash & 63) as usize % self.registers.len();
        let rest = hash >> 6;
        // The top six bits are zero after the shift, so the leading-zero
        // count is at least six; the rank is the position of the first
        // one-bit in the remaining 58 bits, one-based.
        let rank = rest.leading_zeros().saturating_sub(6).saturating_add(1).min(64) as u8;
        self.registers[index] = self.registers[index].max(rank.max(1));
        self.count = self.count.saturating_add(1);
    }

    /// Estimates the distinct count.
    ///
    /// The `u64`-to-`f64` conversions are a precision trade the sketch owns:
    /// the estimate is a candidate-ordering prior with ~13 % standard error,
    /// so mantissa rounding is noise beside the estimator's own error.
    #[allow(clippy::cast_precision_loss, reason = "sketch prior; error dominates rounding")]
    #[must_use]
    pub fn estimate(&self) -> f64 {
        // Bias constant for m = 64 (Flajolet et al.).
        const ALPHA_64: f64 = 0.673;
        let registers = self.registers.len() as f64;
        if registers == 0.0 {
            return 0.0;
        }
        let mut harmonic = 0.0;
        let mut zeros = 0_u64;
        for register in &self.registers {
            harmonic += 2.0_f64.powi(-i32::from(*register));
            if *register == 0 {
                zeros += 1;
            }
        }
        // Small-range correction: linear counting while many registers are
        // still zero.
        let raw = ALPHA_64 * registers * registers / harmonic;
        // Small-range correction: linear counting while many registers are
        // still zero.
        if raw <= 2.5 * registers && zeros > 0 {
            registers * (registers / zeros as f64).ln()
        } else {
            raw
        }
    }

    /// Returns values added.
    #[must_use]
    pub const fn count(&self) -> u64 {
        self.count
    }
}

/// A small capped frequency map standing in for the equi-depth histogram
/// (ADR 0045 section 3.1).
///
/// Keys are canonical value keys in sort order, so range-adjacent values sit
/// adjacently; the full equi-depth bucketing lands with the storage
/// statistics hook. Past [`SKETCH_HISTOGRAM_KEYS`] distinct keys the
/// least-frequent key is evicted.
#[derive(Clone, Debug, Default)]
pub struct ValueHistogram {
    /// Per-key frequencies in key order.
    counts: BTreeMap<String, u64>,
    /// Total values observed.
    total: u64,
}

impl ValueHistogram {
    /// Creates an empty histogram.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// Observes one value.
    pub fn observe(&mut self, value: &DocumentValue) {
        let key = canonical_value_key(value);
        let count = self.counts.get(&key).copied().unwrap_or(0).saturating_add(1);
        self.counts.insert(key, count);
        self.total = self.total.saturating_add(1);
        while self.counts.len() > SKETCH_HISTOGRAM_KEYS {
            let victim =
                self.counts.iter().min_by_key(|(_, count)| **count).map(|(key, _)| key.clone());
            match victim {
                Some(key) => {
                    self.counts.remove(&key);
                }
                None => break,
            }
        }
    }

    /// Returns total values observed.
    #[must_use]
    pub const fn total(&self) -> u64 {
        self.total
    }

    /// Returns distinct keys retained.
    #[must_use]
    pub fn distinct(&self) -> usize {
        self.counts.len()
    }

    /// Returns the retained frequency of one value, or `None` when evicted
    /// or never observed.
    #[must_use]
    pub fn frequency(&self, value: &DocumentValue) -> Option<u64> {
        self.counts.get(&canonical_value_key(value)).copied()
    }

    /// Estimates equality selectivity: retained frequency over the total, or
    /// one over the `HyperLogLog` distinct estimate when the key is absent.
    ///
    /// The count-to-float conversions feed a ranking prior, never an exact
    /// accounting input; rounding is noise beside the estimator's error.
    #[allow(clippy::cast_precision_loss, reason = "selectivity prior; error dominates rounding")]
    #[must_use]
    pub fn selectivity_equality(&self, value: &DocumentValue, distinct: f64) -> f64 {
        if self.total == 0 {
            return 1.0;
        }
        match self.frequency(value) {
            Some(seen) => (seen as f64 / self.total as f64).clamp(0.0, 1.0),
            None => {
                if distinct > 1.0 {
                    (1.0 / distinct).clamp(0.0, 1.0)
                } else {
                    1.0
                }
            }
        }
    }
}

/// Per-column observe state: the distinct sketch plus the value histogram.
#[derive(Clone, Debug, Default)]
pub struct ColumnSketch {
    /// The distinct-count sketch.
    distinct: DistinctSketch,
    /// The value histogram.
    histogram: ValueHistogram,
}

impl ColumnSketch {
    /// Creates an empty sketch.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// Observes one written value in both structures.
    pub fn observe(&mut self, value: &DocumentValue) {
        self.distinct.add_value(value);
        self.histogram.observe(value);
    }

    /// Returns the distinct-count sketch.
    #[must_use]
    pub const fn distinct(&self) -> &DistinctSketch {
        &self.distinct
    }

    /// Returns the value histogram.
    #[must_use]
    pub const fn histogram(&self) -> &ValueHistogram {
        &self.histogram
    }

    /// Estimates the distinct value count.
    #[must_use]
    pub fn estimate_distinct(&self) -> f64 {
        self.distinct.estimate()
    }
}

/// The compression configuration (ADR 0045 section 3.2).
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct CompressConfig {
    /// The sliding window: observations older than this are ignored.
    window: Duration,
    /// The weight share the tuned workload must cover per collection.
    coverage: f64,
}

impl CompressConfig {
    /// The ADR default: a 24 h decayed window covering 95 % of weight.
    pub const DEFAULT: Self = Self { window: Duration::from_hours(24), coverage: 0.95 };

    /// Creates one configuration.
    ///
    /// # Errors
    ///
    /// Returns [`RequestValidationError::InvalidQuery`] when the window is
    /// zero or the coverage is outside `(0, 1]`.
    pub fn try_new(window: Duration, coverage: f64) -> Result<Self, RequestValidationError> {
        if window.is_zero() || !(0.0 < coverage && coverage <= 1.0) {
            return Err(RequestValidationError::InvalidQuery);
        }
        Ok(Self { window, coverage })
    }

    /// Returns the sliding window.
    #[must_use]
    pub const fn window(&self) -> Duration {
        self.window
    }

    /// Returns the coverage share.
    #[must_use]
    pub const fn coverage(&self) -> f64 {
        self.coverage
    }
}

impl Default for CompressConfig {
    fn default() -> Self {
        Self::DEFAULT
    }
}

/// One compressed template: the template with its decayed weight.
#[derive(Clone, Debug, PartialEq)]
pub struct WeightedTemplate {
    /// The template.
    template: Template,
    /// Executions in the window.
    executions: u64,
    /// Total logical cost in the window (`frequency × average cost`).
    total_cost: u64,
    /// Average logical cost per execution.
    average_cost: f64,
    /// The compression weight (today: total cost; decay lands with the
    /// timestamped wave).
    weight: f64,
}

impl WeightedTemplate {
    /// Returns the template.
    #[must_use]
    pub const fn template(&self) -> &Template {
        &self.template
    }

    /// Returns executions in the window.
    #[must_use]
    pub const fn executions(&self) -> u64 {
        self.executions
    }

    /// Returns total logical cost in the window.
    #[must_use]
    pub const fn total_cost(&self) -> u64 {
        self.total_cost
    }

    /// Returns average logical cost per execution.
    #[must_use]
    pub const fn average_cost(&self) -> f64 {
        self.average_cost
    }

    /// Returns the compression weight.
    #[must_use]
    pub const fn weight(&self) -> f64 {
        self.weight
    }
}

/// Compresses observations into the tuned workload (ADR 0045 section 3.2).
///
/// Templates are weighted by total logical cost inside the sliding window.
/// Per collection the heaviest prefix covering [`CompressConfig::coverage`]
/// of that collection's weight is returned, heaviest first; the rest are
/// ignored until they climb. Empty input yields an empty map.
///
/// Cost-to-float conversions feed a ranking weight, never exact accounting.
#[allow(clippy::cast_precision_loss, reason = "compression weight; rounding is noise")]
#[must_use]
pub fn tuned_workload(
    observations: &[TimedObservation],
    now: Instant,
    config: CompressConfig,
) -> BTreeMap<String, Vec<WeightedTemplate>> {
    let mut grouped: BTreeMap<Template, (u64, u64)> = BTreeMap::new();
    for observation in observations {
        if observation.observed_at() > now {
            continue;
        }
        if now.duration_since(observation.observed_at()) > config.window() {
            continue;
        }
        let entry = grouped.entry(observation.template().clone()).or_insert((0, 0));
        entry.0 = entry.0.saturating_add(1);
        entry.1 = entry.1.saturating_add(observation.cost().total());
    }
    let mut per_collection: BTreeMap<String, Vec<WeightedTemplate>> = BTreeMap::new();
    for (template, (executions, total_cost)) in grouped {
        if executions == 0 {
            continue;
        }
        let average_cost = total_cost as f64 / executions as f64;
        let weighted = WeightedTemplate {
            template: template.clone(),
            executions,
            total_cost,
            average_cost,
            weight: total_cost as f64,
        };
        per_collection.entry(template.collection().to_owned()).or_default().push(weighted);
    }
    for templates in per_collection.values_mut() {
        templates.sort_by(|left, right| {
            right.weight().partial_cmp(&left.weight()).unwrap_or(std::cmp::Ordering::Equal)
        });
        let total: f64 = templates.iter().map(WeightedTemplate::weight).sum();
        if total <= 0.0 {
            continue;
        }
        let mut covered = 0.0;
        let mut keep = 0_usize;
        for candidate in templates.iter() {
            covered += candidate.weight();
            keep += 1;
            if covered / total >= config.coverage() {
                break;
            }
        }
        keep = keep.max(1).min(templates.len());
        templates.truncate(keep);
    }
    per_collection.retain(|_, templates| !templates.is_empty());
    per_collection
}

/// The role of one column inside a candidate.
#[derive(Clone, Copy, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub enum ColumnRole {
    /// An equality predicate column (leading prefix position).
    Equality,
    /// The single range predicate column (after the equality prefix).
    Range,
    /// An `ORDER BY` column (after the range position).
    Order,
    /// A projected column carried for covering (trailing position).
    Covering,
}

/// The physical kind of a candidate.
#[derive(Clone, Copy, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub enum CandidateKind {
    /// A btree over extracted columns.
    BTree,
    /// The FTS5 table for `MATCH`.
    Fts,
}

/// Why a candidate exists.
#[derive(Clone, Copy, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub enum CandidateSource {
    /// Proposed from the tuned workload; goes through the bandit.
    Workload,
    /// Mandatory from access rules (D-39); skips the bandit.
    AccessRule,
}

/// One candidate column: the field and its role.
#[derive(Clone, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub struct CandidateColumn {
    /// The indexed field.
    field: String,
    /// The column role.
    role: ColumnRole,
}

impl CandidateColumn {
    /// Creates one candidate column.
    ///
    /// # Errors
    ///
    /// Returns the field-name validation errors for an invalid field.
    pub fn try_new(field: &str, role: ColumnRole) -> Result<Self, RequestValidationError> {
        crate::query::spec::validate_field_name(field)?;
        Ok(Self { field: field.to_owned(), role })
    }

    /// Returns the field.
    #[must_use]
    pub fn field(&self) -> &str {
        &self.field
    }

    /// Returns the role.
    #[must_use]
    pub const fn role(&self) -> ColumnRole {
        self.role
    }
}

/// One index candidate (ADR 0045 section 3.3).
#[derive(Clone, Debug, PartialEq)]
pub struct Candidate {
    /// The indexed collection.
    collection: String,
    /// The columns in index order.
    columns: Vec<CandidateColumn>,
    /// The physical kind.
    kind: CandidateKind,
    /// Why the candidate exists.
    source: CandidateSource,
    /// Whether a JSON-path predicate needs a new descriptor plus the index.
    needs_descriptor: bool,
    /// The skeleton's estimated benefit per index byte (Extend orders by
    /// this; the bandit replaces it with measured rewards).
    benefit_per_byte: f64,
}

impl Candidate {
    /// Returns the indexed collection.
    #[must_use]
    pub fn collection(&self) -> &str {
        &self.collection
    }

    /// Returns the columns in index order.
    #[must_use]
    pub fn columns(&self) -> &[CandidateColumn] {
        &self.columns
    }

    /// Returns the physical kind.
    #[must_use]
    pub const fn kind(&self) -> CandidateKind {
        self.kind
    }

    /// Returns why the candidate exists.
    #[must_use]
    pub const fn source(&self) -> CandidateSource {
        self.source
    }

    /// Reports whether the candidate needs a new descriptor plus the index.
    #[must_use]
    pub const fn needs_descriptor(&self) -> bool {
        self.needs_descriptor
    }

    /// Returns the skeleton's estimated benefit per index byte.
    #[must_use]
    pub const fn benefit_per_byte(&self) -> f64 {
        self.benefit_per_byte
    }

    /// Returns the ordered field names.
    #[must_use]
    pub fn fields(&self) -> Vec<&str> {
        self.columns.iter().map(CandidateColumn::field).collect()
    }
}

/// Estimates the byte width of one candidate.
///
/// Skeleton heuristic until the compile-check lane reads scratch-schema
/// statistics: a fixed per-column overhead plus the field-name length.
/// Ordering across candidates only needs a monotone width, not an exact one.
#[must_use]
pub fn estimated_width_bytes(columns: &[CandidateColumn]) -> u64 {
    const PER_COLUMN_OVERHEAD: u64 = 16;
    columns.iter().fold(0_u64, |width, column| {
        width.saturating_add(PER_COLUMN_OVERHEAD.saturating_add(column.field().len() as u64))
    })
}

/// Estimates equality selectivity from the sketches.
///
/// A retained histogram frequency is used directly; an absent key falls back
/// to one over the `HyperLogLog` distinct estimate; a missing sketch returns
/// the neutral `0.5` so unknown columns neither dominate nor vanish.
#[must_use]
pub fn equality_selectivity(
    sketches: &BTreeMap<(String, String), ColumnSketch>,
    collection: &str,
    field: &str,
    value: &DocumentValue,
) -> f64 {
    match sketches.get(&(collection.to_owned(), field.to_owned())) {
        Some(sketch) => {
            sketch.histogram().selectivity_equality(value, sketch.estimate_distinct().max(1.0))
        }
        None => 0.5,
    }
}

/// Proposes the full-text candidate of one tuned template.
///
/// A full-text predicate proposes the FTS kind instead of a btree column;
/// the first `Search` field names the indexed text column.
fn fts_candidate_for(template: &WeightedTemplate, extracted: &BTreeSet<String>) -> Vec<Candidate> {
    let inner = template.template();
    let field = inner
        .predicates()
        .iter()
        .find(|predicate| predicate.kind() == PredicateKind::FullText)
        .map_or("body", |predicate| predicate.field());
    let needs_descriptor = !extracted.contains(field);
    let columns = CandidateColumn::try_new(field, ColumnRole::Equality)
        .map_or_else(|_| Vec::new(), |column| vec![column]);
    vec![Candidate {
        collection: inner.collection().to_owned(),
        columns,
        kind: CandidateKind::Fts,
        source: CandidateSource::Workload,
        needs_descriptor,
        benefit_per_byte: template.weight().max(1.0),
    }]
}

/// Builds the btree prefix of one template: equality predicates in template
/// order, then the first range predicate, then `ORDER BY` keys not already
/// present. Returns the prefix with the fields it covers.
fn btree_prefix_of(template: &Template) -> (Vec<CandidateColumn>, BTreeSet<String>) {
    let mut prefix: Vec<CandidateColumn> = Vec::new();
    let mut seen: BTreeSet<String> = BTreeSet::new();
    for predicate in template.predicates() {
        if predicate.kind() != PredicateKind::Equality {
            continue;
        }
        if !seen.insert(predicate.field().to_owned()) {
            continue;
        }
        if let Ok(column) = CandidateColumn::try_new(predicate.field(), ColumnRole::Equality) {
            prefix.push(column);
        }
    }
    if let Some(range) = template.predicates().iter().find(|predicate| {
        predicate.kind() == PredicateKind::Range && !seen.contains(predicate.field())
    }) {
        seen.insert(range.field().to_owned());
        if let Ok(column) = CandidateColumn::try_new(range.field(), ColumnRole::Range) {
            prefix.push(column);
        }
    }
    for key in template.ordering() {
        if !seen.insert(key.clone()) {
            continue;
        }
        if let Ok(column) = CandidateColumn::try_new(key, ColumnRole::Order) {
            prefix.push(column);
        }
    }
    (prefix, seen)
}

/// Estimates the selectivity prior of one prefix: template weight scaled so
/// selective equality columns order before weak ones. The product is a prior
/// only; measured rewards replace it.
///
/// Width-to-float conversions feed a ranking ratio, never exact accounting.
#[allow(clippy::cast_precision_loss, reason = "benefit prior; rounding is noise")]
fn selectivity_prior(
    template: &WeightedTemplate,
    sketches: &BTreeMap<(String, String), ColumnSketch>,
    prefix: &[CandidateColumn],
) -> f64 {
    let inner = template.template();
    let mut prior = 1.0_f64;
    for column in prefix {
        if column.role() == ColumnRole::Equality {
            let selectivity = equality_selectivity(
                sketches,
                inner.collection(),
                column.field(),
                &DocumentValue::Null,
            );
            prior *= 1.0 - selectivity * 0.5;
        }
    }
    prior
}

/// Builds the covering variant of one prefix when the projection adds fields
/// and the width stays cheap (at most double the base width).
///
/// Width-to-float conversions feed a ranking ratio, never exact accounting.
#[allow(clippy::cast_precision_loss, reason = "benefit prior; rounding is noise")]
fn covering_variant_of(
    template: &WeightedTemplate,
    prefix: &[CandidateColumn],
    seen: &BTreeSet<String>,
    base_width: u64,
    prior: f64,
    extracted: &BTreeSet<String>,
) -> Option<Candidate> {
    let inner = template.template();
    let projection = inner.projection()?;
    let mut covering = prefix.to_vec();
    for field in projection {
        if seen.contains(field) {
            continue;
        }
        if let Ok(column) = CandidateColumn::try_new(field, ColumnRole::Covering) {
            covering.push(column);
        }
    }
    if covering.len() <= prefix.len() {
        return None;
    }
    let width = estimated_width_bytes(&covering).max(1);
    // The covering variant is cheap when it at most doubles the base width;
    // otherwise the bandit would pay write budget for little read gain.
    if width > base_width.saturating_mul(2) {
        return None;
    }
    let benefit = template.weight() * prior.max(0.5) / width as f64;
    let needs_descriptor = covering.iter().any(|column| !extracted.contains(column.field()));
    Some(Candidate {
        collection: inner.collection().to_owned(),
        columns: covering,
        kind: CandidateKind::BTree,
        source: CandidateSource::Workload,
        needs_descriptor,
        benefit_per_byte: benefit.max(0.0),
    })
}

/// Proposes candidates for one tuned template (ADR 0045 section 3.3).
///
/// The classic column order applies: equality predicates in template order,
/// then the first range predicate, then `ORDER BY` keys not already present.
/// A full-text predicate proposes the FTS kind instead of a btree column.
/// The covering variant appends projected fields missing from the prefix when
/// the width stays cheap (at most double the base width). Fields absent from
/// `extracted` set [`Candidate::needs_descriptor`]. The result is ordered
/// base-first, covering-second.
///
/// Width-to-float conversions feed a ranking ratio, never exact accounting.
#[allow(clippy::cast_precision_loss, reason = "benefit prior; rounding is noise")]
#[must_use]
pub fn candidates_for_template(
    template: &WeightedTemplate,
    sketches: &BTreeMap<(String, String), ColumnSketch>,
    extracted: &BTreeSet<String>,
) -> Vec<Candidate> {
    let inner = template.template();
    if inner.has_full_text() {
        return fts_candidate_for(template, extracted);
    }
    let (prefix, seen) = btree_prefix_of(inner);
    if prefix.is_empty() {
        return Vec::new();
    }
    let base_width = estimated_width_bytes(&prefix).max(1);
    let prior = selectivity_prior(template, sketches, &prefix);
    let base_benefit = (template.weight() * prior / base_width as f64).max(0.0);
    let needs_descriptor = prefix.iter().any(|column| !extracted.contains(column.field()));
    let mut out = vec![Candidate {
        collection: inner.collection().to_owned(),
        columns: prefix.clone(),
        kind: CandidateKind::BTree,
        source: CandidateSource::Workload,
        needs_descriptor,
        benefit_per_byte: base_benefit,
    }];
    if let Some(covering) =
        covering_variant_of(template, &prefix, &seen, base_width, prior, extracted)
    {
        out.push(covering);
    }
    out
}

/// Grows one candidate with Extend's rule (ADR 0045 section 3.3).
///
/// Each step adds the extra attribute with the best estimated benefit per
/// index byte and stops when the marginal gain drops below
/// [`EXTEND_MIN_GAIN_FRACTION`]. The benefit closure sees the candidate field
/// set and returns the estimated total benefit; the skeleton caller derives
/// it from template weight and sketch selectivity, the bandit lane from
/// measured rewards.
///
/// Width-to-float conversions feed a ranking ratio, never exact accounting.
#[allow(clippy::cast_precision_loss, reason = "benefit prior; rounding is noise")]
#[must_use]
pub fn extend_candidate(
    mut columns: Vec<CandidateColumn>,
    mut extra: Vec<String>,
    mut benefit_of: impl FnMut(&[CandidateColumn]) -> f64,
) -> Vec<CandidateColumn> {
    extra.sort();
    extra.dedup();
    let mut current = benefit_of(&columns);
    loop {
        let mut best: Option<(usize, f64, Vec<CandidateColumn>)> = None;
        for (index, field) in extra.iter().enumerate() {
            if columns.iter().any(|column| column.field() == field) {
                continue;
            }
            let role = ColumnRole::Covering;
            let Ok(next_column) = CandidateColumn::try_new(field, role) else {
                continue;
            };
            let mut next = columns.clone();
            next.push(next_column);
            let width = estimated_width_bytes(&next).max(1) as f64;
            let benefit = benefit_of(&next);
            let per_byte = if width > 0.0 { benefit / width } else { 0.0 };
            let better =
                best.as_ref().is_none_or(|(_, best_per_byte, _)| per_byte > *best_per_byte);
            if better {
                best = Some((index, per_byte, next));
            }
        }
        let Some((index, _, next)) = best else {
            break;
        };
        let next_benefit = benefit_of(&next);
        let gain = if current > 0.0 { (next_benefit - current) / current } else { 1.0 };
        if gain < EXTEND_MIN_GAIN_FRACTION {
            break;
        }
        columns = next;
        current = next_benefit;
        extra.remove(index);
        if extra.is_empty() {
            break;
        }
    }
    columns
}

/// One entry of a bounded per-collection candidate set.
#[derive(Clone, Debug)]
struct CandidateEntry {
    /// The candidate.
    candidate: Candidate,
    /// When the candidate was last proposed or used.
    last_used: Instant,
}

/// The bounded per-collection candidate set (ADR 0045 section 3.3).
///
/// Insertions past [`MAX_CANDIDATES_PER_COLLECTION`] evict the oldest-unused
/// entry. Candidates are deduplicated by their ordered field list and kind.
#[derive(Debug, Default)]
pub struct CandidateSet {
    /// Entries in insertion order.
    entries: VecDeque<CandidateEntry>,
}

impl CandidateSet {
    /// Creates an empty set.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// Inserts one candidate, refreshing the timestamp on duplicates and
    /// evicting the oldest-unused entry past the cap.
    pub fn insert(&mut self, candidate: Candidate, now: Instant) {
        let key = (candidate.fields().join("\u{1f}"), candidate.kind());
        if let Some(position) = self.entries.iter().position(|entry| {
            (entry.candidate.fields().join("\u{1f}"), entry.candidate.kind()) == key
        }) {
            if let Some(entry) = self.entries.get_mut(position) {
                entry.candidate = candidate;
                entry.last_used = now;
            }
            return;
        }
        self.entries.push_back(CandidateEntry { candidate, last_used: now });
        while self.entries.len() > MAX_CANDIDATES_PER_COLLECTION {
            let oldest = self
                .entries
                .iter()
                .enumerate()
                .min_by_key(|(_, entry)| entry.last_used)
                .map(|(index, _)| index);
            match oldest {
                Some(index) => {
                    self.entries.remove(index);
                }
                None => break,
            }
        }
    }

    /// Returns the candidates in insertion order.
    #[must_use]
    pub fn candidates(&self) -> Vec<&Candidate> {
        self.entries.iter().map(|entry| &entry.candidate).collect()
    }

    /// Returns the number of retained candidates.
    #[must_use]
    pub fn len(&self) -> usize {
        self.entries.len()
    }

    /// Reports whether the set is empty.
    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.entries.is_empty()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn template_of(
        collection: &str,
        predicates: Vec<(&str, PredicateKind)>,
        ordering: Vec<&str>,
        projection: Option<Vec<&str>>,
    ) -> Template {
        Template::try_new(
            collection,
            predicates
                .into_iter()
                .map(|(field, kind)| TemplatePredicate::try_new(field, kind).expect("valid field"))
                .collect(),
            ordering.into_iter().map(str::to_owned).collect(),
            projection.map(|fields| fields.into_iter().map(str::to_owned).collect()),
        )
        .expect("valid template")
    }

    #[allow(clippy::cast_precision_loss, reason = "test weight mirrors the production prior")]
    fn weighted(template: Template, total_cost: u64, executions: u64) -> WeightedTemplate {
        WeightedTemplate {
            template,
            executions,
            total_cost,
            average_cost: total_cost as f64 / executions.max(1) as f64,
            weight: total_cost as f64,
        }
    }

    #[test]
    fn the_observe_ring_is_bounded_and_evicts_oldest_first() {
        let mut observer = WorkloadObserver::new();
        let now = Instant::now();
        for index in 0..OBSERVE_RING_CAPACITY + 10 {
            let template = template_of("users", vec![("a", PredicateKind::Equality)], vec![], None);
            observer.observe_execution(TimedObservation::new(
                template,
                LogicalCost::new(0, u64::try_from(index).unwrap_or(0)),
                1,
                None,
                now,
            ));
        }
        assert_eq!(observer.len(), OBSERVE_RING_CAPACITY);
        let held = observer.observations();
        let first = held.first().expect("ring holds entries");
        assert_eq!(first.cost().total(), 10);
    }

    #[test]
    fn the_template_of_a_normalized_query_strips_literals() {
        use crate::query::spec::{
            QueryCondition, QueryOrdering, QueryPaging, QueryPredicate, QueryProjection,
            QuerySource,
        };
        use crate::types::{CollectionName, TenantId};
        let source =
            QuerySource::try_new(CollectionName::try_new("users").expect("valid collection"))
                .expect("user collection");
        let predicate = QueryPredicate::all(vec![
            QueryCondition::Equals {
                field: "city".to_owned(),
                value: DocumentValue::String("berlin".to_owned()),
            },
            QueryCondition::GreaterThan {
                field: "age".to_owned(),
                value: DocumentValue::Integer(30),
            },
        ])
        .expect("valid predicate");
        let query = crate::query::model::NormalizedQuery::from_spec(
            source,
            predicate,
            QueryOrdering::default(),
            QueryProjection::whole_document(),
            QueryPaging::try_new(0, Some(10)).expect("valid paging"),
            crate::query::spec::StalenessPolicy::try_new(
                crate::query::spec::StalenessMode::Strict,
                Vec::new(),
            )
            .expect("valid policy"),
            Vec::new(),
            TenantId::try_new("tenant-a").expect("valid tenant"),
            crate::types::Deadline::after(Duration::from_secs(30)).expect("valid deadline"),
            crate::types::Cancellation::new(),
            Vec::new(),
            None,
        );
        let first = Template::of(&query);
        let predicate = QueryPredicate::all(vec![
            QueryCondition::Equals {
                field: "city".to_owned(),
                value: DocumentValue::String("paris".to_owned()),
            },
            QueryCondition::GreaterThan {
                field: "age".to_owned(),
                value: DocumentValue::Integer(99),
            },
        ])
        .expect("valid predicate");
        let source =
            QuerySource::try_new(CollectionName::try_new("users").expect("valid collection"))
                .expect("user collection");
        let second_query = crate::query::model::NormalizedQuery::from_spec(
            source,
            predicate,
            QueryOrdering::default(),
            QueryProjection::whole_document(),
            QueryPaging::try_new(0, Some(10)).expect("valid paging"),
            crate::query::spec::StalenessPolicy::try_new(
                crate::query::spec::StalenessMode::Strict,
                Vec::new(),
            )
            .expect("valid policy"),
            Vec::new(),
            TenantId::try_new("tenant-a").expect("valid tenant"),
            crate::types::Deadline::after(Duration::from_secs(30)).expect("valid deadline"),
            crate::types::Cancellation::new(),
            Vec::new(),
            None,
        );
        let second = Template::of(&second_query);
        assert_eq!(first, second, "literals do not change the template");
        assert_eq!(
            first
                .predicates()
                .iter()
                .map(|predicate| (predicate.field(), predicate.kind()))
                .collect::<Vec<_>>(),
            vec![("city", PredicateKind::Equality), ("age", PredicateKind::Range)]
        );
    }

    #[test]
    fn the_template_strips_predicate_classes_without_literals() {
        let equality = template_of("users", vec![("age", PredicateKind::Equality)], vec![], None);
        let range = template_of("users", vec![("age", PredicateKind::Range)], vec![], None);
        assert_ne!(equality, range);
        assert_eq!(equality.collection(), "users");
    }

    #[test]
    fn the_hll_estimates_distinct_counts_within_tolerance() {
        let mut sketch = DistinctSketch::new();
        for index in 0..200 {
            sketch.add_bytes(format!("user-{index}").as_bytes());
        }
        let estimate = sketch.estimate();
        assert!(
            (100.0..400.0).contains(&estimate),
            "200 distinct keys estimate {estimate} within 2x tolerance"
        );
    }

    #[test]
    fn the_histogram_estimates_equality_selectivity() {
        let mut sketch = ColumnSketch::new();
        for _ in 0..90 {
            sketch.observe(&DocumentValue::String("common".to_owned()));
        }
        for _ in 0..10 {
            sketch.observe(&DocumentValue::String("rare".to_owned()));
        }
        let common = sketch.histogram().selectivity_equality(
            &DocumentValue::String("common".to_owned()),
            sketch.estimate_distinct().max(1.0),
        );
        let rare = sketch.histogram().selectivity_equality(
            &DocumentValue::String("rare".to_owned()),
            sketch.estimate_distinct().max(1.0),
        );
        assert!(common > rare, "common {common} orders above rare {rare}");
        assert!((common - 0.9).abs() < 0.05, "common selectivity {common} near 0.9");
    }

    #[test]
    fn compression_keeps_the_prefix_covering_95_percent_per_collection() {
        let now = Instant::now();
        let heavy = weighted(
            template_of("users", vec![("a", PredicateKind::Equality)], vec![], None),
            950,
            95,
        );
        let light = weighted(
            template_of("users", vec![("b", PredicateKind::Equality)], vec![], None),
            50,
            50,
        );
        let observations = [heavy, light]
            .into_iter()
            .flat_map(|weighted| {
                (0..weighted.executions()).map(move |_| {
                    TimedObservation::new(
                        weighted.template().clone(),
                        LogicalCost::new(0, weighted.total_cost() / weighted.executions().max(1)),
                        1,
                        None,
                        now,
                    )
                })
            })
            .collect::<Vec<_>>();
        let tuned = tuned_workload(&observations, now, CompressConfig::default());
        let users = tuned.get("users").expect("users tuned");
        assert_eq!(users.len(), 1, "the 95 % prefix keeps the heavy template only");
        assert_eq!(users[0].template().predicates()[0].field(), "a");
    }

    #[test]
    fn candidates_follow_equality_then_range_then_order_then_covering() {
        let template = weighted(
            template_of(
                "users",
                vec![
                    ("city", PredicateKind::Equality),
                    ("age", PredicateKind::Range),
                    ("name", PredicateKind::Equality),
                ],
                vec!["created"],
                Some(vec!["email"]),
            ),
            100,
            10,
        );
        let proposed = candidates_for_template(&template, &BTreeMap::new(), &BTreeSet::new());
        assert!(!proposed.is_empty(), "one base candidate at least");
        let base = &proposed[0];
        let fields = base.fields();
        assert_eq!(fields, vec!["city", "name", "age", "created"]);
        let roles = base.columns().iter().map(CandidateColumn::role).collect::<Vec<_>>();
        assert_eq!(
            roles,
            vec![ColumnRole::Equality, ColumnRole::Equality, ColumnRole::Range, ColumnRole::Order]
        );
        assert!(base.needs_descriptor(), "no extracted fields means a descriptor is owed");
        if proposed.len() > 1 {
            let covering = &proposed[1];
            assert!(covering.fields().contains(&"email"), "covering carries the projection");
            assert_eq!(
                covering.columns().last().map(CandidateColumn::role),
                Some(ColumnRole::Covering)
            );
        }
    }

    #[test]
    fn fts_templates_propose_the_fts_kind() {
        let template = weighted(
            template_of("docs", vec![("body", PredicateKind::FullText)], vec![], None),
            100,
            10,
        );
        let proposed = candidates_for_template(&template, &BTreeMap::new(), &BTreeSet::new());
        assert_eq!(proposed.len(), 1);
        assert_eq!(proposed[0].kind(), CandidateKind::Fts);
    }

    #[test]
    #[allow(clippy::cast_precision_loss, reason = "test gain curve is illustrative")]
    fn extend_stops_below_the_5_percent_gain_rule() {
        let base = vec![CandidateColumn::try_new("a", ColumnRole::Equality).expect("valid field")];
        let grown = extend_candidate(base, vec!["b".to_owned(), "c".to_owned()], |columns| {
            // Each added column adds a flat +4 %: below the stop rule, so the
            // base never grows.
            100.0 + columns.len().saturating_sub(1) as f64 * 4.0
        });
        assert_eq!(grown.len(), 1, "a 4 % gain never extends");
        let base = vec![CandidateColumn::try_new("a", ColumnRole::Equality).expect("valid field")];
        let grown = extend_candidate(base, vec!["b".to_owned()], |columns| {
            if columns.len() > 1 { 200.0 } else { 100.0 }
        });
        assert_eq!(grown.len(), 2, "a 100 % gain extends once");
    }

    #[test]
    fn the_candidate_set_evicts_oldest_unused_past_the_cap() {
        let mut set = CandidateSet::new();
        let start = Instant::now();
        for index in 0..=MAX_CANDIDATES_PER_COLLECTION {
            let template = weighted(
                template_of(
                    "users",
                    vec![(format!("f{index}").as_str(), PredicateKind::Equality)],
                    vec![],
                    None,
                ),
                10,
                1,
            );
            let proposed = candidates_for_template(&template, &BTreeMap::new(), &BTreeSet::new());
            set.insert(proposed[0].clone(), start + Duration::from_secs(index as u64));
        }
        assert_eq!(set.len(), MAX_CANDIDATES_PER_COLLECTION);
        let fields = set
            .candidates()
            .iter()
            .flat_map(|candidate| candidate.fields())
            .map(str::to_owned)
            .collect::<Vec<_>>();
        assert!(!fields.contains(&"f0".to_owned()), "the oldest entry is evicted");
    }
}
