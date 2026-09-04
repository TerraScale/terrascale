//! Typed field-level merge for cross-region replication (S-05 D-36).
//!
//! Document-level causal metadata stays the default: one hybrid stamp plus
//! origin per document. A clean apply writes bytes. A true concurrent
//! conflict — both regions hold a live object body for the same key —
//! parses both bodies and merges per field: the remove-wins map default
//! plus the `(hlc, origin)` tiebreak, or a declared collection's typed
//! kernels (PN-counter, add-wins/remove-wins set, MV register). Only
//! declared collections store per-field kernels; schema-free collections
//! store none. Every merge renders a deterministic explanation so both
//! regions record the same loser history.

use std::cmp::Ordering;
use std::collections::{BTreeMap, BTreeSet};

use crate::storage::{MergeFieldKind, MergePolicy, MergeSchema};
use crate::types::DocumentValue;

/// Compares two `(hlc, origin)` stamps: physical stamp first, origin text
/// second. Total, deterministic, and shared by every merge decision.
#[must_use]
pub(crate) fn compare_stamps(
    left_hlc: u64,
    left_origin: &str,
    right_hlc: u64,
    right_origin: &str,
) -> Ordering {
    (left_hlc, left_origin).cmp(&(right_hlc, right_origin))
}

/// Returns the canonical sort key of one value: distinct across types, so
/// an integer `1`, a float `1.0`, and the string `"1"` never collide, and
/// arrays and objects render deterministically.
#[must_use]
pub(crate) fn canonical_key(value: &DocumentValue) -> String {
    match value {
        DocumentValue::Null => "null".to_owned(),
        DocumentValue::Boolean(true) => "bool:true".to_owned(),
        DocumentValue::Boolean(false) => "bool:false".to_owned(),
        DocumentValue::Integer(integer) => format!("int:{integer}"),
        DocumentValue::Number(number) => format!("float:{:x}", number.to_bits()),
        DocumentValue::String(text) => format!("str:{}:{text}", text.len()),
        DocumentValue::Array(elements) => {
            let inner = elements.iter().map(canonical_key).collect::<Vec<_>>().join(",");
            format!("array:[{inner}]")
        }
        DocumentValue::Object(fields) => {
            let inner = fields
                .iter()
                .map(|(key, member)| format!("{}={}", key.len(), canonical_key(member)))
                .collect::<Vec<_>>()
                .join(",");
            format!("object:{{{inner}}}")
        }
    }
}

/// Renders one value for a deterministic explanation: short, typed, and
/// stable across regions.
#[must_use]
pub(crate) fn render_value(value: &DocumentValue) -> String {
    match value {
        DocumentValue::Null => "null".to_owned(),
        DocumentValue::Boolean(flag) => flag.to_string(),
        DocumentValue::Integer(integer) => integer.to_string(),
        DocumentValue::Number(number) => {
            if number.fract() == 0.0 && number.is_finite() {
                format!("{number:.1}")
            } else {
                format!("{number}")
            }
        }
        DocumentValue::String(text) => format!("{text:?}"),
        DocumentValue::Array(elements) => {
            let inner = elements.iter().map(render_value).collect::<Vec<_>>().join(", ");
            format!("[{inner}]")
        }
        DocumentValue::Object(fields) => {
            let inner = fields
                .iter()
                .map(|(key, member)| format!("{key:?}: {}", render_value(member)))
                .collect::<Vec<_>>()
                .join(", ");
            format!("{{{inner}}}")
        }
    }
}

/// The per-field outcome of one merge: the value to keep (or `None` for a
/// removal) plus the policy that decided it when the sides disagreed.
struct FieldOutcome {
    value: Option<DocumentValue>,
    policy: Option<MergePolicy>,
    note: Option<String>,
}

/// Merges one field with the schema-free default: remove-wins map presence
/// plus the `(hlc, origin)` tiebreak for concurrent assignments.
fn merge_default_field(
    field: &str,
    current: Option<&DocumentValue>,
    incoming: Option<&DocumentValue>,
    current_hlc: u64,
    current_origin: &str,
    incoming_hlc: u64,
    incoming_origin: &str,
) -> FieldOutcome {
    match (current, incoming) {
        (None, None) => FieldOutcome { value: None, policy: None, note: None },
        (Some(value), None) | (None, Some(value)) => {
            let _ = value;
            FieldOutcome {
                value: None,
                policy: Some(MergePolicy::RemoveWins),
                note: Some(format!("field {field:?}: remove-wins (absent beats present)")),
            }
        }
        (Some(left), Some(right)) => {
            if left == right {
                FieldOutcome { value: Some(left.clone()), policy: None, note: None }
            } else {
                let winner =
                    if compare_stamps(current_hlc, current_origin, incoming_hlc, incoming_origin)
                        == Ordering::Greater
                    {
                        left
                    } else {
                        right
                    };
                FieldOutcome {
                    value: Some(winner.clone()),
                    policy: Some(MergePolicy::HlcTiebreak),
                    note: Some(format!(
                        "field {field:?}: hlc-tiebreak {} vs {} -> {}",
                        render_value(left),
                        render_value(right),
                        render_value(winner),
                    )),
                }
            }
        }
    }
}

/// Merges one declared PN-counter field: integers converge to the highest
/// observed value (the idempotent full-state form). A missing side reads
/// as absent and the present side wins; non-integers fall back to the
/// default tiebreak.
fn merge_counter_field(
    field: &str,
    current: Option<&DocumentValue>,
    incoming: Option<&DocumentValue>,
    current_hlc: u64,
    current_origin: &str,
    incoming_hlc: u64,
    incoming_origin: &str,
) -> FieldOutcome {
    match (current, incoming) {
        (Some(DocumentValue::Integer(left)), Some(DocumentValue::Integer(right))) => {
            if left == right {
                FieldOutcome {
                    value: Some(current.cloned().unwrap_or(DocumentValue::Null)),
                    policy: None,
                    note: None,
                }
            } else {
                let winner = (*left).max(*right);
                FieldOutcome {
                    value: Some(DocumentValue::Integer(winner)),
                    policy: Some(MergePolicy::PnCounterMax),
                    note: Some(format!(
                        "field {field:?}: pn-counter-max {left} vs {right} -> {winner}"
                    )),
                }
            }
        }
        (Some(DocumentValue::Integer(value)), None)
        | (None, Some(DocumentValue::Integer(value))) => FieldOutcome {
            value: Some(DocumentValue::Integer(*value)),
            policy: Some(MergePolicy::PnCounterMax),
            note: Some(format!("field {field:?}: pn-counter-max present {value} vs absent")),
        },
        _ => merge_default_field(
            field,
            current,
            incoming,
            current_hlc,
            current_origin,
            incoming_hlc,
            incoming_origin,
        ),
    }
}

/// Collects the distinct array elements of one set field in canonical
/// order.
fn distinct_sorted(elements: &[DocumentValue]) -> Vec<DocumentValue> {
    let mut seen: BTreeSet<String> = BTreeSet::new();
    let mut ordered: Vec<(String, DocumentValue)> = Vec::new();
    for element in elements {
        let key = canonical_key(element);
        if seen.insert(key.clone()) {
            ordered.push((key, element.clone()));
        }
    }
    ordered.sort_by(|left, right| left.0.cmp(&right.0));
    ordered.into_iter().map(|(_, value)| value).collect()
}

/// Merges one declared add-wins set field: arrays converge by union. A
/// missing side reads as the empty set, so a present side survives —
/// unlike the remove-wins default. Non-arrays fall back to the default.
fn merge_add_wins_field(
    field: &str,
    current: Option<&DocumentValue>,
    incoming: Option<&DocumentValue>,
) -> Option<FieldOutcome> {
    match (current, incoming) {
        (Some(DocumentValue::Array(left)), Some(DocumentValue::Array(right))) => {
            if left == right {
                Some(FieldOutcome {
                    value: Some(current.cloned().unwrap_or(DocumentValue::Null)),
                    policy: None,
                    note: None,
                })
            } else {
                let mut combined = left.clone();
                combined.extend(right.iter().cloned());
                let union = distinct_sorted(&combined);
                Some(FieldOutcome {
                    value: Some(DocumentValue::Array(union.clone())),
                    policy: Some(MergePolicy::AddWinsUnion),
                    note: Some(format!(
                        "field {field:?}: add-wins-union {} vs {} -> {}",
                        render_value(&DocumentValue::Array(left.clone())),
                        render_value(&DocumentValue::Array(right.clone())),
                        render_value(&DocumentValue::Array(union)),
                    )),
                })
            }
        }
        (Some(DocumentValue::Array(values)), None) | (None, Some(DocumentValue::Array(values))) => {
            Some(FieldOutcome {
                value: Some(DocumentValue::Array(distinct_sorted(values))),
                policy: Some(MergePolicy::AddWinsUnion),
                note: Some(format!(
                    "field {field:?}: add-wins-union present {} vs absent",
                    render_value(&DocumentValue::Array(values.clone())),
                )),
            })
        }
        _ => None,
    }
}

/// Merges one declared remove-wins set field: arrays converge by
/// intersection, so a concurrent remove wins. A missing side empties the
/// field. Non-arrays fall back to the default.
fn merge_remove_wins_field(
    field: &str,
    current: Option<&DocumentValue>,
    incoming: Option<&DocumentValue>,
) -> Option<FieldOutcome> {
    match (current, incoming) {
        (Some(DocumentValue::Array(left)), Some(DocumentValue::Array(right))) => {
            if left == right {
                Some(FieldOutcome {
                    value: Some(current.cloned().unwrap_or(DocumentValue::Null)),
                    policy: None,
                    note: None,
                })
            } else {
                let right_keys: BTreeSet<String> = right.iter().map(canonical_key).collect();
                let kept: Vec<DocumentValue> = distinct_sorted(left)
                    .into_iter()
                    .filter(|element| right_keys.contains(&canonical_key(element)))
                    .collect();
                let rendered = render_value(&DocumentValue::Array(kept.clone()));
                let value = (!kept.is_empty()).then_some(DocumentValue::Array(kept));
                Some(FieldOutcome {
                    value,
                    policy: Some(MergePolicy::RemoveWinsIntersection),
                    note: Some(format!(
                        "field {field:?}: remove-wins-intersection {} vs {} -> {rendered}",
                        render_value(&DocumentValue::Array(left.clone())),
                        render_value(&DocumentValue::Array(right.clone())),
                    )),
                })
            }
        }
        (Some(_), None) | (None, Some(_)) => Some(FieldOutcome {
            value: None,
            policy: Some(MergePolicy::RemoveWinsIntersection),
            note: Some(format!(
                "field {field:?}: remove-wins-intersection (absent empties the set)"
            )),
        }),
        _ => None,
    }
}

/// Flattens one MV candidate side into its distinct member values: a prior
/// union array expands one level, any other value is one candidate.
fn mv_candidates(value: &DocumentValue) -> Vec<DocumentValue> {
    match value {
        DocumentValue::Array(elements) => distinct_sorted(elements),
        single => vec![single.clone()],
    }
}

/// Merges one declared MV-register field: concurrent distinct values are
/// all kept as a sorted array. A missing side is a removal and wins like
/// the default.
fn merge_mv_field(
    field: &str,
    current: Option<&DocumentValue>,
    incoming: Option<&DocumentValue>,
) -> Option<FieldOutcome> {
    match (current, incoming) {
        (Some(left), Some(right)) => {
            if left == right {
                Some(FieldOutcome { value: Some(left.clone()), policy: None, note: None })
            } else {
                let mut combined = mv_candidates(left);
                combined.extend(mv_candidates(right));
                let union = distinct_sorted(&combined);
                Some(FieldOutcome {
                    value: Some(DocumentValue::Array(union.clone())),
                    policy: Some(MergePolicy::MvUnion),
                    note: Some(format!(
                        "field {field:?}: mv-union {} vs {} -> {}",
                        render_value(left),
                        render_value(right),
                        render_value(&DocumentValue::Array(union)),
                    )),
                })
            }
        }
        (Some(_), None) | (None, Some(_)) => Some(FieldOutcome {
            value: None,
            policy: Some(MergePolicy::RemoveWins),
            note: Some(format!("field {field:?}: remove-wins (absent beats present)")),
        }),
        _ => None,
    }
}

/// Merges two live object bodies field by field.
///
/// `current_*` names the stored row, `incoming_*` the replicated record;
/// `schema` is the collection's declared merge schema, if any. Returns the
/// merged fields, the overall deterministic policy, and the deterministic
/// explanation both regions record.
#[allow(
    clippy::too_many_arguments,
    clippy::too_many_lines,
    reason = "the merge's fixed dimensions"
)]
#[must_use]
pub(crate) fn merge_object_fields(
    current: &BTreeMap<String, DocumentValue>,
    current_hlc: u64,
    current_origin: &str,
    current_commit: &str,
    incoming: &BTreeMap<String, DocumentValue>,
    incoming_hlc: u64,
    incoming_origin: &str,
    incoming_commit: &str,
    schema: Option<&MergeSchema>,
) -> (BTreeMap<String, DocumentValue>, MergePolicy, String) {
    let mut names: BTreeSet<&String> = BTreeSet::new();
    names.extend(current.keys());
    names.extend(incoming.keys());
    let mut merged: BTreeMap<String, DocumentValue> = BTreeMap::new();
    let mut policies: BTreeSet<MergePolicy> = BTreeSet::new();
    let mut notes: Vec<String> = Vec::new();
    for field in names {
        let left = current.get(field);
        let right = incoming.get(field);
        let kind = schema.and_then(|declared| declared.get(field.as_str()));
        let outcome = match kind {
            None => merge_default_field(
                field,
                left,
                right,
                current_hlc,
                current_origin,
                incoming_hlc,
                incoming_origin,
            ),
            Some(MergeFieldKind::PnCounter) => merge_counter_field(
                field,
                left,
                right,
                current_hlc,
                current_origin,
                incoming_hlc,
                incoming_origin,
            ),
            Some(MergeFieldKind::AddWinsSet) => merge_add_wins_field(field, left, right)
                .unwrap_or_else(|| {
                    merge_default_field(
                        field,
                        left,
                        right,
                        current_hlc,
                        current_origin,
                        incoming_hlc,
                        incoming_origin,
                    )
                }),
            Some(MergeFieldKind::RemoveWinsSet) => merge_remove_wins_field(field, left, right)
                .unwrap_or_else(|| {
                    merge_default_field(
                        field,
                        left,
                        right,
                        current_hlc,
                        current_origin,
                        incoming_hlc,
                        incoming_origin,
                    )
                }),
            Some(MergeFieldKind::MvRegister) => {
                merge_mv_field(field, left, right).unwrap_or_else(|| {
                    merge_default_field(
                        field,
                        left,
                        right,
                        current_hlc,
                        current_origin,
                        incoming_hlc,
                        incoming_origin,
                    )
                })
            }
        };
        if let Some(policy) = outcome.policy {
            policies.insert(policy);
        }
        if let Some(note) = outcome.note {
            notes.push(note);
        }
        if let Some(value) = outcome.value {
            merged.insert(field.clone(), value);
        }
    }
    let overall = match policies.len() {
        0 => MergePolicy::HlcTiebreak,
        1 => policies.iter().next().copied().unwrap_or(MergePolicy::HlcTiebreak),
        _ => MergePolicy::Mixed,
    };
    let (winner_hlc, winner_origin) =
        if compare_stamps(current_hlc, current_origin, incoming_hlc, incoming_origin)
            == Ordering::Greater
        {
            (current_hlc, current_origin)
        } else {
            (incoming_hlc, incoming_origin)
        };
    let mut explanation = format!(
        "merge {current_commit} (hlc {current_hlc}, origin '{current_origin}') vs \
         {incoming_commit} (hlc {incoming_hlc}, origin '{incoming_origin}'): \
         policy {}; winner (hlc {winner_hlc}, origin '{winner_origin}')",
        overall.as_str(),
    );
    if notes.is_empty() {
        explanation.push_str("; identical bodies");
    } else {
        explanation.push_str("; ");
        explanation.push_str(&notes.join("; "));
    }
    (merged, overall, explanation)
}

#[cfg(test)]
mod tests {
    use super::{compare_stamps, merge_object_fields};
    use crate::storage::{MergeFieldKind, MergeSchema};
    use crate::types::DocumentValue;
    use std::cmp::Ordering;
    use std::collections::BTreeMap;

    fn fields(pairs: &[(&str, DocumentValue)]) -> BTreeMap<String, DocumentValue> {
        pairs.iter().map(|(key, value)| ((*key).to_owned(), value.clone())).collect()
    }

    #[test]
    fn stamps_order_by_hlc_then_origin() {
        assert_eq!(compare_stamps(2, "a", 1, "z"), Ordering::Greater);
        assert_eq!(compare_stamps(1, "b", 1, "a"), Ordering::Greater);
        assert_eq!(compare_stamps(1, "a", 1, "a"), Ordering::Equal);
    }

    #[test]
    fn default_map_keeps_disjoint_fields_and_tiebreaks_shared_ones() {
        let current =
            fields(&[("keep", DocumentValue::Integer(1)), ("shared", DocumentValue::Integer(1))]);
        let incoming =
            fields(&[("fresh", DocumentValue::Integer(2)), ("shared", DocumentValue::Integer(2))]);
        // Incoming stamp wins, so `shared` takes the incoming value while
        // the disjoint present-vs-absent fields are removals: both drop.
        let (merged, _, _) =
            merge_object_fields(&current, 1, "a", "c1", &incoming, 2, "a", "c2", None);
        assert_eq!(merged.get("shared"), Some(&DocumentValue::Integer(2)));
        assert!(!merged.contains_key("keep"), "remove-wins drops one-sided fields");
        assert!(!merged.contains_key("fresh"), "remove-wins drops one-sided fields");
    }

    #[test]
    fn counter_max_beats_hlc_order() {
        let schema =
            MergeSchema::try_new(BTreeMap::from([("count".to_owned(), MergeFieldKind::PnCounter)]))
                .expect("the schema parses");
        let current = fields(&[("count", DocumentValue::Integer(5))]);
        let incoming = fields(&[("count", DocumentValue::Integer(7))]);
        // Current stamp is higher, yet the counter keeps the higher value.
        let (merged, policy, _) =
            merge_object_fields(&current, 20, "a", "c1", &incoming, 10, "b", "c2", Some(&schema));
        assert_eq!(merged.get("count"), Some(&DocumentValue::Integer(7)));
        assert_eq!(policy, crate::storage::MergePolicy::PnCounterMax);
    }

    #[test]
    fn sets_union_and_intersect_deterministically() {
        let schema = MergeSchema::try_new(BTreeMap::from([
            ("aw".to_owned(), MergeFieldKind::AddWinsSet),
            ("rw".to_owned(), MergeFieldKind::RemoveWinsSet),
        ]))
        .expect("the schema parses");
        let current = fields(&[
            ("aw", DocumentValue::Array(vec![DocumentValue::Integer(1)])),
            (
                "rw",
                DocumentValue::Array(vec![DocumentValue::Integer(1), DocumentValue::Integer(2)]),
            ),
        ]);
        let incoming = fields(&[
            ("aw", DocumentValue::Array(vec![DocumentValue::Integer(2)])),
            ("rw", DocumentValue::Array(vec![DocumentValue::Integer(2)])),
        ]);
        let (merged, policy, explanation) =
            merge_object_fields(&current, 1, "a", "c1", &incoming, 2, "b", "c2", Some(&schema));
        assert_eq!(
            merged.get("aw"),
            Some(&DocumentValue::Array(vec![DocumentValue::Integer(1), DocumentValue::Integer(2)])),
            "add-wins keeps both adds"
        );
        assert_eq!(
            merged.get("rw"),
            Some(&DocumentValue::Array(vec![DocumentValue::Integer(2)])),
            "remove-wins keeps only the common element"
        );
        assert_eq!(policy, crate::storage::MergePolicy::Mixed);
        assert!(explanation.contains("add-wins-union"));
        assert!(explanation.contains("remove-wins-intersection"));
    }

    #[test]
    fn mv_union_keeps_both_values_sorted() {
        let schema =
            MergeSchema::try_new(BTreeMap::from([("reg".to_owned(), MergeFieldKind::MvRegister)]))
                .expect("the schema parses");
        let current = fields(&[("reg", DocumentValue::String("b".to_owned()))]);
        let incoming = fields(&[("reg", DocumentValue::String("a".to_owned()))]);
        let (merged, policy, _) =
            merge_object_fields(&current, 1, "a", "c1", &incoming, 2, "b", "c2", Some(&schema));
        assert_eq!(
            merged.get("reg"),
            Some(&DocumentValue::Array(vec![
                DocumentValue::String("a".to_owned()),
                DocumentValue::String("b".to_owned()),
            ])),
            "mv keeps both values in canonical order"
        );
        assert_eq!(policy, crate::storage::MergePolicy::MvUnion);
    }
}
