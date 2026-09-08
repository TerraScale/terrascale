# Astra supplement: origin MAX regression coverage

Batch: `benchmark-20260907-origin-max-regression`
Source authority: `/tmp/terrabase-qual-fae32` only. The exact authored test
delta is [origin-max-regression.patch](diffs/origin-max-regression.patch).

## Coverage

The new test `origin_allocation_uses_each_durable_source_max_without_cross_scope_rows`
seeds a local origin and then verifies sequence allocation from each durable
source independently: `documents` max 10 produces 11, `origin_prefixes` max 20
produces 21, and `replication_publications` max 30 produces 31. Distractor rows
for another origin and another tenant prove tenant/origin scoping. A fresh
empty source case asserts the `COALESCE` path returns 1. Existing fixture
helpers and `stage_intent` remain from the copied clean baseline.

The SQL schema context in `source/storage-sqlite-mod.rs` defines
`replication_publications` with primary key `(tenant, origin, origin_seq)` and
`WITHOUT ROWID`; the test inserts valid rows directly through the existing
raw writer fixture. The companion SQL probe in the prior origin-max packet is
diagnostic only; this is the focused Rust regression requested for approval.

## Immutable inputs

| Input | SHA-256 |
| --- | --- |
| `source/storage-sqlite-mod.rs` | `ba5b3350fd4a4c250ef7d9498534953ece63f2338602d4829f0c4ce2ebad6dbc` |
| `source/storage-sqlite-tests.rs` | `bd61e89f0151289ea9e62ebb59079d71385d2089c0c6fbcb54362be56c712495` |
| `diffs/origin-max-regression.patch` | `c9b7e5eb31f1fe8f4d442927b741f46574a0f3b93e65f3475c7d2b478fee651c` |

All source was copied from the supplied clean baseline and hash checked after
copy. No live source was used and no build was run by the packet preparer.

## Verification state

The build owner and benchmark owner still need to run the focused selector and
the final benchmark cohort against the resulting source. Until then, test and
benchmark status is pending. No correctness approval is made here.
