---
title: SQL shape read benchmark
---

<!-- i18n:fallback:ja -->
> **翻訳レビュー待ち。** このページはまだ日本語の人力レビューがなく、英語の原文を表示しています。

<div lang="en" dir="ltr">

# SQL shape read benchmark

The JavaScript SDK in Node.js delivered snapshots, catch-up changes and live
updates with zero billed read units at 1, 10, 100 and 1,000 concurrent
listeners. The November 4–5, 2026 benchmark measured table and filtered shapes
through Cloudflare with regional egress in GRU, SJC and FRA. Each group had
three measured repeats.

Identical listeners in one SDK client shared one upstream stream. These
results measure that sharing behavior; independent clients and browser
sessions were not measured.

| Listeners in one client | Origin attempts per cohort | CDN GET attempts per cohort | Billed read units |
| ---: | ---: | ---: | ---: |
| 1 | 7–8 | 2–3 | 0 |
| 10 | 7–8 | 2–3 | 0 |
| 100 | 7–8 | 2–3 | 0 |
| 1,000 | 7–8 | 2–3 | 0 |

Ranges cover all 18 cohorts at each listener count. Origin attempts include
shape discovery, keys, head reads and WebSocket upgrades. Each cohort used a
fresh 16-row table, sealed snapshot and catch-up URLs, and one live update.

The CDN HIT ratio was 0.00% across 192 GET attempts against these cold URLs.
SDK sharing kept upstream requests bounded in this workload. This does not
establish cache-hit rates or flat origin cost across independent clients,
larger snapshots, longer histories or sustained writes.

The [full results and method](https://github.com/TerraScale/terrascale/blob/main/benchmark/bench/2026-11-04-f10-post-f12.md)
include regional p50/p99 latency, CPU, R2 operations, the failed readiness
attempt and the before/after comparison. See also [subscription pricing](/ja/pricing/)
and [usage accounting](/ja/concepts/pricing-philosophy).

</div>
