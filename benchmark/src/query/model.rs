//! The private normalized query model shared by both input forms.
//!
//! [`QuerySpec::try_new`](super::QuerySpec::try_new) and the V1-compatible
//! grammar both normalize into [`NormalizedQuery`], and
//! [`QueryStore::compile`](super::QueryStore::compile) is the only producer of
//! plans from it. The model is crate-private: planner shapes, reducer state,
//! and evaluation helpers are never exposed.

use std::cmp::Ordering;

use super::includes::IncludeClause;
use super::spec::{
    NullsPlacement, OrderDirection, QueryCondition, QueryLoad, QueryOrdering, QueryPaging,
    QueryPredicate, QueryProjection, QuerySource, StalenessPolicy,
};
use crate::families::{TimeSeriesPaging, TimeSeriesRange};
use crate::types::{Cancellation, Deadline, Document, DocumentValue, TenantId};

/// The validated internal query model both input forms normalize into.
#[derive(Clone, Debug, PartialEq)]
pub(crate) struct NormalizedQuery {
    source: QuerySource,
    predicate: QueryPredicate,
    ordering: QueryOrdering,
    projection: QueryProjection,
    paging: QueryPaging,
    staleness: StalenessPolicy,
    loads: Vec<QueryLoad>,
    includes: Vec<IncludeClause>,
    time_series_window: Option<(Option<TimeSeriesRange>, Option<TimeSeriesPaging>)>,
    tenant_claim: Option<TenantId>,
    deadline: Option<Deadline>,
    cancellation: Option<Cancellation>,
}

impl NormalizedQuery {
    /// Builds a model from already validated typed clauses.
    #[allow(clippy::too_many_arguments)] // the typed form carries every frozen clause
    pub(crate) fn from_spec(
        source: QuerySource,
        predicate: QueryPredicate,
        ordering: QueryOrdering,
        projection: QueryProjection,
        paging: QueryPaging,
        staleness: StalenessPolicy,
        loads: Vec<QueryLoad>,
        tenant_claim: TenantId,
        deadline: Deadline,
        cancellation: Cancellation,
        includes: Vec<IncludeClause>,
        time_series_window: Option<(Option<TimeSeriesRange>, Option<TimeSeriesPaging>)>,
    ) -> Self {
        Self {
            source,
            predicate,
            ordering,
            projection,
            paging,
            staleness,
            loads,
            includes,
            time_series_window,
            tenant_claim: Some(tenant_claim),
            deadline: Some(deadline),
            cancellation: Some(cancellation),
        }
    }

    /// Builds an unbound model, the shape the V1-compatible grammar produces:
    /// the grammar has no tenant, deadline, or cancellation clause, so those
    /// stay unbound and the snapshot supplies the tenant binding at execution.
    /// The grammar has no include clause either, so an unbound model
    /// materializes nothing.
    pub(crate) fn unbound(
        source: QuerySource,
        predicate: QueryPredicate,
        ordering: QueryOrdering,
        projection: QueryProjection,
        paging: QueryPaging,
        staleness: StalenessPolicy,
    ) -> Self {
        Self {
            source,
            predicate,
            ordering,
            projection,
            paging,
            staleness,
            loads: Vec::new(),
            includes: Vec::new(),
            time_series_window: None,
            tenant_claim: None,
            deadline: None,
            cancellation: None,
        }
    }

    /// Binds execution control to an unbound model — the deadline and
    /// cancellation the serving seam supplies for a V1-compatible
    /// query whose grammar has no control clauses.
    pub(crate) fn with_control(mut self, deadline: Deadline, cancellation: Cancellation) -> Self {
        self.deadline = Some(deadline);
        self.cancellation = Some(cancellation);
        self
    }

    /// Returns the typed source.
    pub(crate) const fn source(&self) -> &QuerySource {
        &self.source
    }

    /// Returns the typed predicate.
    pub(crate) const fn predicate(&self) -> &QueryPredicate {
        &self.predicate
    }

    /// Returns the typed ordering.
    pub(crate) const fn ordering(&self) -> &QueryOrdering {
        &self.ordering
    }

    /// Returns the typed projection.
    pub(crate) const fn projection(&self) -> &QueryProjection {
        &self.projection
    }

    /// Returns the typed page request.
    pub(crate) const fn paging(&self) -> QueryPaging {
        self.paging
    }

    /// Returns the typed staleness policy.
    pub(crate) const fn staleness(&self) -> &StalenessPolicy {
        &self.staleness
    }

    /// Returns the reference-resolution loads in declaration order.
    pub(crate) fn loads(&self) -> &[QueryLoad] {
        &self.loads
    }

    /// Returns the include clauses in declaration order.
    pub(crate) fn includes(&self) -> &[IncludeClause] {
        &self.includes
    }

    /// Returns the bound family read window of a time-series source query,
    /// or `None` when no window was bound (GAP-Q19).
    pub(crate) const fn time_series_window(
        &self,
    ) -> Option<(Option<TimeSeriesRange>, Option<TimeSeriesPaging>)> {
        self.time_series_window
    }

    /// Returns the tenant claim, or `None` for the unbound V1 form.
    pub(crate) fn tenant_claim(&self) -> Option<&TenantId> {
        self.tenant_claim.as_ref()
    }

    /// Returns the deadline, or `None` when the form carries none.
    pub(crate) fn deadline(&self) -> Option<Deadline> {
        self.deadline
    }

    /// Returns a reference to the cancellation signal, or `None`.
    ///
    /// Crate-only: execution re-reads this signal at every bounded
    /// checkpoint. The public construction path hands the signal over by
    /// value, so a caller cannot cancel a compiled plan mid-flight; the later
    /// wire adapter owns that lifecycle.
    pub(crate) fn cancellation_signal(&self) -> Option<&Cancellation> {
        self.cancellation.as_ref()
    }

    /// Returns a clone of the cancellation signal, or `None`.
    ///
    /// A clone snapshots the signal state at clone time; the bounded
    /// checkpoints re-read the owned signal through
    /// [`Self::cancellation_signal`] instead.
    #[allow(dead_code, reason = "kept for the later wire adapter that observes the bound")]
    pub(crate) fn cancellation(&self) -> Option<Cancellation> {
        self.cancellation.clone()
    }
}

/// Returns one top-level field of a document, or `None` when the document is
/// not an object or does not carry the field. W3-2 addresses top-level fields
/// only; nested paths are a documented limit.
pub(crate) fn field_of<'document>(
    document: &'document Document,
    field: &str,
) -> Option<&'document DocumentValue> {
    match document.value() {
        DocumentValue::Object(fields) => fields.get(field),
        _ => None,
    }
}

/// Reports whether one field map satisfies the predicate conjunction.
///
/// The family-source execution path (GAP-Q19) evaluates predicates over the
/// entry field space — the same closed condition list, the same
/// [`matches_condition`] evaluator, over a map instead of a document, so the
/// two paths can never drift apart.
pub(crate) fn matches_fields(
    predicate: &QueryPredicate,
    fields: &std::collections::BTreeMap<&str, DocumentValue>,
) -> bool {
    predicate
        .conditions()
        .iter()
        .all(|condition| matches_condition(condition, fields.get(condition.field())))
}

/// Compares two field maps under one typed ordering.
///
/// The mirror of [`compare_documents`] for the family entry space: the same
/// [`compare_key`] comparison, the same total document order, over the map's
/// field values. The sort applied on top is stable, so equal keys keep the
/// deterministic enumeration order of the family read.
pub(crate) fn compare_field_maps(
    ordering: &QueryOrdering,
    left: &std::collections::BTreeMap<&str, DocumentValue>,
    right: &std::collections::BTreeMap<&str, DocumentValue>,
) -> Ordering {
    for key in ordering.keys() {
        let ordering = compare_key(key, left.get(key.field()), right.get(key.field()));
        if ordering != Ordering::Equal {
            return ordering;
        }
    }
    Ordering::Equal
}

/// Reports whether one document satisfies the predicate conjunction.
pub(crate) fn matches(predicate: &QueryPredicate, document: &Document) -> bool {
    predicate
        .conditions()
        .iter()
        .all(|condition| matches_condition(condition, field_of(document, condition.field())))
}

/// Evaluates one closed condition against one optionally present field value.
fn matches_condition(condition: &QueryCondition, present: Option<&DocumentValue>) -> bool {
    match condition {
        QueryCondition::Exists { .. } => present.is_some(),
        QueryCondition::Missing { .. } => present.is_none(),
        QueryCondition::Equals { value, .. } => present == Some(value),
        QueryCondition::NotEquals { value, .. } => present != Some(value),
        QueryCondition::GreaterThan { value, .. } => {
            compare(present, value) == Some(Ordering::Greater)
        }
        QueryCondition::GreaterOrEqual { value, .. } => {
            matches!(compare(present, value), Some(Ordering::Greater | Ordering::Equal))
        }
        QueryCondition::LessThan { value, .. } => compare(present, value) == Some(Ordering::Less),
        QueryCondition::LessOrEqual { value, .. } => {
            matches!(compare(present, value), Some(Ordering::Less | Ordering::Equal))
        }
        QueryCondition::Between { low, high, .. } => matches_between(present, low, high),
        QueryCondition::In { values, .. } => match present {
            // Any-of-nothing matches nothing, absence included.
            _ if values.is_empty() => false,
            Some(value) => values.iter().any(|candidate| carries(value, candidate)),
            None => false,
        },
        QueryCondition::AllIn { values, .. } => match present {
            // All-of-nothing is vacuously true for every document, absence
            // included: V1 lowers an empty ContainsAll to TrueToken.
            _ if values.is_empty() => true,
            Some(value) => values.iter().all(|candidate| carries(value, candidate)),
            None => false,
        },
        QueryCondition::StartsWith { value, .. } => {
            matches_pattern(present, |text| text.starts_with(value.as_str()))
        }
        QueryCondition::EndsWith { value, .. } => {
            matches_pattern(present, |text| text.ends_with(value.as_str()))
        }
        QueryCondition::Contains { value, .. } => {
            matches_pattern(present, |text| text.contains(value.as_str()))
        }
    }
}

/// Reports whether one field value carries one candidate value: a scalar
/// carries it by equality, an array when one element equals it. V1's index
/// flattens array values into terms, so this one relation serves scalar `in`
/// and array contains-any alike.
fn carries(value: &DocumentValue, candidate: &DocumentValue) -> bool {
    match value {
        DocumentValue::Array(elements) => elements.contains(candidate),
        _ => value == candidate,
    }
}

/// Applies one string predicate to the string domain of a field: directly to
/// a string value, or to any string element of an array value. No other
/// value has string shape, so nothing else matches.
fn matches_pattern(present: Option<&DocumentValue>, predicate: impl Fn(&str) -> bool) -> bool {
    match present {
        Some(DocumentValue::String(text)) => predicate(text),
        Some(DocumentValue::Array(elements)) => elements
            .iter()
            .any(|element| matches!(element, DocumentValue::String(text) if predicate(text))),
        _ => false,
    }
}

/// Evaluates one inclusive range with V1's null-bound rule
/// (`CoraxBooleanItem.BuildBetween`): a null bound is unbounded on its side,
/// both-unbounded matches every present field value including explicit null,
/// an unbounded upper side additionally admits explicit null, and two bounds
/// order only orderable values.
fn matches_between(
    present: Option<&DocumentValue>,
    low: &DocumentValue,
    high: &DocumentValue,
) -> bool {
    let Some(value) = present else {
        // Absence never carries the field, not even in the both-unbounded
        // exists-or-null form.
        return false;
    };
    let low_unbounded = matches!(low, DocumentValue::Null);
    let high_unbounded = matches!(high, DocumentValue::Null);
    match (low_unbounded, high_unbounded) {
        (true, true) => true,
        (true, false) => {
            compare(Some(value), high).is_some_and(|ordering| ordering != Ordering::Greater)
        }
        (false, true) => {
            matches!(value, DocumentValue::Null)
                || compare(Some(value), low).is_some_and(|ordering| ordering != Ordering::Less)
        }
        (false, false) => {
            let ordering = compare(Some(value), low);
            let upper = compare(Some(value), high);
            ordering.is_some_and(|ordering| ordering != Ordering::Less)
                && upper.is_some_and(|upper| upper != Ordering::Greater)
        }
    }
}

/// Compares one present field value with a literal of the same logical type.
///
/// Ordering comparisons are defined inside the numeric domain (`Integer` and
/// `Number` compare numerically) and inside the text domain. Any other pair,
/// including cross-type pairs and absent fields, has no ordering and evaluates
/// to `None`, which makes the comparison conditions false rather than error.
/// A `NaN` operand is unordered in both the same-domain and the mixed
/// numeric arms, so every comparison condition against `NaN` is false. The
/// mixed numeric pairs run the exact cross-encoding comparison
/// ([`compare_integer_with_number`]): widening an integer into `f64` rounds
/// above 2^53, where the float spacing exceeds one, and an
/// inconsistent equivalence class is not a comparison.
fn compare(present: Option<&DocumentValue>, literal: &DocumentValue) -> Option<Ordering> {
    let present = present?;
    match (present, literal) {
        (DocumentValue::Integer(left), DocumentValue::Integer(right)) => Some(left.cmp(right)),
        (DocumentValue::Integer(left), DocumentValue::Number(right)) => {
            mixed_numeric_ordering(*left, *right)
        }
        (DocumentValue::Number(left), DocumentValue::Integer(right)) => {
            mixed_numeric_ordering(*right, *left).map(Ordering::reverse)
        }
        (DocumentValue::Number(left), DocumentValue::Number(right)) => ordered_f64(*left, *right),
        (DocumentValue::String(left), DocumentValue::String(right)) => Some(left.cmp(right)),
        (DocumentValue::Boolean(left), DocumentValue::Boolean(right)) => Some(left.cmp(right)),
        _ => None,
    }
}

/// Compares two finite floats; NaN compares as unordered (`None`).
fn ordered_f64(left: f64, right: f64) -> Option<Ordering> {
    left.partial_cmp(&right)
}

/// Orders one integer against one number for the predicate path: `None` for
/// `NaN` — an unordered operand makes the comparison condition false, the
/// same rule the same-domain `Number` arm applies through `partial_cmp` —
/// and the exact cross-encoding comparison otherwise. The infinities keep
/// their sign-gate answers (every integer is below `+inf` and above `-inf`),
/// so only `NaN` is routed around [`compare_integer_with_number`].
fn mixed_numeric_ordering(integer: i64, number: f64) -> Option<Ordering> {
    if number.is_nan() {
        return None;
    }
    Some(compare_integer_with_number(integer, number))
}

/// The type rank of the total document order used by `order by`.
///
/// Missing fields sort first, then null, then every value type in a fixed
/// rank. Non-finite numbers (`NaN` and the infinities) get their own rank
/// slot after every finite number, because `f64::partial_cmp` cannot order
/// `NaN` and a comparator built on it is not a total order. The rank order is
/// a W3-2 tracer rule, not a V1 parity claim.
fn rank(value: Option<&DocumentValue>) -> u8 {
    match value {
        None => 0,
        Some(DocumentValue::Null) => 1,
        Some(DocumentValue::Boolean(_)) => 2,
        // Integers and finite numbers share one unified numeric rank.
        Some(DocumentValue::Integer(_)) => 3,
        Some(DocumentValue::Number(number)) if number.is_finite() => 3,
        Some(DocumentValue::Number(_)) => 4,
        Some(DocumentValue::String(_)) => 5,
        Some(DocumentValue::Array(_)) => 6,
        Some(DocumentValue::Object(_)) => 7,
    }
}

/// Compares one integer with one finite number by exact mathematical value.
///
/// Widening the integer into `f64` rounds above 2^53 (`9007199254740992`),
/// where the float spacing exceeds one: `Integer(2^53)` and
/// `Integer(2^53 + 1)` both widen to `2^53.0`, so each would compare `Equal`
/// against `Number(2^53.0)` while comparing unequal to each other — an
/// inconsistent equivalence class that can misorder or panic a sort. The
/// comparison therefore stays exact:
///
/// - a number at or beyond 2^63 in magnitude is decided by sign against the
///   whole `i64` range (only `-2^63` itself is an `i64` value, and the exact
///   path below handles it);
/// - every other number compares its exact whole part through `i128`;
/// - equal whole parts tie-break on the fractional part, symmetrically for
///   negatives because `fract` carries the number's sign.
fn compare_integer_with_number(integer: i64, number: f64) -> Ordering {
    // Every caller routes NaN elsewhere (the rank gate in `compare_values`,
    // the NaN gate in `mixed_numeric_ordering`), and the sign gates below
    // decide any infinity before the truncation, so the `trunc` only ever
    // runs on a finite `number`.
    const TWO_POW_63: f64 = 9_223_372_036_854_775_808.0;
    if number >= TWO_POW_63 {
        // Every i64 is at most 2^63 - 1, below any float at or beyond 2^63.
        return Ordering::Less;
    }
    if number < -TWO_POW_63 {
        // Every i64 is at least -2^63, above any float below -2^63.
        return Ordering::Greater;
    }
    // `number` is finite with |number| <= 2^63, so its whole part is an
    // exact integer of at most 2^63 in magnitude: far inside `i128`, with no
    // rounding on the cast.
    #[allow(
        clippy::cast_possible_truncation,
        reason = "trunc of a finite f64 with magnitude at most 2^63 is an exact integer that fits i128"
    )]
    let whole = number.trunc() as i128;
    let ordering = i128::from(integer).cmp(&whole);
    if ordering != Ordering::Equal {
        return ordering;
    }
    let fraction = number.fract();
    if fraction > 0.0 {
        Ordering::Less
    } else if fraction < 0.0 {
        Ordering::Greater
    } else {
        Ordering::Equal
    }
}

/// Compares two document values by the total document order.
///
/// The order is a true total order over every [`DocumentValue`], including
/// `NaN`: same-type operands compare consistently, `NaN` equals `NaN`,
/// integers and finite numbers compare by exact mathematical value across
/// the two encodings, and antisymmetry and transitivity hold, so the stable
/// sort in the execution path can never observe an inconsistent comparator.
pub(crate) fn compare_values(
    left: Option<&DocumentValue>,
    right: Option<&DocumentValue>,
) -> Ordering {
    let (left_rank, right_rank) = (rank(left), rank(right));
    if left_rank != right_rank {
        return left_rank.cmp(&right_rank);
    }
    match (left, right) {
        (Some(DocumentValue::Boolean(left)), Some(DocumentValue::Boolean(right))) => {
            left.cmp(right)
        }
        (Some(DocumentValue::Integer(left)), Some(DocumentValue::Integer(right))) => {
            left.cmp(right)
        }
        // Equal ranks mean a `Number` here is finite, so the mixed arms run
        // the exact cross-type comparison instead of a widening one.
        (Some(DocumentValue::Integer(left)), Some(DocumentValue::Number(right))) => {
            compare_integer_with_number(*left, *right)
        }
        (Some(DocumentValue::Number(left)), Some(DocumentValue::Integer(right))) => {
            compare_integer_with_number(*right, *left).reverse()
        }
        (Some(DocumentValue::Number(left)), Some(DocumentValue::Number(right))) => {
            if left.is_finite() && right.is_finite() {
                // Both finite: `partial_cmp` is total over this domain, so
                // the `unwrap_or` arm is unreachable.
                left.partial_cmp(right).unwrap_or(Ordering::Equal)
            } else {
                // Equal ranks put both operands in the non-finite slot, so
                // they compare by the float total order: infinities and NaNs
                // order consistently and NaN equals NaN.
                left.total_cmp(right)
            }
        }
        (Some(DocumentValue::String(left)), Some(DocumentValue::String(right))) => left.cmp(right),
        (Some(DocumentValue::Array(left)), Some(DocumentValue::Array(right))) => {
            compare_arrays(left, right)
        }
        (Some(DocumentValue::Object(left)), Some(DocumentValue::Object(right))) => {
            compare_objects(left, right)
        }
        _ => Ordering::Equal,
    }
}

/// Compares arrays element-wise by the total document order.
fn compare_arrays(left: &[DocumentValue], right: &[DocumentValue]) -> Ordering {
    for (left_item, right_item) in left.iter().zip(right.iter()) {
        let ordering = compare_values(Some(left_item), Some(right_item));
        if ordering != Ordering::Equal {
            return ordering;
        }
    }
    left.len().cmp(&right.len())
}

/// Compares objects by their sorted field names, then by field values.
fn compare_objects(
    left: &std::collections::BTreeMap<String, DocumentValue>,
    right: &std::collections::BTreeMap<String, DocumentValue>,
) -> Ordering {
    let mut left_fields = left.iter();
    let mut right_fields = right.iter();
    loop {
        match (left_fields.next(), right_fields.next()) {
            (None, None) => return Ordering::Equal,
            (None, Some(_)) => return Ordering::Less,
            (Some(_), None) => return Ordering::Greater,
            (Some((left_name, left_value)), Some((right_name, right_value))) => {
                let name_ordering = left_name.cmp(right_name);
                if name_ordering != Ordering::Equal {
                    return name_ordering;
                }
                let value_ordering = compare_values(Some(left_value), Some(right_value));
                if value_ordering != Ordering::Equal {
                    return value_ordering;
                }
            }
        }
    }
}

/// Compares two documents under one typed ordering. The sort applied on top is
/// stable, so equal keys keep the deterministic source order.
///
/// A seeded random ordering compares the per-document hash of the seed and
/// the document identity, which is one fixed permutation per seed. A field
/// key with an explicit nulls placement moves the missing-and-null group as
/// one block to the block's side in either direction — V1's
/// direction-independent `nulls first`/`nulls last` (the Corax mapping in
/// `CoraxQueryBuilder.cs`: First is `NullsSmallest` ascending and
/// `NullsLargest` descending, Last the mirror) — while the implicit
/// placement keeps the seam's total document order unchanged.
pub(crate) fn compare_documents(
    ordering: &QueryOrdering,
    left: &Document,
    right: &Document,
) -> Ordering {
    if let Some(seed) = ordering.random_seed() {
        return random_order_key(seed, left.id().as_str())
            .cmp(&random_order_key(seed, right.id().as_str()));
    }
    for key in ordering.keys() {
        let ordering = compare_key(key, field_of(left, key.field()), field_of(right, key.field()));
        if ordering != Ordering::Equal {
            return ordering;
        }
    }
    Ordering::Equal
}

/// Compares one ordering key's field values under the key's direction and
/// nulls placement.
fn compare_key(
    key: &super::spec::OrderingKey,
    left: Option<&DocumentValue>,
    right: Option<&DocumentValue>,
) -> Ordering {
    if key.nulls_placement() == NullsPlacement::Implicit {
        return apply_direction(key.direction(), compare_values(left, right));
    }
    // The placed group is missing and null together: an index sort cannot
    // separate an absent field from an explicit null, and neither does a
    // placement.
    let left_grouped = is_missing_or_null(left);
    let right_grouped = is_missing_or_null(right);
    if left_grouped != right_grouped {
        let group_first = key.nulls_placement() == NullsPlacement::First;
        let ordering = if left_grouped { Ordering::Less } else { Ordering::Greater };
        return if group_first { ordering } else { ordering.reverse() };
    }
    if left_grouped && right_grouped {
        // Inside the block the tracer order (missing before null) stays, in
        // either direction: the placement owns the block's side, not the
        // block's internal shape.
        return rank(left).cmp(&rank(right));
    }
    apply_direction(key.direction(), compare_values(left, right))
}

/// Applies one key's direction to a value ordering.
fn apply_direction(direction: OrderDirection, ordering: Ordering) -> Ordering {
    match direction {
        OrderDirection::Ascending => ordering,
        OrderDirection::Descending => ordering.reverse(),
    }
}

/// Reports whether one field value belongs to the missing-and-null group.
fn is_missing_or_null(value: Option<&DocumentValue>) -> bool {
    match value {
        None | Some(DocumentValue::Null) => true,
        Some(_) => false,
    }
}

/// Computes the deterministic order key of one document under one seed.
///
/// FNV-1a over the seed and the document identity: a fixed, dependency-free
/// hash whose only contract is that one seed yields one stable permutation.
/// V1's two engines already produce different permutations for one seed, so
/// matching a V1 sequence is not a parity obligation.
fn random_order_key(seed: u64, id: &str) -> u64 {
    let mut hash = 0xcbf2_9ce4_8422_2325_u64 ^ seed;
    for byte in id.as_bytes() {
        hash ^= u64::from(*byte);
        hash = hash.wrapping_mul(0x0000_0100_0000_01b3);
    }
    hash
}
