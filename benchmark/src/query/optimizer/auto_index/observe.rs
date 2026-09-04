//! Bounded workload observation and compression for automatic indexes.
//!
//! The query path only records small, immutable facts. Compression happens
//! outside that path and applies both an age window and exponential decay.
//! This module also owns the in-process replay format used by a durable
//! observation writer; it deliberately does not know how bytes are stored.

use std::collections::{BTreeMap, VecDeque};
use std::time::{Duration, Instant};

use super::{
    LogicalCost, PredicateKind, Template, TemplatePredicate, TimedObservation, WorkloadObserver,
};
use crate::query::model::NormalizedQuery;
use crate::types::DocumentValue;

/// Maximum bytes retained for one value boundary in a histogram.
pub const MAX_HISTOGRAM_KEY_BYTES: usize = 256;
/// Maximum number of column sketches in a production observer.
pub const MAX_SKETCH_COLUMNS: usize = 1024;
/// Version of the canonical observation record encoding.
pub const OBSERVATION_RECORD_VERSION: u8 = 1;

/// Inputs captured at the query execution boundary.
pub(crate) struct ExecutionInput<'a> {
    /// The observer owning the database-local ring.
    pub(crate) observer: &'a mut WorkloadObserver,
    /// The normalized query, before literals are discarded.
    pub(crate) query: &'a NormalizedQuery,
    /// Logical SQLite work counters.
    pub(crate) cost: LogicalCost,
    /// Rows returned by the execution.
    pub(crate) rows_returned: u64,
    /// Serving index, if one was selected.
    pub(crate) index_used: Option<String>,
    /// Monotonic observation timestamp.
    pub(crate) observed_at: Instant,
}

impl<'a> ExecutionInput<'a> {
    /// Records the input after deriving its literal-free template.
    pub(crate) fn observe(self) {
        let template = Template::of(self.query);
        self.observer.observe_execution(TimedObservation::new(
            template,
            self.cost,
            self.rows_returned,
            self.index_used,
            self.observed_at,
        ));
    }
}

/// Configuration for a production observer's bounded state.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct ObserverConfig {
    capacity: usize,
    max_columns: usize,
    histogram_buckets: usize,
    window: Duration,
    half_life: Duration,
}

impl ObserverConfig {
    /// The conservative production defaults.
    pub const DEFAULT: Self = Self {
        capacity: super::OBSERVE_RING_CAPACITY,
        max_columns: MAX_SKETCH_COLUMNS,
        histogram_buckets: 32,
        window: Duration::from_secs(24 * 60 * 60),
        half_life: Duration::from_secs(6 * 60 * 60),
    };

    /// Creates a bounded configuration.
    ///
    /// Capacities are capped at hard module bounds, so configuration cannot
    /// turn observations into an unbounded allocation.
    pub fn try_new(
        capacity: usize,
        max_columns: usize,
        histogram_buckets: usize,
        window: Duration,
        half_life: Duration,
    ) -> Result<Self, ObservationConfigError> {
        if capacity == 0
            || max_columns == 0
            || histogram_buckets == 0
            || window.is_zero()
            || half_life.is_zero()
        {
            return Err(ObservationConfigError::ZeroValue);
        }
        Ok(Self {
            capacity: capacity.min(super::OBSERVE_RING_CAPACITY),
            max_columns: max_columns.min(MAX_SKETCH_COLUMNS),
            histogram_buckets: histogram_buckets.min(256),
            window,
            half_life,
        })
    }

    /// Returns the ring capacity.
    #[must_use]
    pub const fn capacity(self) -> usize {
        self.capacity
    }
    /// Returns the maximum number of columns.
    #[must_use]
    pub const fn max_columns(self) -> usize {
        self.max_columns
    }
    /// Returns the histogram bucket bound.
    #[must_use]
    pub const fn histogram_buckets(self) -> usize {
        self.histogram_buckets
    }
    /// Returns the compression window.
    #[must_use]
    pub const fn window(self) -> Duration {
        self.window
    }
    /// Returns the exponential-decay half-life.
    #[must_use]
    pub const fn half_life(self) -> Duration {
        self.half_life
    }
}

impl Default for ObserverConfig {
    fn default() -> Self {
        Self::DEFAULT
    }
}

/// A configuration value was invalid.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ObservationConfigError {
    /// A required duration or capacity was zero.
    ZeroValue,
}

/// A fixed-register HyperLogLog sketch used by a bounded column sketch.
#[derive(Clone, Debug, PartialEq)]
pub struct BoundedDistinctSketch {
    registers: [u8; super::SKETCH_HLL_REGISTERS],
    count: u64,
}

impl Default for BoundedDistinctSketch {
    fn default() -> Self {
        Self { registers: [0; super::SKETCH_HLL_REGISTERS], count: 0 }
    }
}

impl BoundedDistinctSketch {
    /// Creates an empty sketch.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// Adds a value without retaining the value itself.
    pub fn observe(&mut self, value: &DocumentValue) {
        let key = canonical_value_key(value);
        self.add_bytes(key.as_bytes());
    }

    /// Adds bytes to the sketch.
    pub fn add_bytes(&mut self, bytes: &[u8]) {
        let hash = fnv1a64(bytes);
        let index = (hash as usize) & (self.registers.len() - 1);
        let rest = hash >> 6;
        let rank = rest.leading_zeros().saturating_sub(6).saturating_add(1).min(64) as u8;
        self.registers[index] = self.registers[index].max(rank.max(1));
        self.count = self.count.saturating_add(1);
    }

    /// Returns the approximate distinct cardinality.
    #[must_use]
    #[allow(clippy::cast_precision_loss, reason = "HLL estimate is an approximate prior")]
    pub fn estimate(&self) -> f64 {
        let m = self.registers.len() as f64;
        let mut harmonic = 0.0;
        let mut zeros = 0_u64;
        for register in self.registers {
            harmonic += 2.0_f64.powi(-i32::from(register));
            zeros += u64::from(register == 0);
        }
        if harmonic == 0.0 {
            return f64::INFINITY;
        }
        let raw = 0.673 * m * m / harmonic;
        if raw <= 2.5 * m && zeros > 0 { m * (m / zeros as f64).ln() } else { raw }
    }

    /// Returns the number of values fed to the sketch.
    #[must_use]
    pub const fn count(&self) -> u64 {
        self.count
    }
}

/// One ordered, approximate equi-depth bucket.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct HistogramBucket {
    lower: String,
    upper: String,
    count: u64,
}

impl HistogramBucket {
    /// Returns the inclusive lower canonical value boundary.
    #[must_use]
    pub fn lower(&self) -> &str {
        &self.lower
    }
    /// Returns the inclusive upper canonical value boundary.
    #[must_use]
    pub fn upper(&self) -> &str {
        &self.upper
    }
    /// Returns the number of values represented by this bucket.
    #[must_use]
    pub const fn count(&self) -> u64 {
        self.count
    }
}

/// A bounded ordered histogram. Adjacent buckets are merged when the bound
/// is reached, preserving quantile order and total mass deterministically.
#[derive(Clone, Debug, Default, PartialEq)]
pub struct EquiDepthHistogram {
    buckets: Vec<HistogramBucket>,
    total: u64,
    limit: usize,
}

impl EquiDepthHistogram {
    /// Creates an empty histogram with a bounded number of buckets.
    #[must_use]
    pub fn new(limit: usize) -> Self {
        Self { buckets: Vec::new(), total: 0, limit: limit.max(1).min(256) }
    }

    /// Adds one value and maintains the bucket bound.
    pub fn observe(&mut self, value: &DocumentValue) {
        self.observe_key(&canonical_value_key(value));
    }

    fn observe_key(&mut self, raw_key: &str) {
        let key = bounded_key(raw_key);
        self.total = self.total.saturating_add(1);
        if let Some(bucket) =
            self.buckets.iter_mut().find(|bucket| key >= bucket.lower && key <= bucket.upper)
        {
            bucket.count = bucket.count.saturating_add(1);
            return;
        }
        let position = self.buckets.partition_point(|bucket| bucket.lower < key);
        self.buckets.insert(position, HistogramBucket { lower: key.clone(), upper: key, count: 1 });
        while self.buckets.len() > self.limit {
            self.merge_closest();
        }
    }

    fn merge_closest(&mut self) {
        if self.buckets.len() < 2 {
            return;
        }
        let mut best = 0;
        let mut score = u64::MAX;
        for index in 0..self.buckets.len() - 1 {
            let candidate = self.buckets[index].count.saturating_add(self.buckets[index + 1].count);
            if candidate < score {
                score = candidate;
                best = index;
            }
        }
        let right = self.buckets.remove(best + 1);
        let left = &mut self.buckets[best];
        left.upper = right.upper;
        left.count = left.count.saturating_add(right.count);
    }

    /// Returns the total number of values observed.
    #[must_use]
    pub const fn total(&self) -> u64 {
        self.total
    }
    /// Returns buckets in ascending canonical-key order.
    #[must_use]
    pub fn buckets(&self) -> &[HistogramBucket] {
        &self.buckets
    }
    /// Returns the configured bucket bound.
    #[must_use]
    pub const fn limit(&self) -> usize {
        self.limit
    }

    /// Returns an approximate equality selectivity for a value.
    #[must_use]
    #[allow(clippy::cast_precision_loss, reason = "histogram is a ranking prior")]
    pub fn selectivity_equality(&self, value: &DocumentValue, distinct: f64) -> f64 {
        if self.total == 0 {
            return 1.0;
        }
        let key = bounded_key(&canonical_value_key(value));
        let count = self
            .buckets
            .iter()
            .find(|bucket| key >= bucket.lower && key <= bucket.upper)
            .map_or(0, |bucket| bucket.count);
        if count > 0 {
            count as f64 / self.total as f64
        } else if distinct > 1.0 {
            (1.0 / distinct).min(1.0)
        } else {
            1.0
        }
    }

    /// Returns an approximate fraction of values in an inclusive range.
    #[must_use]
    #[allow(clippy::cast_precision_loss, reason = "histogram is a ranking prior")]
    pub fn selectivity_range(&self, lower: &DocumentValue, upper: &DocumentValue) -> f64 {
        if self.total == 0 {
            return 0.0;
        }
        let lower = bounded_key(&canonical_value_key(lower));
        let upper = bounded_key(&canonical_value_key(upper));
        if lower > upper {
            return 0.0;
        }
        let count: u64 = self
            .buckets
            .iter()
            .filter(|bucket| bucket.upper >= lower && bucket.lower <= upper)
            .map(|bucket| bucket.count)
            .sum();
        (count as f64 / self.total as f64).clamp(0.0, 1.0)
    }
}

/// Bounded statistics for one extracted column.
#[derive(Clone, Debug, PartialEq)]
pub struct BoundedColumnSketch {
    distinct: BoundedDistinctSketch,
    histogram: EquiDepthHistogram,
}

impl BoundedColumnSketch {
    /// Creates a sketch with the supplied histogram bound.
    #[must_use]
    pub fn new(histogram_buckets: usize) -> Self {
        Self {
            distinct: BoundedDistinctSketch::new(),
            histogram: EquiDepthHistogram::new(histogram_buckets),
        }
    }
    /// Observes one value.
    pub fn observe(&mut self, value: &DocumentValue) {
        self.distinct.observe(value);
        self.histogram.observe(value);
    }
    /// Returns the distinct sketch.
    #[must_use]
    pub const fn distinct(&self) -> &BoundedDistinctSketch {
        &self.distinct
    }
    /// Returns the equi-depth histogram.
    #[must_use]
    pub const fn histogram(&self) -> &EquiDepthHistogram {
        &self.histogram
    }
}

/// A production observer with strict ring, sketch, and histogram bounds.
#[derive(Clone, Debug, PartialEq)]
pub struct ProductionWorkloadObserver {
    config: ObserverConfig,
    ring: VecDeque<TimedObservation>,
    sketches: BTreeMap<(String, String), BoundedColumnSketch>,
}

impl ProductionWorkloadObserver {
    /// Creates an observer with production defaults.
    #[must_use]
    pub fn new() -> Self {
        Self::with_config(ObserverConfig::default())
    }
    /// Creates an observer with bounded custom configuration.
    #[must_use]
    pub fn with_config(config: ObserverConfig) -> Self {
        Self { config, ring: VecDeque::new(), sketches: BTreeMap::new() }
    }
    /// Records one execution, retaining only the newest configured entries.
    pub fn observe_execution(&mut self, observation: TimedObservation) {
        self.ring.push_back(observation);
        while self.ring.len() > self.config.capacity {
            self.ring.pop_front();
        }
    }
    /// Records an extracted-column value. Invalid or oversized identifiers are ignored.
    pub fn observe_write(&mut self, collection: &str, field: &str, value: &DocumentValue) {
        if !valid_bounded_identifier(collection) || !valid_bounded_identifier(field) {
            return;
        }
        let key = (collection.to_owned(), field.to_owned());
        if !self.sketches.contains_key(&key) && self.sketches.len() >= self.config.max_columns {
            return;
        }
        self.sketches
            .entry(key)
            .or_insert_with(|| BoundedColumnSketch::new(self.config.histogram_buckets))
            .observe(value);
    }
    /// Returns the configuration.
    #[must_use]
    pub const fn config(&self) -> ObserverConfig {
        self.config
    }
    /// Returns retained observations in oldest-first order.
    #[must_use]
    pub fn observations(&self) -> Vec<&TimedObservation> {
        self.ring.iter().collect()
    }
    /// Returns sketches in deterministic key order.
    #[must_use]
    pub fn sketches(&self) -> &BTreeMap<(String, String), BoundedColumnSketch> {
        &self.sketches
    }
    /// Compresses the retained ring using this observer's decay policy.
    #[must_use]
    pub fn compress(&self, now: Instant) -> BTreeMap<String, Vec<CompressedTemplate>> {
        compress_workload(self.ring.iter(), now, self.config.window, self.config.half_life, 0.95)
    }
}

impl Default for ProductionWorkloadObserver {
    fn default() -> Self {
        Self::new()
    }
}

/// An aggregate template after windowing and decay.
#[derive(Clone, Debug, PartialEq)]
pub struct CompressedTemplate {
    template: Template,
    executions: u64,
    effective_executions: f64,
    weighted_cost: f64,
    average_cost: f64,
}

impl CompressedTemplate {
    /// Returns the template.
    #[must_use]
    pub const fn template(&self) -> &Template {
        &self.template
    }
    /// Returns the raw execution count in the window.
    #[must_use]
    pub const fn executions(&self) -> u64 {
        self.executions
    }
    /// Returns the decayed execution count.
    #[must_use]
    pub const fn effective_executions(&self) -> f64 {
        self.effective_executions
    }
    /// Returns the decayed logical-cost weight.
    #[must_use]
    pub const fn weighted_cost(&self) -> f64 {
        self.weighted_cost
    }
    /// Returns the decayed average logical cost.
    #[must_use]
    pub const fn average_cost(&self) -> f64 {
        self.average_cost
    }
}

/// Compresses observations by template with age-window and half-life decay.
#[must_use]
#[allow(clippy::cast_precision_loss, reason = "compression is an approximate ranking prior")]
pub fn compress_workload<'a, I>(
    observations: I,
    now: Instant,
    window: Duration,
    half_life: Duration,
    coverage: f64,
) -> BTreeMap<String, Vec<CompressedTemplate>>
where
    I: IntoIterator<Item = &'a TimedObservation>,
{
    if window.is_zero() || half_life.is_zero() || !(0.0 < coverage && coverage <= 1.0) {
        return BTreeMap::new();
    }
    let mut grouped: BTreeMap<Template, (u64, f64, f64)> = BTreeMap::new();
    for observation in observations {
        let age = match now.checked_duration_since(observation.observed_at()) {
            Some(age) if age <= window => age,
            _ => continue,
        };
        let decay = 2.0_f64.powf(-(age.as_secs_f64() / half_life.as_secs_f64()));
        let entry = grouped.entry(observation.template().clone()).or_insert((0, 0.0, 0.0));
        entry.0 = entry.0.saturating_add(1);
        entry.1 += decay;
        entry.2 += observation.cost().total() as f64 * decay;
    }
    let mut result: BTreeMap<String, Vec<CompressedTemplate>> = BTreeMap::new();
    for (template, (executions, effective, cost)) in grouped {
        if effective <= 0.0 {
            continue;
        }
        result.entry(template.collection().to_owned()).or_default().push(CompressedTemplate {
            template,
            executions,
            effective_executions: effective,
            weighted_cost: cost,
            average_cost: cost / effective,
        });
    }
    for templates in result.values_mut() {
        templates.sort_by(|left, right| {
            right
                .weighted_cost
                .total_cmp(&left.weighted_cost)
                .then_with(|| left.template.cmp(&right.template))
        });
        let total: f64 = templates.iter().map(|item| item.weighted_cost).sum();
        let mut covered = 0.0;
        let mut keep = 0;
        for item in templates.iter() {
            covered += item.weighted_cost;
            keep += 1;
            if total > 0.0 && covered / total >= coverage {
                break;
            }
        }
        templates.truncate(keep.max(1));
    }
    result.retain(|_, templates| !templates.is_empty());
    result
}

/// A durable, replayable observation record. Its timestamp is stored as an
/// age relative to the flush instant, avoiding wall-clock dependence.
#[derive(Clone, Debug, PartialEq)]
pub struct ObservationRecord {
    sequence: u64,
    template: Template,
    cost: LogicalCost,
    rows_returned: u64,
    index_used: Option<String>,
    age_millis: u64,
}

impl ObservationRecord {
    /// Captures an observation at a flush instant.
    #[must_use]
    pub fn from_observation(
        sequence: u64,
        observation: &TimedObservation,
        flushed_at: Instant,
    ) -> Self {
        let age_millis = flushed_at
            .checked_duration_since(observation.observed_at())
            .unwrap_or_default()
            .as_millis()
            .min(u128::from(u64::MAX)) as u64;
        Self {
            sequence,
            template: observation.template().clone(),
            cost: observation.cost(),
            rows_returned: observation.rows_returned(),
            index_used: observation.index_used().map(str::to_owned),
            age_millis,
        }
    }
    /// Returns the durable sequence.
    #[must_use]
    pub const fn sequence(&self) -> u64 {
        self.sequence
    }
    /// Recreates the observation at a new replay instant.
    #[must_use]
    pub fn replay(&self, now: Instant) -> TimedObservation {
        TimedObservation::new(
            self.template.clone(),
            self.cost,
            self.rows_returned,
            self.index_used.clone(),
            now.checked_sub(Duration::from_millis(self.age_millis)).unwrap_or(now),
        )
    }
    /// Encodes this record deterministically.
    #[must_use]
    pub fn encoded(&self) -> Vec<u8> {
        let mut out = Vec::new();
        out.extend_from_slice(b"TBO");
        out.push(OBSERVATION_RECORD_VERSION);
        put_u64(&mut out, self.sequence);
        put_u64(&mut out, self.age_millis);
        put_u64(&mut out, self.cost.pages_read());
        put_u64(&mut out, self.cost.rows_examined());
        put_u64(&mut out, self.rows_returned);
        put_string(&mut out, self.template.collection());
        put_u32(&mut out, self.template.predicates().len() as u32);
        for predicate in self.template.predicates() {
            put_string(&mut out, predicate.field());
            out.push(predicate_kind_byte(predicate.kind()));
        }
        put_u32(&mut out, self.template.ordering().len() as u32);
        for field in self.template.ordering() {
            put_string(&mut out, field);
        }
        match self.template.projection() {
            Some(fields) => {
                out.push(1);
                put_u32(&mut out, fields.len() as u32);
                for field in fields {
                    put_string(&mut out, field);
                }
            }
            None => out.push(0),
        }
        match &self.index_used {
            Some(index) => {
                out.push(1);
                put_string(&mut out, index);
            }
            None => out.push(0),
        }
        out
    }

    /// Decodes and validates one canonical record.
    pub fn decode(bytes: &[u8]) -> Result<Self, ObservationDecodeError> {
        let mut reader = Reader::new(bytes);
        if reader.take(3)? != b"TBO" || reader.byte()? != OBSERVATION_RECORD_VERSION {
            return Err(ObservationDecodeError::InvalidHeader);
        }
        let sequence = reader.u64()?;
        let age_millis = reader.u64()?;
        let pages = reader.u64()?;
        let rows_examined = reader.u64()?;
        let rows_returned = reader.u64()?;
        let collection = reader.string()?;
        let predicate_count = reader.count()?;
        let mut predicates = Vec::with_capacity(predicate_count);
        for _ in 0..predicate_count {
            let field = reader.string()?;
            let kind =
                predicate_kind(reader.byte()?).ok_or(ObservationDecodeError::InvalidValue)?;
            predicates.push(
                TemplatePredicate::try_new(&field, kind)
                    .map_err(|_| ObservationDecodeError::InvalidValue)?,
            );
        }
        let ordering_count = reader.count()?;
        let mut ordering = Vec::with_capacity(ordering_count);
        for _ in 0..ordering_count {
            ordering.push(reader.string()?);
        }
        let projection = match reader.byte()? {
            0 => None,
            1 => {
                let count = reader.count()?;
                let mut fields = Vec::with_capacity(count);
                for _ in 0..count {
                    fields.push(reader.string()?);
                }
                Some(fields)
            }
            _ => return Err(ObservationDecodeError::InvalidValue),
        };
        let index_used = match reader.byte()? {
            0 => None,
            1 => Some(reader.string()?),
            _ => return Err(ObservationDecodeError::InvalidValue),
        };
        if !reader.done() {
            return Err(ObservationDecodeError::TrailingBytes);
        }
        let template = Template::try_new(&collection, predicates, ordering, projection)
            .map_err(|_| ObservationDecodeError::InvalidValue)?;
        Ok(Self {
            sequence,
            template,
            cost: LogicalCost::new(pages, rows_examined),
            rows_returned,
            index_used,
            age_millis,
        })
    }
}

/// A durable observation record was malformed.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ObservationDecodeError {
    /// Header or version did not match.
    InvalidHeader,
    /// The record ended before a field was complete.
    Truncated,
    /// A field violated its bound or enum contract.
    InvalidValue,
    /// Bytes followed a complete record.
    TrailingBytes,
}

fn put_u32(out: &mut Vec<u8>, value: u32) {
    out.extend_from_slice(&value.to_le_bytes());
}
fn put_u64(out: &mut Vec<u8>, value: u64) {
    out.extend_from_slice(&value.to_le_bytes());
}
fn put_string(out: &mut Vec<u8>, value: &str) {
    put_u32(out, value.len() as u32);
    out.extend_from_slice(value.as_bytes());
}
fn predicate_kind_byte(kind: PredicateKind) -> u8 {
    match kind {
        PredicateKind::Equality => 0,
        PredicateKind::Range => 1,
        PredicateKind::FullText => 2,
        PredicateKind::Other => 3,
    }
}
fn predicate_kind(byte: u8) -> Option<PredicateKind> {
    Some(match byte {
        0 => PredicateKind::Equality,
        1 => PredicateKind::Range,
        2 => PredicateKind::FullText,
        3 => PredicateKind::Other,
        _ => return None,
    })
}

struct Reader<'a> {
    bytes: &'a [u8],
    position: usize,
}
impl<'a> Reader<'a> {
    fn new(bytes: &'a [u8]) -> Self {
        Self { bytes, position: 0 }
    }
    fn take(&mut self, count: usize) -> Result<&'a [u8], ObservationDecodeError> {
        let end = self.position.checked_add(count).ok_or(ObservationDecodeError::Truncated)?;
        let value = self.bytes.get(self.position..end).ok_or(ObservationDecodeError::Truncated)?;
        self.position = end;
        Ok(value)
    }
    fn byte(&mut self) -> Result<u8, ObservationDecodeError> {
        Ok(*self.take(1)?.first().ok_or(ObservationDecodeError::Truncated)?)
    }
    fn u64(&mut self) -> Result<u64, ObservationDecodeError> {
        Ok(u64::from_le_bytes(
            self.take(8)?.try_into().map_err(|_| ObservationDecodeError::Truncated)?,
        ))
    }
    fn count(&mut self) -> Result<usize, ObservationDecodeError> {
        let count = u32::from_le_bytes(
            self.take(4)?.try_into().map_err(|_| ObservationDecodeError::Truncated)?,
        );
        if count > 4096 {
            return Err(ObservationDecodeError::InvalidValue);
        }
        Ok(count as usize)
    }
    fn string(&mut self) -> Result<String, ObservationDecodeError> {
        let length = self.count()?;
        String::from_utf8(self.take(length)?.to_vec())
            .map_err(|_| ObservationDecodeError::InvalidValue)
    }
    fn done(&self) -> bool {
        self.position == self.bytes.len()
    }
}

fn fnv1a64(bytes: &[u8]) -> u64 {
    let mut hash = 0xcbf2_9ce4_8422_2325_u64;
    for byte in bytes {
        hash = (hash ^ u64::from(*byte)).wrapping_mul(0x0000_0100_0000_01b3);
    }
    hash
}
fn canonical_value_key(value: &DocumentValue) -> String {
    match value {
        DocumentValue::Null => "null".to_owned(),
        DocumentValue::Boolean(value) => format!("boolean:{value}"),
        DocumentValue::Integer(value) => format!("integer:{value}"),
        DocumentValue::Number(value) => format!("number:{}", value.to_bits()),
        DocumentValue::String(value) => format!("string:{value}"),
        DocumentValue::Array(values) => format!(
            "array:[{}]",
            values.iter().map(canonical_value_key).collect::<Vec<_>>().join(",")
        ),
        DocumentValue::Object(values) => format!(
            "object:{{{}}}",
            values
                .iter()
                .map(|(key, value)| format!("{key}={}", canonical_value_key(value)))
                .collect::<Vec<_>>()
                .join(",")
        ),
    }
}
fn bounded_key(value: &str) -> String {
    if value.len() <= MAX_HISTOGRAM_KEY_BYTES {
        value.to_owned()
    } else {
        format!("hash:{:016x}", fnv1a64(value.as_bytes()))
    }
}
fn valid_bounded_identifier(value: &str) -> bool {
    !value.is_empty()
        && value.len() <= 255
        && !value.chars().any(char::is_control)
        && value == value.trim()
}

/// Applies the bounded-column guard to the legacy observer seam.
pub trait BoundedObservationExt {
    /// Records a write only when the legacy observer is below the column cap.
    fn observe_write_bounded(&mut self, collection: &str, field: &str, value: &DocumentValue);
}
impl BoundedObservationExt for WorkloadObserver {
    fn observe_write_bounded(&mut self, collection: &str, field: &str, value: &DocumentValue) {
        if !valid_bounded_identifier(collection) || !valid_bounded_identifier(field) {
            return;
        }
        if self.sketch(collection, field).is_some() || self.sketches().len() < MAX_SKETCH_COLUMNS {
            self.observe_write(collection, field, value);
        }
    }
}
