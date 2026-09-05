# Ordered materializer update cost

The materializer maintains eligible rows in a `BTreeSet` keyed by SQL order
and document identity. An update removes the old ordering key, inserts the
new eligible key, and projects the first 25 rows. It no longer scans and sorts
the full source for each delta. Source rows and ordering keys remain in memory;
storage-backed boundary refill is still required to bound that memory.

Run from the repository root:

```sh
nice -n 19 cargo test --locked --release -p terrabase-sync-core --test materializer_cost -- --ignored --nocapture
```

The reference reproduces the previous refresh algorithm: scan source rows,
sort references, project top-k, and compute the result diff. Both paths receive
the same 200 replacements and must produce identical final results. Initial
seeding is excluded. Scores are permuted initially, and replacements change
the ordered result. The test source is
`crates/terrabase-sync-core/tests/materializer_cost.rs`.

## Local diagnostic, 2026-09-05

Single release-mode run with Rust 1.97.1, at CPU priority 19, while the workspace
check was running. These are materializer update timings; they do not measure
HTTP delivery, journal decoding, or end-to-end database throughput.

| Source rows | Updates | Full scan/sort ms | Ordered index ms | Speedup |
| ---: | ---: | ---: | ---: | ---: |
| 1000 | 200 | 83.618 | 8.253 | 10.1x |
| 10000 | 200 | 1564.319 | 9.166 | 170.7x |
| 100000 | 200 | 42119.790 | 10.442 | 4033.9x |

Correctness coverage also compares every result and boundary count against a
fresh recomputation across 4,000 update steps, including two ordering columns,
both sort directions, both null placements, predicate and FTS eligibility,
deletions, replacements, and reseeding.
