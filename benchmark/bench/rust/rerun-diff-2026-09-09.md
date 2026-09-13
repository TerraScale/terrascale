# Benchmark re-run diff — 2026-09-09

Full re-run of every locally runnable lane against the working tree of
2026-09-09 (post "Backup resume qualification final" + uncommitted
backup/restore work). Baseline = newest complete receipt per lane retained
before the re-run, snapshotted at `bench/rust/results-baseline-20260909/`.
Median sample per receipt; picking and admission mirror the RESULTS.md
renderer (newest by filename timestamp, `status: complete` only).
Lane contract unchanged: seed 1,000 · 10,000 ops/sample · 5 samples ·
concurrency 16 · 1,000 warm-up · 2 OCPU/12 GiB envelope.

## Re-run lanes

| sqlite | mixed | 16624 → 6024 | -63.8% | 52 → 38 | -28.4% | 5466 → 7947 | 17183 → 9745 |
| sqlite | query | 99639 → 93508 | -6.2% | 20 → 20 | +0.5% | 33 → 33 | 3375 → 4499 |
| sqlite | read | 93944 → 101950 | +8.5% | 20 → 20 | -3.1% | 33 → 31 | 4578 → 5044 |
| sqlite | write | 5983 → 1995 | -66.7% | 116 → 432 | +273.0% | 215 → 768 | 957 → 1920 |
| terra-base | single-node | 585 → 623 | +6.6% | 1713613 → 865312 | -49.5% | 2350322 → 4792095 | 2509620 → 5331102 |
| terrabase | mixed | 223 → 60 | -73.3% | 2812 → 256991 | +9039.7% | 26335 → 504747 | 39874 → 617649 |
| terrabase | query | 1354 → 881 | -34.9% | 11657 → 18171 | +55.9% | 18605 → 29343 | 27485 → 34518 |
| terrabase | read | 1737 → 1832 | +5.5% | 9152 → 8812 | -3.7% | 14203 → 13201 | 20623 → 15878 |
| terrabase | write | 74 → 37 | -49.1% | 209591 → 402833 | +92.2% | 341486 → 730633 | 380977 → 892002 |
| turso | mixed | 60 → 102 | +70.0% | 213257 → 187254 | -12.2% | 318517 → 287022 | 474069 → 326969 |
| turso | query | 865 → 906 | +4.7% | 22145 → 22047 | -0.4% | 25320 → 23043 | 32579 → 25227 |
| turso | read | 1781 → 1662 | -6.7% | 1521 → 1714 | +12.7% | 22027 → 22092 | 25664 → 24148 |
| turso | write | 181 → 460 | +153.6% | 4025 → 944 | -76.6% | 13044 → 8992 | 2428247 → 609551 |
| zonetree-async | mixed | 234759 → 198503 | -15.4% | 3 → 3 | -5.8% | 12 → 7 | 29 → 18 |
| zonetree-async | query | 751275 → 567835 | -24.4% | 2 → 2 | +0.4% | 3 → 3 | 5 → 5 |
| zonetree-async | read | 918218 → 594006 | -35.3% | 2 → 2 | +0.6% | 3 → 3 | 5 → 5 |
| zonetree-async | write | 549039 → 640473 | +16.7% | 2 → 2 | -2.9% | 3 → 3 | 5 → 5 |
| zonetree-sync | mixed | 146498 → 192139 | +31.2% | 3 → 3 | -4.9% | 54 → 29 | 950 → 726 |
| zonetree-sync | query | 682166 → 999097 | +46.5% | 2 → 2 | +1.7% | 3 → 3 | 5 → 5 |
| zonetree-sync | read | 760555 → 730417 | -4.0% | 2 → 2 | -1.9% | 3 → 3 | 5 → 5 |
| zonetree-sync | write | 727747 → 759285 | +4.3% | 2 → 2 | +0.7% | 3 → 3 | 5 → 5 |

## Not re-run, and why

| lane | reason |
| --- | --- |
| postgresql, pg-fullstack, ravendb point ops | no live pinned comparator server provisioned; retained unavailable per the fail-closed script design |
| auto-index (1M docs), sync family | no complete baseline receipt exists to diff against (prior attempts failed/unavailable) |

## Limits

- Terrabase rows are local-member diagnostic-boundary runs (dev TLS, dev
  key, `local` databases), the same boundary as the Sep 8 baseline; the
  renderer classifies them `diagnostic`, not decision-grade.
- The sqlite comparator driver is byte-identical old vs new (last
  `bench/src` commit 2026-09-08 20:44 precedes the 20:51 baseline), yet
  sqlite write/mixed dropped ~2/3 — today's host write-path conditions
  differ from Sep 8 (no io-pressure snapshot was captured during this
  re-run). Read comparator deltas of ±10% bound the ambient noise floor.
- Headline: terrabase served write throughput halved (74 → 37 ops/s,
  p50 210 ms → 403 ms) and mixed degraded within-run (139 → 38 ops/s
  across samples); query −35%; read flat. Ingest docs/s +6.6% with p50
  batch latency −49% but p95/p99 roughly doubled.
