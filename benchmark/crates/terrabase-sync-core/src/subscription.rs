//! Subscription keys and the interval structure that matches committed
//! rows against them (`S-04`, D-46).
//!
//! A subscription is a planned SQL query plus its dependency keys: the
//! `(collection, index field, value range)` triples its plan reads. Each
//! committed row is matched against those keys using the extracted values
//! the write path already produced — no re-planning per write — and every
//! subscription whose key the row touches gets one delta.

use std::cmp::Ordering;
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

/// One registered range and its owning subscription.
#[derive(Clone, Debug, Eq, PartialEq)]
struct IntervalEntry {
    key: DependencyKey,
    owner: u64,
}

/// One balanced interval-tree node, augmented with the farthest end in
/// its subtree. Registrations rebuild the tree; committed rows only run
/// point queries, which are the write-path operation D-46 keeps bounded.
#[derive(Debug)]
struct IntervalNode {
    entry: IntervalEntry,
    max_end: RangeEnd,
    left: Option<Box<Self>>,
    right: Option<Box<Self>>,
}

/// All dependency ranges for one `(collection, index field)` key.
#[derive(Default)]
struct IntervalTree {
    entries: Vec<IntervalEntry>,
    root: Option<Box<IntervalNode>>,
}

impl IntervalTree {
    fn insert(&mut self, key: DependencyKey, owner: u64) {
        self.entries.push(IntervalEntry { key, owner });
        self.rebuild();
    }

    fn retract(&mut self, owner: u64) {
        self.entries.retain(|entry| entry.owner != owner);
        self.rebuild();
    }

    fn is_empty(&self) -> bool {
        self.entries.is_empty()
    }

    #[cfg(test)]
    fn contains(&self, owner: u64, key: &DependencyKey) -> bool {
        self.entries.iter().any(|entry| entry.owner == owner && &entry.key == key)
    }

    fn match_point(&self, value: &FieldValue, affected: &mut Vec<u64>) {
        let mut visited = 0;
        visit_intervals(self.root.as_deref(), value, affected, &mut visited);
    }

    #[cfg(test)]
    fn match_point_counted(&self, value: &FieldValue) -> (Vec<u64>, usize) {
        let mut affected = Vec::new();
        let mut visited = 0;
        visit_intervals(self.root.as_deref(), value, &mut affected, &mut visited);
        (affected, visited)
    }

    fn rebuild(&mut self) {
        self.entries.sort_by(|left, right| {
            compare_starts(&left.key.range.start, &right.key.range.start)
                .then_with(|| compare_ends(&left.key.range.end, &right.key.range.end))
                .then_with(|| left.owner.cmp(&right.owner))
        });
        self.root = build_interval_tree(&self.entries);
    }
}

impl std::fmt::Debug for IntervalTree {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        formatter.debug_list().entries(&self.entries).finish()
    }
}

/// Builds a balanced search tree over lower bounds and records the farthest
/// upper bound reachable below every node.
fn build_interval_tree(entries: &[IntervalEntry]) -> Option<Box<IntervalNode>> {
    let (entry, left_entries, right_entries) = match entries.len() {
        0 => return None,
        len => {
            let middle = len / 2;
            (&entries[middle], &entries[..middle], &entries[middle + 1..])
        }
    };
    let left = build_interval_tree(left_entries);
    let right = build_interval_tree(right_entries);
    let mut max_end = entry.key.range.end.clone();
    if let Some(child) = &left {
        max_end = farther_end(max_end, &child.max_end);
    }
    if let Some(child) = &right {
        max_end = farther_end(max_end, &child.max_end);
    }
    Some(Box::new(IntervalNode { entry: entry.clone(), max_end, left, right }))
}

/// Point-searches one interval tree. The left subtree is skipped when its
/// farthest upper bound cannot reach the point; the right subtree is skipped
/// when this node's lower bound is already beyond it.
fn visit_intervals(
    node: Option<&IntervalNode>,
    value: &FieldValue,
    affected: &mut Vec<u64>,
    visited: &mut usize,
) {
    let Some(node) = node else {
        return;
    };
    *visited = visited.saturating_add(1);
    if node.left.as_ref().is_some_and(|left| end_reaches(&left.max_end, value)) {
        visit_intervals(node.left.as_deref(), value, affected, visited);
    }
    if node.entry.key.range.contains(value) {
        affected.push(node.entry.owner);
    }
    if start_reaches(&node.entry.key.range.start, value) {
        visit_intervals(node.right.as_deref(), value, affected, visited);
    }
}

/// Orders lower bounds as `-infinity`, then by value, with an included bound
/// before an excluded bound at the same value.
fn compare_starts(left: &RangeEnd, right: &RangeEnd) -> Ordering {
    match (left, right) {
        (RangeEnd::Unbounded, RangeEnd::Unbounded) => Ordering::Equal,
        (RangeEnd::Unbounded, _) => Ordering::Less,
        (_, RangeEnd::Unbounded) => Ordering::Greater,
        (RangeEnd::Included(left), RangeEnd::Included(right))
        | (RangeEnd::Excluded(left), RangeEnd::Excluded(right)) => left.cmp(right),
        (RangeEnd::Included(left), RangeEnd::Excluded(right)) => {
            left.cmp(right).then(Ordering::Less)
        }
        (RangeEnd::Excluded(left), RangeEnd::Included(right)) => {
            left.cmp(right).then(Ordering::Greater)
        }
    }
}

/// Orders upper bounds by reach. An unbounded upper end is positive infinity;
/// at the same value an included end reaches farther than an excluded end.
fn compare_ends(left: &RangeEnd, right: &RangeEnd) -> Ordering {
    match (left, right) {
        (RangeEnd::Unbounded, RangeEnd::Unbounded) => Ordering::Equal,
        (RangeEnd::Unbounded, _) => Ordering::Greater,
        (_, RangeEnd::Unbounded) => Ordering::Less,
        (RangeEnd::Included(left), RangeEnd::Included(right))
        | (RangeEnd::Excluded(left), RangeEnd::Excluded(right)) => left.cmp(right),
        (RangeEnd::Included(left), RangeEnd::Excluded(right)) => {
            left.cmp(right).then(Ordering::Greater)
        }
        (RangeEnd::Excluded(left), RangeEnd::Included(right)) => {
            left.cmp(right).then(Ordering::Less)
        }
    }
}

fn farther_end(current: RangeEnd, candidate: &RangeEnd) -> RangeEnd {
    if compare_ends(&current, candidate).is_lt() { candidate.clone() } else { current }
}

fn start_reaches(start: &RangeEnd, value: &FieldValue) -> bool {
    match start {
        RangeEnd::Unbounded => true,
        RangeEnd::Included(bound) => bound <= value,
        RangeEnd::Excluded(bound) => bound < value,
    }
}

fn end_reaches(end: &RangeEnd, value: &FieldValue) -> bool {
    match end {
        RangeEnd::Unbounded => true,
        RangeEnd::Included(bound) => bound >= value,
        RangeEnd::Excluded(bound) => bound > value,
    }
}

/// The dependency index: subscriptions keyed by their
/// `(collection, index field, range)` dependencies and matched by committed
/// rows through a max-end-augmented interval tree (D-46).
#[derive(Default)]
pub struct DependencyIndex {
    keys: BTreeMap<(String, String), IntervalTree>,
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
        let bucket = (key.collection.clone(), key.field.clone());
        if remove {
            if let Some(tree) = self.keys.get_mut(&bucket) {
                tree.retract(subscription);
                if tree.is_empty() {
                    self.keys.remove(&bucket);
                }
            }
            return;
        }
        self.keys.entry(bucket).or_default().insert(key, subscription);
    }

    /// Retracts every dependency of one subscription.
    pub fn retract(&mut self, subscription: u64) {
        self.keys.retain(|_, tree| {
            tree.retract(subscription);
            !tree.is_empty()
        });
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
            entries.match_point(value, &mut affected);
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
            .is_some_and(|tree| tree.contains(subscription, key))
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
    use super::IntervalTree;
    use super::RangeEnd;
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

    #[test]
    fn interval_tree_prunes_disjoint_ranges_for_a_point_query() {
        let mut tree = IntervalTree::default();
        for owner in 0..2_047_u32 {
            tree.insert(
                key("orders", "total", ValueRange::equals(FieldValue::Number(f64::from(owner)))),
                u64::from(owner),
            );
        }

        let (affected, visited) = tree.match_point_counted(&FieldValue::Number(1_023.5));
        assert!(affected.is_empty());
        assert!(
            visited <= 24,
            "a point query visited {visited} of 2,047 disjoint intervals instead of pruning"
        );
    }

    #[test]
    fn interval_tree_preserves_open_closed_and_unbounded_ranges() {
        let mut tree = IntervalTree::default();
        tree.insert(
            key(
                "orders",
                "total",
                ValueRange {
                    start: RangeEnd::Unbounded,
                    end: RangeEnd::Excluded(FieldValue::Number(10.0)),
                },
            ),
            1,
        );
        tree.insert(
            key(
                "orders",
                "total",
                ValueRange {
                    start: RangeEnd::Included(FieldValue::Number(10.0)),
                    end: RangeEnd::Unbounded,
                },
            ),
            2,
        );
        tree.insert(
            key(
                "orders",
                "total",
                ValueRange {
                    start: RangeEnd::Excluded(FieldValue::Number(5.0)),
                    end: RangeEnd::Included(FieldValue::Number(10.0)),
                },
            ),
            3,
        );

        let (mut below, _) = tree.match_point_counted(&FieldValue::Number(9.0));
        below.sort_unstable();
        assert_eq!(below, [1, 3]);

        let (mut boundary, _) = tree.match_point_counted(&FieldValue::Number(10.0));
        boundary.sort_unstable();
        assert_eq!(boundary, [2, 3]);
    }
}
