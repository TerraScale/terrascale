# Astra packet: origin sequence MAX query optimization

Batch: `benchmark-20260907-origin-max`
Source baseline: `/tmp/terrabase-qual-fae32` only. Exact proposed delta:
[origin-max.patch](diffs/origin-max.patch).

## Change and callers

`stage_intent` in `source/storage-sqlite-mod.rs:2374-2435` allocates the next
per-tenant/per-origin sequence inside the commit transaction. The patch changes
each arm of the UNION from returning every row to returning `MAX` per table,
then applies the outer `MAX` and `COALESCE(..., 0) + 1`. This preserves the
next sequence for empty tables and reduces rows materialized by the query.

The three tables are: `documents(origin, origin_seq, tenant)`,
`origin_prefixes(origin, seq, tenant)`, and
`replication_publications(tenant, origin, origin_seq)`. The latter has primary
key `(tenant, origin, origin_seq) WITHOUT ROWID`, confirmed in the copied schema
at `source/storage-sqlite-mod.rs:946-958`. Existing commit and journal tests in
`source/storage-sqlite-tests.rs` exercise origin sequence continuity and
replication publication behavior; no new origin-max regression test is included
in this patch. Astra should require a discriminating test covering empty state,
the maximum from each table, and mixed tenant/origin isolation before approval.

## Source hashes

| Input | SHA-256 |
| --- | --- |
| `source/storage-sqlite-mod.rs` | `641ee3c419a14505af486d73a0cb37f434a128fecc150db613c74c4b8174b4cc` |
| `source/storage-sqlite-tests.rs` | `bd61e89f0151289ea9e62ebb59079d71385d2089c0c6fbcb54362be56c712495` |
| `diffs/origin-max.patch` | `8632d1a9ccb011e51fcac0dffe46c7a7514e7869eec1b13f3478d0437e9215fc` |

The source baseline is copied and immutable in this packet. No live worktree
source or unrelated benchmark artifact is part of this review.

The corrected SQLite probe is copied at `diffs/probe.py` and its output at
`diffs/probe-output.txt`. At 100,000 rows per large branch, the old query used
1,800,054 VM steps versus 86 for the MAX-per-branch query; both returned
200,001. Empty, wrong-tenant, and other-origin cases returned 1, 1, and
1,000,000 respectively. The query plans retained the intended covering/primary
key searches. This is diagnostic evidence for the proposed SQL equivalence,
not a replacement for the focused Rust regression.

## Verification state

No build or benchmark was run by the packet preparer. Benchmark owner evidence
and the worker's corrected schema probe are pending. Review scope is limited to
SQL equivalence, index/schema interaction, empty-table behavior, overflow/error
handling, and tenant/origin isolation. This packet makes no correctness claim.
