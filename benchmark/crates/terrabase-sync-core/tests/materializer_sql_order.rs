//! SQL filtering, top-k, and explicit null ordering in the incremental plan.

use terrabase_sync_core::view::{
    OrderKey,
    plan::{
        Compare, ExtractedRow, FtsPredicate, Materializer, Predicate, Value, ViewPlan, ZSetDelta,
    },
};

fn row(id: &str, score: f64) -> ExtractedRow {
    ExtractedRow::new("orders", id)
        .with_value("score", Value::Number(score))
        .with_value("status", Value::Text("paid".into()))
        .with_fts("needle")
}

fn ids(materializer: &Materializer) -> Vec<&str> {
    materializer.result().iter().map(|item| item.identity.as_str()).collect()
}

#[test]
fn filtering_precedes_top_k_and_deletions_refill_the_result() {
    let plan = ViewPlan::new(
        "orders",
        vec![Predicate {
            field: "status".into(),
            compare: Compare::Equal,
            value: Value::Text("paid".into()),
        }],
        vec!["score".into()],
        Some(FtsPredicate { query: "needle".into() }),
        vec![OrderKey { field: "score".into(), descending: false, nulls_last: true }],
        2,
    )
    .unwrap();
    let mut materializer = Materializer::new(plan);
    let mut rows = Vec::new();
    for position in 0..24 {
        let mut rejected = row(&format!("rejected-{position}"), f64::from(position));
        match position % 3 {
            0 => {
                rejected = rejected.with_value("status", Value::Text("open".into()));
            }
            1 => {
                rejected.fts = "unrelated text".into();
            }
            _ => {
                rejected.collection = "foreign".into();
            }
        }
        rows.push(rejected);
    }
    let a = row("a", 100.0);
    let b = row("b", 101.0);
    rows.extend([a.clone(), b.clone(), row("c", 102.0), row("d", 103.0)]);
    materializer.seed(rows).unwrap();
    assert_eq!(ids(&materializer), ["a", "b"]);
    assert_eq!(materializer.boundary_len(), 4);
    materializer.apply(&ZSetDelta::remove(a)).unwrap();
    assert_eq!(ids(&materializer), ["b", "c"]);
    assert_eq!(materializer.boundary_len(), 3);
    let changed = b.clone().with_value("status", Value::Text("open".into()));
    materializer.apply(&ZSetDelta::transition(Some(b), Some(changed))).unwrap();
    assert_eq!(ids(&materializer), ["c", "d"]);
    assert_eq!(materializer.boundary_len(), 2);
}

#[test]
fn explicit_null_placement_is_independent_of_sort_direction() {
    for (descending, nulls_last, expected) in [
        (false, false, ["a-missing", "b-null", "c-low", "d-high"]),
        (false, true, ["c-low", "d-high", "a-missing", "b-null"]),
        (true, false, ["a-missing", "b-null", "d-high", "c-low"]),
        (true, true, ["d-high", "c-low", "a-missing", "b-null"]),
    ] {
        let plan = ViewPlan::new(
            "orders",
            vec![],
            vec![],
            None,
            vec![OrderKey { field: "score".into(), descending, nulls_last }],
            4,
        )
        .unwrap();
        let mut materializer = Materializer::new(plan);
        let rows = [
            ExtractedRow::new("orders", "a-missing"),
            ExtractedRow::new("orders", "b-null").with_value("score", Value::Null),
            row("c-low", 1.0),
            row("d-high", 2.0),
        ];
        materializer.seed(rows.iter().rev().cloned()).unwrap();
        assert_eq!(ids(&materializer), expected);
        materializer.seed([]).unwrap();
        for value in rows {
            materializer.apply(&ZSetDelta::insert(value)).unwrap();
        }
        assert_eq!(ids(&materializer), expected);
    }
}

#[test]
fn foreign_collection_cannot_replace_or_remove_an_owned_identity() {
    let plan = ViewPlan::new("orders", vec![], vec![], None, vec![], 2).unwrap();
    let mut materializer = Materializer::new(plan);
    let owned = row("same-id", 1.0);
    let mut foreign = row("same-id", 2.0);
    foreign.collection = "foreign".into();
    materializer.seed([owned, foreign.clone()]).unwrap();
    let original = materializer.result().to_vec();
    assert_eq!(original.len(), 1);
    assert!(materializer.apply(&ZSetDelta::insert(foreign.clone())).unwrap().is_empty());
    assert_eq!(materializer.result(), original);
    assert!(materializer.apply(&ZSetDelta::remove(foreign)).unwrap().is_empty());
    assert_eq!(materializer.result(), original);
}

#[test]
fn scalar_comparisons_with_null_do_not_admit_rows() {
    let missing = ExtractedRow::new("orders", "missing");
    let null = missing.clone().with_value("score", Value::Null);
    let number = missing.clone().with_value("score", Value::Number(1.0));
    for compare in [
        Compare::Equal,
        Compare::NotEqual,
        Compare::Less,
        Compare::LessOrEqual,
        Compare::Greater,
        Compare::GreaterOrEqual,
    ] {
        let predicate = Predicate { field: "score".into(), compare, value: Value::Number(1.0) };
        assert!(!predicate.matches(&missing));
        assert!(!predicate.matches(&null));
        let predicate = Predicate { value: Value::Null, ..predicate };
        assert!(!predicate.matches(&missing));
        assert!(!predicate.matches(&null));
        assert!(!predicate.matches(&number));
    }
}

#[test]
fn ordered_index_matches_recomputation_through_replacements_and_refills() {
    use std::collections::BTreeMap;

    for descending in [false, true] {
        for nulls_last in [false, true] {
            let plan = ViewPlan::new(
                "orders",
                vec![Predicate {
                    field: "status".into(),
                    compare: Compare::Equal,
                    value: Value::Text("paid".into()),
                }],
                vec!["score".into(), "status".into()],
                Some(FtsPredicate { query: "needle".into() }),
                vec![
                    OrderKey { field: "score".into(), descending, nulls_last },
                    OrderKey { field: "tie".into(), descending: true, nulls_last: false },
                ],
                7,
            )
            .unwrap();
            let mut materializer = Materializer::new(plan.clone());
            let mut source = BTreeMap::<String, ExtractedRow>::new();
            let mut random = 17_u64;
            for step in 0..1_000 {
                random = random.wrapping_mul(6_364_136_223_846_793_005).wrapping_add(1);
                let id = format!("row-{}", (random >> 32) % 120);
                let mut next = row(&id, f64::from(step % 11))
                    .with_value("tie", Value::Number(f64::from(step % 3)));
                match step % 8 {
                    0 => {
                        next.values.remove("score");
                    }
                    1 => {
                        next.values.insert("score".into(), Value::Null);
                    }
                    2 => {
                        next.values.insert("status".into(), Value::Text("open".into()));
                    }
                    3 => {
                        next.fts = "unrelated".into();
                    }
                    _ => {}
                }
                let before = materializer.result().to_vec();
                let applied = if step % 5 == 0 {
                    source.remove(&id);
                    materializer.apply(&ZSetDelta::remove(next))
                } else {
                    source.insert(id, next.clone());
                    materializer.apply(&ZSetDelta::insert(next))
                };
                if applied == Err(terrabase_sync_core::view::plan::MaterializeError::RefillRequired)
                {
                    assert_eq!(
                        materializer.result(),
                        before,
                        "refusal must not publish partial state"
                    );
                    materializer.refill(source.values().cloned()).unwrap();
                } else {
                    applied.unwrap();
                }
                let mut eligible: Vec<_> =
                    source.values().filter(|row| plan.evaluate(row).is_some()).collect();
                eligible.sort_by(|left, right| oracle_order(left, right, descending, nulls_last));
                let expected: Vec<_> =
                    eligible.iter().take(plan.limit).filter_map(|row| plan.evaluate(row)).collect();
                assert_eq!(
                    materializer.result(),
                    expected,
                    "step {step}, descending={descending}, nulls_last={nulls_last}"
                );
                assert!(materializer.boundary_len() <= eligible.len().min(14));
                if step % 137 == 0 {
                    // Reseeding must clear both row state and its ordering index.
                    materializer.seed(source.values().cloned()).unwrap();
                    assert_eq!(materializer.result(), expected);
                }
            }
        }
    }
}

#[test]
fn bounded_candidates_refuse_an_incomplete_result_until_storage_refills() {
    use terrabase_sync_core::view::plan::MaterializeError;
    let plan = ViewPlan::new(
        "orders",
        vec![],
        vec!["score".into()],
        None,
        vec![OrderKey { field: "score".into(), descending: false, nulls_last: true }],
        2,
    )
    .unwrap();
    let mut materializer = Materializer::new(plan);
    materializer.seed((0..10_000).map(|id| row(&id.to_string(), f64::from(id)))).unwrap();
    assert_eq!(materializer.boundary_len(), 4);
    assert!(materializer.retained_bytes() < 10_000);
    materializer.apply(&ZSetDelta::remove(row("0", 0.0))).unwrap();
    materializer.apply(&ZSetDelta::insert(row("1", 20_000.0))).unwrap();
    assert_eq!(ids(&materializer), ["2", "3"], "the moved row must leave the known prefix");
    let before = materializer.result().to_vec();
    let bytes = materializer.retained_bytes();
    assert_eq!(
        materializer.apply(&ZSetDelta::remove(row("2", 2.0))),
        Err(MaterializeError::RefillRequired)
    );
    assert_eq!(materializer.result(), before);
    assert_eq!(materializer.retained_bytes(), bytes);
    let operations = materializer
        .refill(
            (3..10_000).map(|id| row(&id.to_string(), f64::from(id))).chain([row("1", 20_000.0)]),
        )
        .unwrap();
    assert!(!operations.is_empty());
    assert_eq!(ids(&materializer), ["3", "4"]);
    assert_eq!(materializer.boundary_len(), 4);
}

#[test]
fn candidate_byte_refusal_preserves_the_last_result() {
    use terrabase_sync_core::view::{MAX_VIEW_BYTES, plan::MaterializeError};
    let plan = ViewPlan::new("orders", vec![], vec![], None, vec![], 2).unwrap();
    let mut materializer = Materializer::new(plan);
    materializer.seed([row("kept", 1.0)]).unwrap();
    let before = materializer.result().to_vec();
    let bytes = materializer.retained_bytes();
    let mut oversized = ExtractedRow::new("orders", "oversized");
    oversized.values.insert("payload".into(), Value::Bytes(vec![0; MAX_VIEW_BYTES]));
    let delta = ZSetDelta::insert(oversized);
    assert_eq!(materializer.apply(&delta), Err(MaterializeError::ByteBudgetExceeded));
    assert_eq!(materializer.result(), before);
    assert_eq!(materializer.retained_bytes(), bytes);
    assert_eq!(
        materializer.refill(delta.entries.into_iter().map(|entry| entry.row)),
        Err(MaterializeError::ByteBudgetExceeded)
    );
    assert_eq!(materializer.result(), before);
}

fn oracle_order(
    left: &ExtractedRow,
    right: &ExtractedRow,
    descending: bool,
    nulls_last: bool,
) -> std::cmp::Ordering {
    use std::cmp::Ordering;
    let score = |row: &ExtractedRow| match row.values.get("score") {
        Some(Value::Number(value)) => Some(*value),
        _ => None,
    };
    let primary = match (score(left), score(right)) {
        (Some(a), Some(b)) => {
            if descending {
                b.total_cmp(&a)
            } else {
                a.total_cmp(&b)
            }
        }
        (None, None) => Ordering::Equal,
        (None, Some(_)) => {
            if nulls_last {
                Ordering::Greater
            } else {
                Ordering::Less
            }
        }
        (Some(_), None) => {
            if nulls_last {
                Ordering::Less
            } else {
                Ordering::Greater
            }
        }
    };
    primary
        .then_with(|| right.values["tie"].cmp(&left.values["tie"]))
        .then_with(|| left.identity.cmp(&right.identity))
}
