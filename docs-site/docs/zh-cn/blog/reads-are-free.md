---
title: SQL shape read benchmark
---

<!-- i18n:fallback:zh-cn -->
> **翻译待审。** 此页面尚无经人工审校的中文译文，暂显示英文原文。

<div lang="en" dir="ltr">

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

The full regional matrix is not qualified. One 1,000-reader pilot did not
finish within 180 seconds, and the run was stopped after a CPU measurement
error was found. These results do not establish performance for larger
snapshots, 1,000-reader cohorts or browser bootstrap. See the
[benchmark method and results](https://github.com/TerraScale/terrascale/blob/main/benchmark/bench/2026-11-04-f10-free-reads.md),
[subscription pricing](/zh-cn/pricing/) and [usage accounting](/zh-cn/concepts/pricing-philosophy).

</div>
