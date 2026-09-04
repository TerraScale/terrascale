---
---
<!-- i18n:fallback:ja -->
> **翻訳レビュー待ち。** このページはまだ日本語の人力レビューがなく、英語の原文を表示しています。

# Benchmarks (draft)

> DRAFT. This launch post is not published. Result numbers remain
> placeholders until a recorded measurement replaces them.

## Outline

- The discipline (D-61): no result number is published without a measurement behind it; every billed line shows ≥ 50 % measured margin; this post ships only when the tables have real results.
- Targets vs results: GOAL section 4 numbers are targets, not results. List them as targets.
- The rig: one launch member host (2 OCPU / 12 GB ARM, D-65) as a hard cgroup ceiling; load generator outside the budget; 1 KiB hot documents; results without the ceiling record are non-gating.
- Families: point read/write, indexed query, bulk ingest, auto-indexing, sync/reactive, versus PostgreSQL-with-JSONB and RavenDB like-for-like. Single-node and RF=2 are separate families.
- Auto-indexing correctness: zero stale reads, zero regressions after activation.
- The fan-out target (one million concurrent readers of one shared view at 16 writes/s, zero Worker requests on `sync.api.`) is not public until the production test passes.
- Placeholder results table (family / metric / result / ceiling).

## Draft

We have a rule that governs this post more than any benchmark methodology does (D-61): no result number is published without a measurement behind it, and no priced line ships unless its measured unit cost carries at least 50 % gross margin at list price. That is why this draft contains no measured results, only clearly marked placeholders and the targets we are engineering toward. When we publish benchmarks, every row will trace to a recorded run on the hardware we actually pay for.

The targets, stated as targets, are same-region point read p99 ≤ 10 ms, `RegionalDurable` write p99 ≤ 20 ms, local reactive visibility p99 ≤ 50 ms over WebSocket and ≤ 250 ms over CDN HTTP, cross-region visibility p50 ≤ 300 ms and p99 ≤ 1 s, and database and branch creation p99 ≤ 5 s. The cross-region target means a write in region A becomes visible to a subscriber in region B. Cost per admitted write is our primary efficiency metric, ahead of any peak throughput figure. These are launch-gate targets, not current results.

The rig is deliberately unglamorous (D-65): a launch member host is a 2 OCPU / 12 GB Ampere A1 ARM instance. Every result must hold with the database under a hard resource ceiling equal to that host, enforced with cgroups for the single-node family, or under two such hosts for RF=2. The load generator runs outside the budget. A result measured without the ceiling in the record is non-gating and will not appear here. Documents are hot and 1 KiB. Comparisons are like-for-like against PostgreSQL-with-JSONB and RavenDB, with single-node and RF=2 reported as separate families. Combining them would hide the cost of durability.

Auto-indexing gets correctness tests before performance ones (D-31): zero stale reads while an index builds, because maintenance is in the write's own transaction, and zero query regressions after activation. A regression must flip the index invisible within one window. Performance measurements cover time-to-active at a million documents, templates-to-fixed-point on a replayed workload, and write throughput at 0, 5, and 12 indexes and at the write-budget ceiling.

The fan-out target is the one we are most careful with: one million concurrent readers of one authorization-equivalent shared view at 16 writes/s, one delta computation per cadence window, bounded origin fills, identical ordered bytes for every client, and zero Worker requests on `sync.api.`. It is not a public claim until the production test passes. The launch gate runs it on the real zone against the real engine.

Placeholder results, to be measured on the record before publication:

| Family | Metric | Result | Ceiling |
| --- | --- | --- | --- |
| Point read | p99 latency | [to be measured] | 1 launch host, cgroup |
| Point read | throughput | [to be measured] | 1 launch host, cgroup |
| Write (`RegionalDurable`) | p99 latency | [to be measured] | 1 launch host, cgroup |
| Indexed query vs PostgreSQL/JSONB | p99, like-for-like | [to be measured] | 1 launch host, cgroup |
| Bulk ingest vs PostgreSQL/JSONB | rows/s | [to be measured] | 1 launch host, cgroup |
| RF=2 write | p99 latency | [to be measured] | 2 launch hosts |
| Reactive HTTP | visibility p99 | [to be measured] | 100 ms cadence + edge |
| Fan-out | 1M readers @ 16 writes/s | [to be measured on production] | zero Worker requests on `sync.api.` |
| Auto-indexing | stale reads after activation | [target: zero, structural] | write's own transaction |
