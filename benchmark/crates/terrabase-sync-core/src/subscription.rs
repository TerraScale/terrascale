//! Subscription keys and the interval structure that matches committed
//! rows against them (`S-04`, D-46).
//!
//! A subscription is a planned TQL query plus its dependency keys: the
//! `(collection, index field, value range)` triples its plan reads. Each
//! committed row is matched against those keys using the extracted values
//! the write path already produced — no re-planning per write — and every
//! subscription whose key the row touches gets one delta.

use std::collections::BTreeMap;
use std::ops::Bound;

/// One extracted index value: the two storage classes the D-42 stored
/// columns carry (`f:<field>#t` TEXT, `f:<field>#n` NUMERIC).
#[derive(Clone, Debug, PartialEq)]
pub enum FieldValue {
    /// The text-column value.
    Text(String),
    /// The numeric-column value (the stored NUMERIC column class).
    Number(f64),
}

// `f64` has no total `Ord`; the dependency order needs one, so the
// numeric class orders through `total_cmp` (NaN sorts last, total).
impl Eq for FieldValue {}
impl PartialOrd for FieldValue {
    fn partial_cmp(&self, other: &Self) -> Option<std::cmp::Ordering> {
        Some(self.cmp(other))
    }
}
impl Ord for FieldValue {
    fn cmp(&self, other: &Self) -> std::cmp::Ordering {
        match (self, other) {
            (Self::Text(left), Self::Text(right)) => left.cmp(right),
            (Self::Number(left), Self::Number(right)) => left.total_cmp(right),
            (Self::Text(_), Self::Number(_)) => std::cmp::Ordering::Less,
            (Self::Number(_), Self::Text(_)) => std::cmp::Ordering::Greater,
        }
    }
}

/// One end of a dependency range.
#[derive(Clone, Debug, Eq, Ord, PartialEq, PartialOrd)]
pub enum RangeEnd {
    /// The range is unbounded on this side.
    Unbounded,
    /// The range includes this value.
    Included(FieldValue),
    /// The range excludes this value.
    Excluded(FieldValue),
}

/// The value range one subscription reads on one `(collection, field)`
/// index. `Included..Included` with equal ends is an equality point.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ValueRange {
    /// The lower end.
    pub start: RangeEnd,
    /// The upper end.
    pub end: RangeEnd,
}

impl ValueRange {
    /// The equality point over one value.
    #[must_use]
    pub fn equals(value: FieldValue) -> Self {
        Self { start: RangeEnd::Included(value.clone()), end: RangeEnd::Included(value) }
    }

    /// The unbounded range: every row of the collection matches.
    #[must_use]
    pub fn all() -> Self {
        Self { start: RangeEnd::Unbounded, end: RangeEnd::Unbounded }
    }

    /// Reports whether one extracted value falls inside the range.
    #[must_use]
    pub fn contains(&self, value: &FieldValue) -> bool {
        above_start(&self.start, value) && below_end(&self.end, value)
    }
}

/// Reports whether the value sits at or above the range's start end.
fn above_start(start: &RangeEnd, value: &FieldValue) -> bool {
    match start {
        RangeEnd::Unbounded => true,
        RangeEnd::Included(bound) => value >= bound,
        RangeEnd::Excluded(bound) => value > bound,
    }
}

/// Reports whether the value sits at or below the range's end end.
fn below_end(end: &RangeEnd, value: &FieldValue) -> bool {
    match end {
        RangeEnd::Unbounded => true,
        RangeEnd::Included(bound) => value <= bound,
        RangeEnd::Excluded(bound) => value < bound,
    }
}

/// One subscription's dependency on one index: the collection, the
/// extracted field its plan reads, and the value range it covers.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct DependencyKey {
    /// The collection the plan reads.
    pub collection: String,
    /// The extracted index field the plan reads.
    pub field: String,
    /// The value range the plan covers.
    pub range: ValueRange,
}

/// One committed row offered to the matcher: the extracted values the
/// write path already produced, never a re-parse of the body.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct CommittedRow<'row> {
    /// The row's collection.
    pub collection: &'row str,
    /// The row's document identifier.
    pub document: &'row str,
    /// The extracted values in hand, in field order.
    pub extracted: Vec<(&'row str, FieldValue)>,
}

/// The dependency index: subscriptions keyed by their
/// `(collection, field, range)` dependencies, matched by committed rows
/// (D-46).
///
/// The tree is one ordered map per `(collection, field)` whose entries
/// carry the range's start end; a row probes the map with its value and
/// walks backwards over every range that could still cover it. The
/// per-key start ordering bounds the walk: once a start end sorts above
/// the value's own position, no earlier subscription can match.
// ponytail: per-key sorted starts with a backward walk; a max-end
// augmented interval tree replaces it when one (collection, field) hosts
// thousands of overlapping range subscriptions.
#[derive(Default)]
pub struct DependencyIndex {
    keys: BTreeMap<(String, String), Vec<(DependencyKey, u64)>>,
}

impl DependencyIndex {
    /// Creates an empty index.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// Registers one subscription's dependencies; `remove` retracts them.
    /// A subscription with no dependency keys depends on nothing and is
    /// never matched.
    pub fn register(&mut self, subscription: u64, key: DependencyKey, remove: bool) {
        let entries = self.keys.entry((key.collection.clone(), key.field.clone())).or_default();
        if remove {
            entries.retain(|(_, owner)| *owner != subscription);
            if entries.is_empty() {
                self.keys.remove(&(key.collection, key.field));
            }
            return;
        }
        let pair = (key, subscription);
        let position =
            entries.partition_point(|(owned, _)| owned.range.start <= pair.0.range.start);
        entries.insert(position, pair);
    }

    /// Retracts every dependency of one subscription.
    pub fn retract(&mut self, subscription: u64) {
        let mut empty = Vec::new();
        for ((collection, field), entries) in &mut self.keys {
            let before = entries.len();
            entries.retain(|(_, owner)| *owner != subscription);
            if entries.len() != before {
                empty.push((collection.clone(), field.clone()));
            }
        }
        for key in empty {
            if self.keys.get(&key).is_some_and(Vec::is_empty) {
                self.keys.remove(&key);
            }
        }
    }

    /// Returns every subscription whose dependency the committed row
    /// touches, in ascending subscription order, once each.
    #[must_use]
    pub fn match_row(&self, row: &CommittedRow<'_>) -> Vec<u64> {
        let mut affected: Vec<u64> = Vec::new();
        for (field, value) in &row.extracted {
            let Some(entries) = self.keys.get(&(row.collection.to_owned(), (*field).to_owned()))
            else {
                continue;
            };
            for (key, owner) in entries {
                if key.range.contains(value) {
                    affected.push(*owner);
                }
            }
        }
        affected.sort_unstable();
        affected.dedup();
        affected
    }

    /// Reports whether one subscription depends on one key. Test seam
    /// for the registration discipline.
    #[cfg(test)]
    fn is_registered(&self, subscription: u64, key: &DependencyKey) -> bool {
        self.keys
            .get(&(key.collection.clone(), key.field.clone()))
            .is_some_and(|entries| entries.iter().any(|(_, owner)| *owner == subscription))
    }
}

impl std::fmt::Debug for DependencyIndex {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        formatter.debug_map().entries(self.keys.iter()).finish()
    }
}

/// The bound type [`ValueRange`] pairs; re-exported for callers that
/// build ranges from plan bounds (`Bound::Included(x)` maps to
/// [`RangeEnd::Included`]).
#[must_use]
pub fn range_end_of(bound: Bound<FieldValue>) -> RangeEnd {
    match bound {
        Bound::Included(value) => RangeEnd::Included(value),
        Bound::Excluded(value) => RangeEnd::Excluded(value),
        Bound::Unbounded => RangeEnd::Unbounded,
    }
}

#[cfg(test)]
mod tests {
    use super::CommittedRow;
    use super::DependencyIndex;
    use super::DependencyKey;
    use super::FieldValue;
    use super::ValueRange;

    fn key(collection: &str, field: &str, range: ValueRange) -> DependencyKey {
        DependencyKey { collection: collection.to_owned(), field: field.to_owned(), range }
    }

    #[test]
    fn equality_ranges_match_only_their_value() {
        let mut index = DependencyIndex::new();
        index.register(
            7,
            key("orders", "status", ValueRange::equals(FieldValue::Text("paid".into()))),
            false,
        );
        let row = CommittedRow {
            collection: "orders",
            document: "o-1",
            extracted: vec![("status", FieldValue::Text("paid".into()))],
        };
        assert_eq!(index.match_row(&row), vec![7]);
        let other = CommittedRow {
            collection: "orders",
            document: "o-2",
            extracted: vec![("status", FieldValue::Text("open".into()))],
        };
        assert!(index.match_row(&other).is_empty());
    }

    #[test]
    fn range_dependencies_match_inside_and_not_outside() {
        let mut index = DependencyIndex::new();
        index.register(
            1,
            key(
                "orders",
                "total",
                ValueRange {
                    start: super::RangeEnd::Included(FieldValue::Number(100.0)),
                    end: super::RangeEnd::Excluded(FieldValue::Number(200.0)),
                },
            ),
            false,
        );
        for (total, matches) in [(100, true), (150, true), (199, true), (200, false), (99, false)] {
            let row = CommittedRow {
                collection: "orders",
                document: "x",
                extracted: vec![("total", FieldValue::Number(f64::from(total)))],
            };
            assert_eq!(
                index.match_row(&row),
                if matches { vec![1] } else { Vec::<u64>::new() },
                "total {total}"
            );
        }
    }

    #[test]
    fn unbounded_dependencies_match_every_row_of_the_collection() {
        let mut index = DependencyIndex::new();
        index.register(3, key("orders", "status", ValueRange::all()), false);
        let row = CommittedRow {
            collection: "orders",
            document: "o-9",
            extracted: vec![("status", FieldValue::Text("whatever".into()))],
        };
        assert_eq!(index.match_row(&row), vec![3]);
        let elsewhere = CommittedRow {
            collection: "users",
            document: "u-1",
            extracted: vec![("status", FieldValue::Text("whatever".into()))],
        };
        assert!(index.match_row(&elsewhere).is_empty(), "the collection is part of the key");
    }

    #[test]
    fn one_row_hits_each_affected_subscription_once_in_order() {
        let mut index = DependencyIndex::new();
        index.register(
            9,
            key("orders", "status", ValueRange::equals(FieldValue::Text("paid".into()))),
            false,
        );
        index.register(2, key("orders", "status", ValueRange::all()), false);
        index.register(
            5,
            key(
                "orders",
                "total",
                ValueRange { start: super::RangeEnd::Unbounded, end: super::RangeEnd::Unbounded },
            ),
            false,
        );
        let row = CommittedRow {
            collection: "orders",
            document: "o-1",
            extracted: vec![
                ("status", FieldValue::Text("paid".into())),
                ("total", FieldValue::Number(42.0)),
            ],
        };
        assert_eq!(index.match_row(&row), vec![2, 5, 9]);
    }

    #[test]
    fn retract_removes_every_dependency_of_the_subscription() {
        let mut index = DependencyIndex::new();
        let status = key("orders", "status", ValueRange::all());
        let total = key("orders", "total", ValueRange::equals(FieldValue::Number(1.0)));
        index.register(4, status.clone(), false);
        index.register(4, total.clone(), false);
        assert!(index.is_registered(4, &status) && index.is_registered(4, &total));
        index.retract(4);
        assert!(!index.is_registered(4, &status) && !index.is_registered(4, &total));
    }

    #[test]
    fn register_with_remove_retracts_one_key() {
        let mut index = DependencyIndex::new();
        let status = key("orders", "status", ValueRange::all());
        index.register(6, status.clone(), false);
        index.register(6, status.clone(), true);
        assert!(!index.is_registered(6, &status));
    }

    #[test]
    fn rows_without_extracted_values_match_only_unbounded_keys_of_other_fields() {
        let mut index = DependencyIndex::new();
        index.register(8, key("orders", "status", ValueRange::all()), false);
        let row = CommittedRow { collection: "orders", document: "o-1", extracted: Vec::new() };
        // No extracted value for `status`: the row cannot prove membership.
        assert!(index.match_row(&row).is_empty());
    }
}
