# Query-independent direct index probes

Three fresh paired processes were run against the two frozen TerraBase
subjects named in `terrabase/docs/query-independent-indexing.md`. Each process
used 2,000 documents, 100 seeded maintenance documents, two automatic indexes,
30 full runtime repeats, and `--maintenance-documents=0`. The zero-maintenance
lane performs one direct `AutomaticIndexRuntime.MeasureOnIndexAsync` probe and
30 direct repeats for every active index before the ordinary runtime query
measurement.

The direct probe statistics below are medians across the three fresh processes;
ranges are retained so the small sample is reviewable. Full runtime first-query
values are intentionally omitted because the direct probes warm the indexed
runtime. The full runtime repeat p50/p95 values are measured after the direct
probes. Every indexed row returned the expected 42 IDs and served all full
runtime repeats from an automatic index.

| Subject | Shape | Direct first / p50 / p95 µs | Full runtime repeat p50 / p95 µs | Build ms |
| --- | --- | ---: | ---: | ---: |
| Clean | `status` | 24,268.8 (22,913.6–24,993.9) / 48.4 (45.2–52.0) / 77.7 (76.0–93.7) | 19.5 (19.1–19.6) / 39.4 (32.0–41.3) | 17,441.2 (17,253.4–18,795.5) |
| Clean | `profile.segment` | 918.2 (899.9–1,083.4) / 54.2 (52.7–55.1) / 89.5 (83.7–106.3) | 18.2 (17.7–19.2) / 33.0 (32.5–33.9) | 17,441.2 (17,253.4–18,795.5) |
| Runtime-order | `status` | 23,016.9 (22,866.6–24,029.7) / 63.0 (57.5–63.7) / 94.0 (89.0–106.0) | 28.3 (27.5–28.6) / 50.0 (45.3–55.4) | 2,573.7 (2,555.5–3,491.7) |
| Runtime-order | `profile.segment` | 1,315.0 (1,310.6–1,387.5) / 77.6 (74.2–79.2) / 102.7 (91.4–115.8) | 28.1 (27.6–28.4) / 42.0 (41.9–43.2) | 2,573.7 (2,555.5–3,491.7) |

## Frozen provenance

| Item | Clean subject | Runtime-order subject |
| --- | --- | --- |
| Repository | `/tmp/terrabase-benchmark-subject-7e5a1851` | `/tmp/terrabase-indexing-mirror-fixed-20260923T013001Z` |
| Commit | `7e5a1851ce356a2f4cccbfdeffeb54635f6e9cc6` | `7e5a1851ce356a2f4cccbfdeffeb54635f6e9cc6` |
| Tree | `6ac4eeb26a369bf87e645158802da3873f6dca23` | `6ac4eeb26a369bf87e645158802da3873f6dca23` |
| Working-tree digest | `69121d5f2269f14d9598de66881be2de4017735eb6c110c5dc861d505746be73` | `2a27753aa2a328d3bed7066b34ca4f04ec84876626e99adc9edb056484d2764d` |
| `TerraBase.dll` SHA-256 | `91706de2d0b2f28399dd836bd2f6e9c9d943612496a0f9a214fafa3b4284114a` | `5c0f580215cf909ab650b444464b21af89d8f7cc90b9a1da581c2677c1321717` |

The benchmark runner checkout base commit was
`0f73f7874fd873e5aa2605fd06ad6854d1378372`. The measured workload source
`QueryIndependentIndexWorkload.cs` SHA-256 is
`a5c24c64fde426e697d52c61be8877e5eeb966564423cb0bf48d6ca3bb0d08cd`; the
runner `Program.cs` SHA-256 is
`69f3e57e9a97c34f3df8dc3bfb191a3ab7f39339e88d358a630f6b8e6d1b09ad`.
The clean-subject runner assembly SHA-256 is
`bc176f5e4378bf3f78a01f69ee7c510293de44fe7e672b1146282c1bdee94f83`; the
runtime-order runner assembly SHA-256 is
`b159f273889a97fd9de71443a33875faf93d984fec5a81c16ad902b46b6e8a3e`.

## Commands and receipts

The command for every process was:

```text
dotnet run --project terrabase/TerraBase.Benchmarks --configuration Release --no-build --no-restore -- --workload=query-independent --documents=2000 --samples=30 --maximum-indexes=2 --maintenance-documents=0 --output=<trial-directory>
```

Each build used `--no-restore --no-dependencies` with `TerraBaseRepo` set to
the subject path above. Raw Markdown, console output, build output, and
run-receipt digests remain in each trial directory.

| Trial | Clean receipt | Runtime-order receipt |
| ---: | --- | --- |
| 1 | [run-receipt.json](trial-1-clean/run-receipt.json) `af6ac810e3cf636581ace64f68c5e217be52a0039b433052c71e533e15638fdf` | [run-receipt.json](trial-1-runtime-order/run-receipt.json) `504e4773958838019ced53d5c03fbd1ad78c5198fde27922a03ab4c47509b18d` |
| 2 | [run-receipt.json](trial-2-clean/run-receipt.json) `eaacf722d5d1e1b6dfb03874a1eed690d2f8893c432844248805cafd2baf1c65` | [run-receipt.json](trial-2-runtime-order/run-receipt.json) `95e054fd641318f1755831d4ccfe0c70ab3bc075132112eaaac991b0326229bd` |
| 3 | [run-receipt.json](trial-3-clean/run-receipt.json) `6ff59e4c4fc616c8a5d12813183beef742e1884903ad9a9ce6818ce19c4324b1` | [run-receipt.json](trial-3-runtime-order/run-receipt.json) `f6f0ecb5635322a59d7557cfe3a3bafb350cccb0ba682d9d8c186c7558761a1a` |

Jev was not requested. These are workload observations from three sequential
fresh processes on the local host; they do not establish a general performance
claim or a hard resource cap.
