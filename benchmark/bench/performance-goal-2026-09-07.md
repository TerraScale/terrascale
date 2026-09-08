# TerraBase performance goal

The session goal tool tracks this goal as active. Continue until the applicable
TerraBase benchmarks complete, discovered failures are fixed, and the final
source and measurements have passed the relevant verification and review.

Optimize real calls, writes, reads, queries, indexing, ingest, and sync toward
the best measured throughput, latency, and resource efficiency achievable in
this work. Aim toward PostgreSQL performance without claiming parity from
incomparable or failed measurements. Run **only TerraBase**; the existing
PostgreSQL receipts currently provide no valid comparison baseline.

## Instructions and measurement contract

- Preserve synced durability, atomicity, causal/session guarantees, exact
  results, admission limits, billing correctness, and error detection.
- Keep all benchmark history and save readable Markdown tables in `bench/`,
  backed by raw receipts. Include settings, percentiles, throughput, memory,
  I/O, source/executable/schema identity, and before/after evidence.
- Failed, interrupted, unavailable, and incomparable measurements remain
  explicit. Never report partial execution or malformed protocol responses
  as a successful benchmark. Missing real CDN/region deployments cannot be
  replaced by a local origin and called remote qualification.
- Use the requested Astra Medium parallel workforce for independent work,
  explicit file ownership, and independent review. Keep live benchmark loads
  sequential; coordinate builds and record concurrent host contention.
- Follow human-owned `AGENTS.md` files without editing them. TerraBase uses
  Rust and SQLite; deployment targets are OCI and Cloudflare. Writes and
  storage are billed; everything else is free. Never put a Worker on the
  reactive hot path.
- Run Rust builds at reduced priority and `make` at the lowest CPU priority.
  Preserve concurrent user changes. Delete obsolete code rather than adding
  compatibility shims.
- Use `fj` for Forgejo, the repository's `develop` branch, and no PRs. Do not
  commit or push unless requested; never add a Co-authored trailer. Push only
  with `make check` green. If the human explicitly asks for a commit, follow
  that instruction exactly without first running tests, checks, or builds.

## Work and acceptance

| Outcome | Implementation | Review / verification |
| --- | --- | --- |
| Bounded causal frontier on real session/dependency/commit callers | Implemented; seven new regressions prepared | Coordinated build and independent review pending |
| Lifecycle and repeated placement reads avoid writer transactions | Implemented; five new regressions prepared | Initial source review found no regression; tests pending |
| PUT avoids redundant body loads while retaining causal/replay checks | Implemented; two new regressions prepared | Final caller review and tests pending |
| Batch validation, digest sorting, and dispatch avoid needless work | Implemented; exhaustive semantic/digest regressions prepared | Initial source review found no regression; tests pending |
| Top-k discards avoid unnecessary document cloning | Implemented; four new regressions prepared | Initial source review found no regression; tests pending |
| Benchmark failure coordination terminates without deadlock | Correction in progress | Warm-up failure and panic regression checks required |
| Sync benchmark validates real complete protocol data and resources | Correction in progress | Malformed-frame, revision, percentile, and resource checks required |
| Full served workload and ingest/index/sync tables | Await corrected release and drivers | Full local reruns required; remote dependencies explicit |
| Engine, materializer, QUIC ACK, and 100k FTS diagnostics | Prior release runs completed | Results retained; do not substitute for final-source qualification |
| PostgreSQL comparison | Retained receipts audited | No usable baseline; no parity ratio claimed |

Current receipts live in `bench/results/run-20260907/`. Detailed transient
source cohorts, task ownership, review packets, and test logs are recorded in
`/tmp/terrabase-performance-review-20260907/` and
`/tmp/terrabase-performance-verification-20260907/`. Update this table as
acceptance advances; implementation alone does not complete the goal.
