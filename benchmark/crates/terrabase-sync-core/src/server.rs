//! The server-side subscription engine (`S-04` slice 2b, D-46).
//!
//! The member's tailer pulls committed rows off the durable publication
//! journal ([`Engine::streams`]' replay seam in the host layer), converts
//! each row ONCE into a [`RowEvent`] — extracted dependency values plus
//! the projected fields — and hands it to [`SubscriptionEngine::apply`].
//! The engine matches the row against every registered subscription's
//! dependency keys, re-evaluates the exact predicate only for the
//! subscriptions the index selected, and computes one delta per affected
//! subscription. The projection and membership answers ride the event,
//! so nothing re-parses a body and no query re-plans per write.
//!
//! M1 delta vocabulary: `Insert`, `Update`, `Remove` — the server always
//! holds the full projected value, so replacement (never a guessed
//! patch) is the honest emit; ordering moves, bounded top-k windows, and
//! patch proofs join with the ordering slice.

use std::collections::BTreeMap;

use crate::subscription::CommittedRow;
use crate::subscription::DependencyIndex;
use crate::subscription::FieldValue;

/// One membership comparison over an extracted field.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct Condition {
    /// The extracted field the predicate reads (dotted path form).
    pub field: String,
    /// The comparison.
    pub op: Comparison,
    /// The literal the field compares against.
    pub value: FieldValue,
}

/// The comparison kinds the M1 dependency model narrows.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum Comparison {
    /// Field equals the value.
    Equals,
    /// Field is strictly below the value.
    Less,
    /// Field is at or below the value.
    LessOrEqual,
    /// Field is strictly above the value.
    Greater,
    /// Field is at or above the value.
    GreaterOrEqual,
}

impl Condition {
    /// Evaluates the condition against one extracted value.
    #[must_use]
    pub fn matches(&self, value: &FieldValue) -> bool {
        use std::cmp::Ordering;
        match self.op {
            Comparison::Equals => value == &self.value,
            Comparison::Less => value.cmp(&self.value) == Ordering::Less,
            Comparison::LessOrEqual => value.cmp(&self.value) != Ordering::Greater,
            Comparison::Greater => value.cmp(&self.value) == Ordering::Greater,
            Comparison::GreaterOrEqual => value.cmp(&self.value) != Ordering::Less,
        }
    }
}

/// One subscription's server-side specification: the collection, the
/// conjunctive predicate over extracted fields, and the projected
/// fields each result item carries.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct SubscriptionSpec {
    /// The single collection the plan reads.
    pub collection: String,
    /// The conjunctive membership predicate.
    pub conditions: Vec<Condition>,
    /// The projected field paths, in declaration order.
    pub projection: Vec<String>,
}

/// One committed row converted once by the host tailer: the dependency
/// values the matcher needs and the projected fields the result items
/// carry, both read off the in-hand document value — never a re-parse.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct RowEvent {
    /// The row's collection.
    pub collection: String,
    /// The row's document identifier.
    pub document: String,
    /// The put's projected fields (`None` for a tombstone).
    pub put: Option<Vec<(String, FieldValue)>>,
    /// The extracted dependency values, in field order.
    pub extracted: Vec<(String, FieldValue)>,
}

impl RowEvent {
    /// The matcher's view of this row.
    #[must_use]
    pub fn committed_row(&self) -> CommittedRow<'_> {
        CommittedRow {
            collection: &self.collection,
            document: &self.document,
            extracted: self
                .extracted
                .iter()
                .map(|(field, value)| (field.as_str(), value.clone()))
                .collect(),
        }
    }
}

/// One delta operation emitted to one subscription.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct Delta {
    /// The subscription's revision after this delta.
    pub revision: u64,
    /// The operation.
    pub operation: DeltaOp,
}

/// The M1 delta vocabulary: replacement semantics, never a guess.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum DeltaOp {
    /// The document entered the result.
    Insert {
        /// The document identifier.
        document: String,
        /// The projected fields.
        fields: Vec<(String, FieldValue)>,
    },
    /// The document's projection changed inside the result.
    Update {
        /// The document identifier.
        document: String,
        /// The projected fields.
        fields: Vec<(String, FieldValue)>,
    },
    /// The document left the result (deleted or no longer matches).
    Remove {
        /// The document identifier.
        document: String,
    },
}

/// One registered subscription's serving state.
struct SubscriptionState {
    spec: SubscriptionSpec,
    /// The current result members: document → projected fields.
    members: BTreeMap<String, Vec<(String, FieldValue)>>,
    /// The subscription's revision: one per delta, from 1.
    revision: u64,
}

/// The member's subscription engine: registration, dependency matching,
/// and delta computation (D-46).
// ponytail: one global lock; per-collection shards if subscriber
// fan-out ever contends with the committer.
#[derive(Default)]
pub struct SubscriptionEngine {
    index: DependencyIndex,
    /// Subscriptions with no field predicate: every row of the
    /// collection is their dependency.
    collection_wide: BTreeMap<String, Vec<u64>>,
    /// The reverse membership: which subscriptions currently hold each
    /// document. The dependency index narrows by the row's NEW values,
    /// so a write that leaves a range must still reach the subscriptions
    /// the document's OLD membership put it in — the holders map is that
    /// half of the match.
    holders: BTreeMap<(String, String), std::collections::BTreeSet<u64>>,
    subscriptions: BTreeMap<u64, SubscriptionState>,
    next_id: u64,
}

impl SubscriptionEngine {
    /// Creates an empty engine.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// Registers one subscription and returns its identity. Registration
    /// derives the dependency keys from the spec's conditions: one key
    /// per condition (a sound narrowing — the exact predicate decides
    /// membership), or a collection-wide dependency when the predicate is
    /// empty.
    pub fn register(&mut self, spec: SubscriptionSpec) -> u64 {
        let id = self.next_id;
        self.next_id = self.next_id.saturating_add(1);
        if spec.conditions.is_empty() {
            self.collection_wide.entry(spec.collection.clone()).or_default().push(id);
        } else {
            for condition in &spec.conditions {
                let range = range_of(condition);
                self.index.register(
                    id,
                    crate::subscription::DependencyKey {
                        collection: spec.collection.clone(),
                        field: condition.field.clone(),
                        range,
                    },
                    false,
                );
            }
        }
        self.subscriptions
            .insert(id, SubscriptionState { spec, members: BTreeMap::new(), revision: 0 });
        id
    }

    /// Retracts one subscription.
    pub fn unregister(&mut self, id: u64) {
        self.index.retract(id);
        for wide in self.collection_wide.values_mut() {
            wide.retain(|owner| *owner != id);
        }
        self.collection_wide.retain(|_, wide| !wide.is_empty());
        self.holders.retain(|_, holders| {
            holders.remove(&id);
            !holders.is_empty()
        });
        self.subscriptions.remove(&id);
    }

    /// The number of live subscriptions.
    #[must_use]
    pub fn len(&self) -> usize {
        self.subscriptions.len()
    }

    /// Reports whether no subscription is live.
    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.subscriptions.is_empty()
    }

    /// One subscription's current result members, in document order.
    #[must_use]
    pub fn members(&self, id: u64) -> Option<&BTreeMap<String, Vec<(String, FieldValue)>>> {
        self.subscriptions.get(&id).map(|state| &state.members)
    }

    /// Applies one committed row and returns the deltas it produced, in
    /// ascending subscription order. The row is converted once; each
    /// affected subscription gets exactly one delta.
    pub fn apply(&mut self, event: &RowEvent) -> Vec<(u64, Delta)> {
        let mut affected = self.index.match_row(&event.committed_row());
        if let Some(wide) = self.collection_wide.get(&event.collection) {
            affected.extend(wide.iter().copied());
        }
        if let Some(holders) = self.holders.get(&(event.collection.clone(), event.document.clone()))
        {
            affected.extend(holders.iter().copied());
        }
        affected.sort_unstable();
        affected.dedup();

        let mut deltas = Vec::with_capacity(affected.len());
        for id in affected {
            let Some(state) = self.subscriptions.get_mut(&id) else {
                continue;
            };
            if state.spec.collection != event.collection {
                continue;
            }
            let matched = event.put.is_some()
                && state.spec.conditions.iter().all(|condition| {
                    field_of(&event.extracted, &condition.field)
                        .is_some_and(|value| condition.matches(value))
                });
            let fields = event.put.clone().unwrap_or_default();
            let holds = (self.holders)
                .get(&(event.collection.clone(), event.document.clone()))
                .is_some_and(|holders| holders.contains(&id));
            let operation = match (holds, matched) {
                (false, true) => {
                    self.holders
                        .entry((event.collection.clone(), event.document.clone()))
                        .or_default()
                        .insert(id);
                    state.members.insert(event.document.clone(), fields.clone());
                    DeltaOp::Insert { document: event.document.clone(), fields }
                }
                (true, true) => {
                    state.members.insert(event.document.clone(), fields.clone());
                    DeltaOp::Update { document: event.document.clone(), fields }
                }
                (true, false) => {
                    if let Some(holders) =
                        self.holders.get_mut(&(event.collection.clone(), event.document.clone()))
                    {
                        holders.remove(&id);
                        if holders.is_empty() {
                            self.holders
                                .remove(&(event.collection.clone(), event.document.clone()));
                        }
                    }
                    state.members.remove(&event.document);
                    DeltaOp::Remove { document: event.document.clone() }
                }
                (false, false) => continue,
            };
            state.revision = state.revision.saturating_add(1);
            deltas.push((id, Delta { revision: state.revision, operation }));
        }
        deltas
    }
}

/// The dependency range one condition narrows to. The range is the
/// condition's exact span for equality and a sound superset for the
/// orderings (the exact predicate still decides membership).
fn range_of(condition: &Condition) -> crate::subscription::ValueRange {
    use crate::subscription::RangeEnd;
    let value = condition.value.clone();
    let unbounded = RangeEnd::Unbounded;
    match condition.op {
        Comparison::Equals => crate::subscription::ValueRange::equals(value),
        Comparison::Less => {
            crate::subscription::ValueRange { start: unbounded, end: RangeEnd::Excluded(value) }
        }
        Comparison::LessOrEqual => {
            crate::subscription::ValueRange { start: unbounded, end: RangeEnd::Included(value) }
        }
        Comparison::Greater => {
            crate::subscription::ValueRange { start: RangeEnd::Excluded(value), end: unbounded }
        }
        Comparison::GreaterOrEqual => {
            crate::subscription::ValueRange { start: RangeEnd::Included(value), end: unbounded }
        }
    }
}

/// Looks one field up in the extracted values.
fn field_of<'values>(
    extracted: &'values [(String, FieldValue)],
    field: &str,
) -> Option<&'values FieldValue> {
    extracted.iter().find(|(name, _)| name == field).map(|(_, value)| value)
}

#[cfg(test)]
mod tests {
    use super::Comparison;
    use super::Condition;
    use super::FieldValue;
    use super::RowEvent;
    use super::SubscriptionEngine;
    use super::SubscriptionSpec;

    fn spec(conditions: Vec<Condition>) -> SubscriptionSpec {
        SubscriptionSpec {
            collection: "orders".into(),
            conditions,
            projection: vec!["total".into()],
        }
    }

    fn condition(field: &str, op: Comparison, value: FieldValue) -> Condition {
        Condition { field: field.into(), op, value }
    }

    fn row(document: &str, status: &str, total: i64) -> RowEvent {
        RowEvent {
            collection: "orders".into(),
            document: document.into(),
            put: Some(vec![("total".into(), FieldValue::Number(total))]),
            extracted: vec![
                ("status".into(), FieldValue::Text(status.into())),
                ("total".into(), FieldValue::Number(total)),
            ],
        }
    }

    #[test]
    fn equality_subscriptions_receive_insert_update_and_remove() {
        let mut engine = SubscriptionEngine::new();
        engine.register(spec(vec![condition(
            "status",
            Comparison::Equals,
            FieldValue::Text("paid".into()),
        )]));
        let deltas = engine.apply(&row("o-1", "paid", 10));
        assert_eq!(deltas.len(), 1);
        assert!(matches!(deltas[0].1.operation, super::DeltaOp::Insert { .. }));
        assert_eq!(deltas[0].1.revision, 1);
        // A second paid write updates the member.
        let deltas = engine.apply(&row("o-1", "paid", 12));
        assert!(matches!(deltas[0].1.operation, super::DeltaOp::Update { .. }));
        assert_eq!(deltas[0].1.revision, 2);
        // A status change removes it.
        let deltas = engine.apply(&row("o-1", "open", 12));
        assert!(matches!(deltas[0].1.operation, super::DeltaOp::Remove { .. }));
        // A tombstone of a non-member produces nothing.
        let mut delete = row("o-9", "paid", 1);
        delete.put = None;
        assert!(engine.apply(&delete).is_empty());
    }

    #[test]
    fn rows_outside_the_dependency_do_not_touch_the_subscription() {
        let mut engine = SubscriptionEngine::new();
        engine.register(spec(vec![condition(
            "status",
            Comparison::Equals,
            FieldValue::Text("paid".into()),
        )]));
        assert!(engine.apply(&row("o-2", "open", 5)).is_empty());
        assert!(engine.is_empty() || engine.len() == 1);
    }

    #[test]
    fn range_conditions_match_by_predicate_not_by_narrowing() {
        let mut engine = SubscriptionEngine::new();
        engine.register(spec(vec![condition(
            "total",
            Comparison::Greater,
            FieldValue::Number(100),
        )]));
        assert_eq!(engine.apply(&row("a", "paid", 150)).len(), 1);
        assert!(engine.apply(&row("b", "paid", 50)).is_empty());
        assert_eq!(engine.apply(&row("c", "paid", 101)).len(), 1);
    }

    #[test]
    fn conjunctive_conditions_all_must_hold() {
        let mut engine = SubscriptionEngine::new();
        engine.register(spec(vec![
            condition("status", Comparison::Equals, FieldValue::Text("paid".into())),
            condition("total", Comparison::LessOrEqual, FieldValue::Number(50)),
        ]));
        assert_eq!(engine.apply(&row("x", "paid", 50)).len(), 1);
        assert!(engine.apply(&row("y", "paid", 51)).is_empty());
        assert!(engine.apply(&row("z", "open", 10)).is_empty());
    }

    #[test]
    fn collection_wide_subscriptions_see_every_row() {
        let mut engine = SubscriptionEngine::new();
        let id = engine.register(spec(Vec::new()));
        assert_eq!(engine.apply(&row("any", "whatever", 0)).len(), 1);
        let members = engine.members(id).expect("live");
        assert!(members.contains_key("any"));
    }

    #[test]
    fn unregister_stops_delivery() {
        let mut engine = SubscriptionEngine::new();
        let id = engine.register(spec(Vec::new()));
        engine.unregister(id);
        assert!(engine.apply(&row("gone", "x", 0)).is_empty());
        assert!(engine.members(id).is_none());
    }

    #[test]
    fn a_tombstone_of_a_live_member_removes_it() {
        let mut engine = SubscriptionEngine::new();
        engine.register(spec(Vec::new()));
        engine.apply(&row("d", "paid", 1));
        let mut delete = row("d", "paid", 1);
        delete.put = None;
        let deltas = engine.apply(&delete);
        assert!(matches!(deltas[0].1.operation, super::DeltaOp::Remove { .. }));
    }
}
