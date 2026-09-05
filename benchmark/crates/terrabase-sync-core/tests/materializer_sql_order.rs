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
    materializer.seed(rows);
    assert_eq!(ids(&materializer), ["a", "b"]);
    assert_eq!(materializer.boundary_len(), 4);
    materializer.apply(&ZSetDelta::remove(a));
    assert_eq!(ids(&materializer), ["b", "c"]);
    assert_eq!(materializer.boundary_len(), 3);
    let changed = b.clone().with_value("status", Value::Text("open".into()));
    materializer.apply(&ZSetDelta::transition(Some(b), Some(changed)));
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
        materializer.seed(rows.iter().rev().cloned());
        assert_eq!(ids(&materializer), expected);
        materializer.seed([]);
        for value in rows {
            materializer.apply(&ZSetDelta::insert(value));
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
    materializer.seed([owned, foreign.clone()]);
    let original = materializer.result().to_vec();
    assert_eq!(original.len(), 1);
    assert!(materializer.apply(&ZSetDelta::insert(foreign.clone())).is_empty());
    assert_eq!(materializer.result(), original);
    assert!(materializer.apply(&ZSetDelta::remove(foreign)).is_empty());
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
