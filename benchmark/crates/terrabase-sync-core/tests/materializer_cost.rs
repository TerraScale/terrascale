//! Manual comparison of maintained ordering with full scan/sort refresh.

use std::collections::BTreeMap;
use std::hint::black_box;
use std::time::Instant;

use terrabase_sync_core::view::{
    OrderKey,
    plan::{ExtractedRow, Materializer, Value, ViewPlan, ZSetDelta, diff},
};

fn row(id: u32, score: f64) -> ExtractedRow {
    ExtractedRow::new("orders", format!("row-{id:06}")).with_value("score", Value::Number(score))
}

#[test]
#[ignore = "manual release-mode benchmark; writes Markdown rows to stdout"]
fn ordered_materializer_cost() {
    let updates = 200_u32;
    println!(
        "| Source rows | Updates | Full scan/sort ms | Bounded index ms | Speedup | Retained rows | Estimated candidate bytes |"
    );
    println!("| ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
    for count in [1_000_u32, 10_000, 100_000] {
        let plan = ViewPlan::new(
            "orders",
            vec![],
            vec!["score".into()],
            None,
            vec![OrderKey { field: "score".into(), descending: false, nulls_last: true }],
            25,
        )
        .unwrap();
        let mut source: BTreeMap<_, _> = (0..count)
            .map(|id| {
                let row = row(id, f64::from(id.wrapping_mul(48_271) % count));
                (row.identity.clone(), row)
            })
            .collect();
        let mut materializer = Materializer::new(plan.clone());
        materializer.seed(source.values().cloned()).unwrap();
        let mut previous = materializer.result().to_vec();
        let changes: Vec<_> =
            (0..updates).map(|step| row(step * 7_919 % count, -f64::from(step) - 1.0)).collect();
        // The reference reproduces the previous algorithm: retain source
        // rows, filter and sort references, then project and diff top-k.
        let start = Instant::now();
        for change in &changes {
            source.insert(change.identity.clone(), change.clone());
            let mut rows: Vec<_> =
                source.values().filter(|row| row.collection == plan.collection).collect();
            rows.sort_by(|left, right| {
                left.values["score"]
                    .cmp(&right.values["score"])
                    .then_with(|| left.identity.cmp(&right.identity))
            });
            let result: Vec<_> =
                rows.into_iter().take(plan.limit).filter_map(|row| plan.evaluate(row)).collect();
            black_box(diff(&previous, &result));
            previous = result;
        }
        let baseline = start.elapsed().as_secs_f64() * 1_000.0;
        let start = Instant::now();
        for change in changes {
            black_box(materializer.apply(&ZSetDelta::insert(change)).unwrap());
        }
        let indexed = start.elapsed().as_secs_f64() * 1_000.0;
        assert_eq!(materializer.result(), previous);
        println!(
            "| {count} | {updates} | {baseline:.3} | {indexed:.3} | {:.1}x | {} | {} |",
            baseline / indexed,
            materializer.boundary_len(),
            materializer.retained_bytes()
        );
    }
}
