# Ordered materializer update cost

The materializer maintains eligible rows in a `BTreeSet` keyed by SQL order
and document identity. An update removes the old ordering key, inserts the
new eligible key, and projects the first 25 rows. It no longer scans and sorts
the full source for each delta. The current implementation retains at most
min(2k, 2000) candidates and caps estimated candidate bytes at 64 MiB. When its
known ordering prefix can no longer prove a complete top-k, it returns
`RefillRequired` without advancing state. The caller must supply an authoritative
storage window at the pending journal cut before publishing. The HTTP storage
adapter is not connected to this interface yet.

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

## Ordering index with all source rows retained, 2026-09-05

Single release-mode run with Rust 1.97.1, at CPU priority 19, while the workspace
check was running. These are materializer update timings; they do not measure
HTTP delivery, journal decoding, or end-to-end database throughput.

| Source rows | Updates | Full scan/sort ms | Ordered index ms | Speedup |
| ---: | ---: | ---: | ---: | ---: |
| 1000 | 200 | 83.618 | 8.253 | 10.1x |
| 10000 | 200 | 1564.319 | 9.166 | 170.7x |
| 100000 | 200 | 42119.790 | 10.442 | 4033.9x |

## Bounded candidate window, 2026-09-05

Same release command and update sequence, with atomic staging of each delta.
Only the candidate window is cloned for staging. Candidate-byte estimates cover
row fields and ordering keys; they exclude the published result, fence, temporary
staging, allocator overhead, and the caller's source snapshot. Source-size
independence here applies to retained materializer candidates, not the database
or the benchmark's reference implementation. This update sequence improves row
scores and does not require storage refills; the correctness tests cover refills.

| Source rows | Updates | Full scan/sort ms | Bounded index ms | Speedup | Retained rows | Estimated candidate bytes |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1000 | 200 | 86.311 | 18.659 | 4.6x | 50 | 9500 |
| 10000 | 200 | 1663.288 | 18.599 | 89.4x | 50 | 9500 |
| 100000 | 200 | 36547.601 | 18.738 | 1950.5x | 50 | 9500 |

Correctness coverage also compares every result and boundary count against a
fresh recomputation across 4,000 update steps, including two ordering columns,
both sort directions, both null placements, predicate and FTS eligibility,
deletions, replacements, and reseeding.
