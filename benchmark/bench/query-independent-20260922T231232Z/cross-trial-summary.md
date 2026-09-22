# Query-independent indexing cross-trial summary

Generated: 2026-09-22T23:26:02+00:00.

## Measurement set

- Three sequential trials, each using 2,000 seeded documents, 100 maintenance documents, and 30 repeat samples per held-out shape.
- Final valid runs used the isolated clean TerraBase worktree `/tmp/terrabase-benchmark-subject-7e5a1851` at commit `7e5a1851ce356a2f4cccbfdeffeb54635f6e9cc6`.
- Subject tree digest: `6ac4eeb26a369bf87e645158802da3873f6dca23`; all three receipts report `dirty: false` and source digest `69121d5f2269f14d9598de66881be2de4017735eb6c110c5dc861d505746be73`.
- The valid assembly came from an isolated benchmark worktree at commit `6a20f945f3f2e12ff8869e994da4d0b90be18487`, tree `d51c97142c8e592c5346e1ed2c75680e76e46b03`; its build-worktree diff SHA-256 was `aa6eb363789642dfdb8e0223334eda260a2b3ca7e89bb43e8285f4b5f14e1428` because the unrelated TieredRustFs lane was temporarily stubbed only there for its concurrent `CS1988` async `ref` compile failure. The committed query-independent source SHA-256 was `de108fb7d671a5af8d6f6da1c00024604ced4e46988972f517491250168b5956` and the committed receipt source SHA-256 was `7717e40605163e529316eaa42a4acef1782e1e7ac938ea28e58d33c1ba388d6c`.
- Measured binaries: `TerraBase.dll` SHA-256 `91706de2d0b2f28399dd836bd2f6e9c9d943612496a0f9a214fafa3b4284114a`; `TerraBase.Benchmarks.dll` SHA-256 `cf5be94037d43c9972e53ecfc459b34effb75a186a00e3c9bf4fee81542063f0`. Only `TieredRustFsQualificationWorkload` was stubbed in the isolated runner; `QueryIndependentIndexWorkload` and `RunReceipt` match committed source.
- Jev was explicitly unset for all runs; no Jev row was emitted.

## Trial receipts

| Trial | Status | Exit | Receipt SHA-256 | Subject commit | Subject dirty |
| ---: | --- | ---: | --- | --- | --- |
| 1 | complete | 0 | `b9505bcd3ce5b12672a74619bbe45bdf727b3430dd13b3e62efb7e95561013dd` | `7e5a1851ce356a2f4cccbfdeffeb54635f6e9cc6` | False |
| 2 | complete | 0 | `34282f9bd1ca15125dbbcc10d3ca153ab9d2ae4220f854cd50b5916bc02c51c7` | `7e5a1851ce356a2f4cccbfdeffeb54635f6e9cc6` | False |
| 3 | complete | 0 | `1d0df643be2f5e2bc627fc23e06f7adb45fbee2861ffd7cc28a2b1830c67c6f8` | `7e5a1851ce356a2f4cccbfdeffeb54635f6e9cc6` | False |

## Medians and ranges

Values are median (minimum–maximum) across the three seeded processes. Storage values are logical `FileInfo.Length` sums reported by the workload; they do not represent allocated disk blocks or physical storage usage.

| Shape | Strategy | Initial ingest ms | First query µs | Repeat p50 µs | Repeat p95 µs | Maintenance ms | Maintenance Δ bytes | Index Δ bytes | Checks |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |
| status | No secondary index | 209.1 (208.1–231.8) | 138442.6 (131617.3–157205.4) | 59038.1 (56999.6–60886.2) | 69481.4 (67134.4–74924.6) | 12.4 (12.4–12.6) | 0 (0–0) | 0 (0–0) | coverage=True, history=— |
| nested profile.segment | No secondary index | 209.1 (208.1–231.8) | 47393.2 (44667.3–47857.7) | 18092.9 (16905.5–24983.2) | 41668.5 (39996.7–48012.2) | 12.4 (12.4–12.6) | 0 (0–0) | 0 (0–0) | coverage=True, history=— |
| status | Deterministic schema statistics | 97.2 (87.6–104.4) | 756200.0 (623497.6–771997.0) | 20.2 (19.0–24.0) | 42.0 (35.7–52.4) | 5.7 (5.5–7.2) | 0 (0–0) | 3593726 (3593654–3593752) | coverage=True, history=0 |
| nested profile.segment | Deterministic schema statistics | 97.2 (87.6–104.4) | 572.9 (560.7–580.2) | 19.6 (18.3–22.1) | 40.9 (31.4–44.4) | 5.7 (5.5–7.2) | 0 (0–0) | 3593726 (3593654–3593752) | coverage=True, history=0 |
| status | SQLite no secondary index | 43.6 (43.0–44.4) | 2244.5 (2238.3–2255.6) | 747.5 (744.0–763.8) | 1071.1 (889.8–1098.1) | 1.6 (1.5–2.4) | 12360 (12360–12360) | 0 (0–0) | coverage=True, history=— |
| nested profile.segment | SQLite no secondary index | 43.6 (43.0–44.4) | 882.5 (865.3–908.8) | 891.9 (870.6–894.0) | 1259.9 (1182.0–1440.3) | 1.6 (1.5–2.4) | 12360 (12360–12360) | 0 (0–0) | coverage=True, history=— |
| status | SQLite explicit JSONB indexes | 34.2 (33.4–37.1) | 200.1 (195.6–252.1) | 73.3 (72.6–132.4) | 90.7 (89.2–152.1) | 3.7 (2.1–4.0) | 61800 (61800–61800) | 65920 (65920–65920) | coverage=True, history=— |
| nested profile.segment | SQLite explicit JSONB indexes | 34.2 (33.4–37.1) | 103.5 (87.4–165.1) | 71.6 (71.4–134.6) | 86.9 (83.5–153.9) | 3.7 (2.1–4.0) | 61800 (61800–61800) | 65920 (65920–65920) | coverage=True, history=— |

## Source-state record and limitations

- The shared TerraBase checkout remained at HEAD `7e5a1851ce356a2f4cccbfdeffeb54635f6e9cc6` but was dirty throughout; concurrent storage edits changed its dirty status during the run. The measured receipts point to the isolated clean worktree, so those shared-checkout edits were excluded from measured TerraBase code.
- The shared benchmark checkout advanced from HEAD `57d12ed6ae6db472a26318a9b2671b3aa08e26b0` before the first attempt to `6a20f945f3f2e12ff8869e994da4d0b90be18487` during the run. The final valid trials use the isolated `6a20f945` benchmark worktree and direct DLL, avoiding shared output replacement. The shared checkout remained dirty from the concurrent Tiered edit and generated artifacts.
- The first live-checkout attempt is retained under `trial-1-live-checkout-discarded`; the first three receipts whose binary provenance was uncertain are retained under `trial-1-prior`, `trial-2-prior`, and `trial-3-prior`. All four are excluded from this summary.
- Repeat samples are repeated calls within each process, not independent seeded trials. The three trials were sequential and have no confidence intervals; host contention and concurrent workspace activity may affect timings.
- The summary reports correctness, zero query history before candidate generation in deterministic lanes, SQLite `EXPLAIN QUERY PLAN` validation, and per-shape index-serving indicators from each Markdown receipt.
