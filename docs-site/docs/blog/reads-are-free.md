---
title: SQL shape read benchmark
---

# SQL shape read benchmark

In three measured repeats on November 4, 2026, the JavaScript SDK in Node.js read a
16-row SQL table shape through the CDN with zero billed read units at 1, 10
and 100 concurrent readers. The workload included a sealed snapshot, a
one-row catch-up change and a live update.

| Concurrent readers | CDN HIT ratio | Total origin requests | Billed read units |
| ---: | ---: | ---: | ---: |
| 1 | 0% | 6 | 0 |
| 10 | 90% | 68 (66–68) | 0 |
| 100 | 99% (98.99–99.05%) | 1,198 (1,176–1,295) | 0 |

Values show the median and range across three repeats, with CDN responses
from Cloudflare’s GRU location. Origin counts include authenticated shape
control requests, live polling, retries and refusals. CDN caching reduced
payload delivery from the origin; total origin requests increased with
reader count, including HTTP 429 responses at higher concurrency.

These results do not establish performance for larger snapshots or
1,000-reader cohorts. See the
[benchmark method and results](https://github.com/TerraScale/terrascale/blob/main/benchmark/bench/2026-11-04-f10-free-reads.md),
[subscription pricing](/pricing/) and [usage accounting](/concepts/pricing-philosophy).
