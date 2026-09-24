# Query independent indexing: held out three trial comparison

## Scope and evidence

This report aggregates the three completed 2,000-document trials. Each used 30 repeat samples, two candidate indexes, 100 post-build maintenance updates, and an 8 MiB physical index cap. The immutable TerraBase snapshot working-tree digest was 489e1bc0d39ff9ccaf0ac472e1b733aa852189eef46ccc4aee74030bdd78f597.

The Release solution build passed with 0 warnings and 0 errors; the selected TUnit gates passed 276 tests with 0 failures and 0 skips. The benchmark runner SHA-256 was dee7cb5babffb0a27f38517761a64a7878a1684ed69aaba0f75aab8478fa1423 and the loaded TerraBase DLL SHA-256 was 2bb2f209fa4158132aa446f4168b4516d4540b1d51cc571c9324949c5cd06bbc.

Von returned model evidence for von-1.2.0 in all three trials (823 input tokens, 7 output tokens, null reported cost). No Von recommendation was accepted; the selected advice was deterministic fallback after the Von attempt. The measured lane is not Von-assisted.

Trial receipts: [Trial 1](query-independent-heldout-20260924T0350Z/run-receipt.json), [Trial 2](query-independent-heldout-20260924T0356Z-trial2/run-receipt.json), [Trial 3](query-independent-heldout-20260924T0358Z-trial3/run-receipt.json).

## Ten comparison rows

Latency columns show median [minimum–maximum] across the three trials, in microseconds. First-call observations remain visible per trial. Index served counts first calls that used an index; SQLite plans are EXPLAIN QUERY PLAN evidence.

| Shape | Strategy | First call µs: median [range] | First call T1 / T2 / T3 µs | Repeat P50 µs: median [range] | Repeat P95 µs: median [range] | First rows examined: median [range] | First calls using index |
|---|---|---:|---:|---:|---:|---:|---:|
| status | No secondary index | 116,398.2 [109,869.7–139,399.9] | 109,869.7 / 139,399.9 / 116,398.2 | 57,870.7 [53,243.2–60,386.9] | 74,541.7 [72,187.2–78,570.3] | 2,100 [2,100–2,100] | 0/3 |
| status | Deterministic schema statistics | 64,157.3 [60,373.1–112,122.9] | 60,373.1 / 112,122.9 / 64,157.3 | 42.9 [42.3–76.4] | 1,962.5 [1,454.0–2,405.2] | 2,100 [2,100–2,100] | 0/3 |
| status | Deterministic fallback after Von attempt | 999,723.2 [973,445.5–1,123,927.6] | 1,123,927.6 / 999,723.2 / 973,445.5 | 25.1 [24.5–32.3] | 74.9 [39.8–82.7] | 42 [42–42] | 3/3 |
| status | SQLite tuned no secondary index | 3,293.0 [3,273.3–4,966.7] | 4,966.7 / 3,273.3 / 3,293.0 | 758.6 [746.1–1,229.9] | 1,008.6 [882.2–1,432.1] | — | 0/3 |
| status | SQLite tuned explicit JSONB indexes | 782.3 [722.8–790.5] | 790.5 / 722.8 / 782.3 | 69.0 [68.6–69.1] | 107.1 [92.3–128.0] | — | 3/3 |
| nested profile.segment | No secondary index | 55,873.7 [52,417.5–77,449.8] | 77,449.8 / 55,873.7 / 52,417.5 | 21,501.7 [18,119.9–27,848.6] | 48,240.5 [46,427.1–75,883.8] | 2,100 [2,100–2,100] | 0/3 |
| nested profile.segment | Deterministic schema statistics | 1,503.8 [1,379.7–2,343.4] | 2,343.4 / 1,503.8 / 1,379.7 | 38.8 [38.6–71.2] | 75.1 [64.5–115.1] | 42 [42–42] | 3/3 |
| nested profile.segment | Deterministic fallback after Von attempt | 940.1 [859.7–979.1] | 859.7 / 940.1 / 979.1 | 23.9 [23.0–24.6] | 48.6 [35.2–50.5] | 42 [42–42] | 3/3 |
| nested profile.segment | SQLite tuned no secondary index | 1,648.5 [1,575.3–2,010.5] | 1,575.3 / 2,010.5 / 1,648.5 | 888.5 [882.0–891.0] | 1,543.9 [1,054.3–2,738.8] | — | 0/3 |
| nested profile.segment | SQLite tuned explicit JSONB indexes | 667.3 [585.9–702.9] | 667.3 / 585.9 / 702.9 | 68.1 [66.5–71.0] | 91.6 [88.4–112.7] | — | 3/3 |

Every query returned the expected 21 documents in each trial. TerraBase deterministic status first calls scanned 2,100 rows in all trials; deterministic nested-profile first calls served the index in all trials. The fallback lane first calls served the automatic index for both shapes in all trials.

## Same-engine first-query comparisons

The status fallback first call regressed against TerraBase's no-secondary-index status baseline in every trial. Trial 1 was +923.0% (1,123,927.6 µs versus 109,869.7 µs). It was +1,761.6% against deterministic schema statistics in the same trial (60,373.1 µs).

| Trial | Status: no-index baseline µs | Status: deterministic µs | Status: fallback after Von µs | Fallback vs no-index | Fallback vs deterministic |
|---|---:|---:|---:|---:|---:|
| Trial 1 | 109,869.7 | 60,373.1 | 1,123,927.6 | +923.0% | +1,761.6% |
| Trial 2 | 139,399.9 | 112,122.9 | 999,723.2 | +617.2% | +791.6% |
| Trial 3 | 116,398.2 | 64,157.3 | 973,445.5 | +736.3% | +1,417.3% |

The separate 2,000-document pre-query diagnostic found identical candidate IDs, paths, and fallback recommendations in both arms. At source sequence 22, both active visible indexes were at applied sequence 21. ExplainAsync returned Scan in both lanes with the reason No candidate satisfies the query shape and freshness contract. The actual query then used the index in both lanes as the background feed caught up. This confirms freshness timing as the relevant planner condition and rules out different candidate definitions; the diagnostic did not reproduce the campaign split, so the Von delay is a plausible contributor rather than a proven sole cause. See [the diagnostic summary](query-independent-freshness-diagnostic-20260924T0408Z/freshness-diagnostic-summary.md).

## Build, maintenance, storage, and inference

Values are median [minimum–maximum] across trials. Time is in milliseconds; storage totals are decimal MB. TerraBase automatic-index logical bytes and WAL bytes are summed across active indexes. SQLite has no TerraBase profile or automatic-index counters.

| Strategy | Initial ingest ms | Profile ms | Advisor/inference ms | Index build ms | Maintenance ms | Maintenance storage delta bytes | Storage after writes MB | Storage before index MB | Final storage MB | Auto-index logical bytes | Auto-index WAL bytes | Inference evidence |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| No secondary index | 411.4 [380.7–423.5] | 0.0 [0.0–0.0] | 0.0 [0.0–0.0] | 0.0 [0.0–0.0] | 25.7 [24.1–31.1] | 0 [0–0] | 67.1 [67.1–67.1] | 67.1 [67.1–67.1] | 67.1 [67.1–67.1] | — | — | — |
| Deterministic schema statistics | 142.7 [135.8–151.4] | 176.9 [168.0–219.5] | 1.7 [1.7–1.7] | 26,541.7 [24,333.1–26,949.2] | 8.4 [7.0–10.1] | 0 [0–0] | 67.1 [67.1–67.1] | 67.1 [67.1–67.1] | 67.6 [67.6–67.6] | 1,004,100 [1,004,100–1,004,100] | 104 [104–104] | — |
| Deterministic fallback after Von attempt | 93.8 [88.6–119.5] | 11.7 [11.1–23.3] | 15,643.0 [15,292.5–18,316.0] | 29,572.6 [26,561.3–30,663.0] | 5.5 [5.2–6.0] | 0 [0–0] | 67.1 [67.1–67.1] | 67.1 [67.1–67.1] | 67.6 [67.6–67.6] | 1,004,100 [1,004,100–1,004,100] | 104 [104–104] | von-1.2.0; 823 in / 7 out; cost n/a |
| SQLite tuned no secondary index | 53.2 [50.8–77.5] | 0.0 [0.0–0.0] | 0.0 [0.0–0.0] | 0.0 [0.0–0.0] | 1.6 [1.4–1.9] | 12,360 [12,360–12,360] | 0.7 [0.7–0.7] | 0.7 [0.7–0.7] | 0.7 [0.7–0.7] | — | — | — |
| SQLite tuned explicit JSONB indexes | 35.4 [32.9–37.8] | 0.0 [0.0–0.0] | 0.0 [0.0–0.0] | 5.3 [5.1–7.1] | 2.2 [2.0–2.6] | 61,800 [61,800–61,800] | 0.7 [0.7–0.7] | 0.7 [0.7–0.7] | 0.8 [0.8–0.8] | — | — | — |

Both TerraBase indexed arms activated two indexes and remained within the 8 MiB physical-index cap. They shared the same policy and logical index bytes (1,004,100 bytes per run). The fallback lane includes a measured Von attempt; its inference time is overhead, not a model benefit.

## Interpretation

The benchmark compares an immediate deterministic decision with the same deterministic fallback policy after an inference call that yielded no accepted Von recommendation. The diagnostic found matching candidate definitions and stale pre-query watermarks in both lanes; the ExplainAsync result was a source scan in each, but the subsequent runtime query used the index in both after background catch-up. The campaign's first-call gap is consistent with an asynchronous readiness race, not a different set of candidate definitions. Later repeats served the index in both TerraBase indexed lanes.

Compact-receipt artifact hashes were checked after this aggregate report was written. The pre-existing default-budget refusal receipt was left unchanged.
