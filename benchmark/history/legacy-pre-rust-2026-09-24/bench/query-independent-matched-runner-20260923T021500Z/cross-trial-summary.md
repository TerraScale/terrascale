# Query-independent indexing: matched frozen-runner comparison

Three paired fresh-process cold trials use one frozen runner revision against the clean and final runtime-order subjects. Each pair uses the same runner binary family, workload flags, document count, samples, and seeded workload.

Runner: commit `6a20f945f3f2e12ff8869e994da4d0b90be18487`, tree `d51c97142c8e592c5346e1ed2c75680e76e46b03`, `QueryIndependentIndexWorkload.cs` SHA-256 `de108fb7d671a5af8d6f6da1c00024604ced4e46988972f517491250168b5956`.
The runner source is clean at the frozen revision. The two benchmark assemblies differ only in the embedded subject-project path and referenced TerraBase subject assembly.

Requested command flags: `--workload=query-independent --documents=2000 --samples=30 --maximum-indexes=2 --maintenance-documents=100`.
This runner predates the optional `--maximum-indexes`, `--maintenance-documents`, and `--index-perf` parsing. The two optional values are ignored but match its hardcoded `MaximumIndexBudget=2` and `MaintenanceDocuments=100`; no diagnostic lane was run.

| Metric | Clean subject | Runtime-order subject | Runtime-order vs clean |
| --- | ---: | ---: | ---: |
| Cold first query µs (status) | median 778782.9; range 673438.8–819798.8 | median 799618.6; range 730386.1–840414.3 | +2.7% |
| Cold first query µs (profile.segment) | median 589.3; range 589.2–605.0 | median 1044.3; range 1022.1–1122.4 | +77.2% |
| Cold repeat p50 µs (status) | median 19.8; range 19.7–20.3 | median 28.6; range 28.3–28.6 | +44.4% |
| Cold repeat p50 µs (profile.segment) | median 18.3; range 18.1–18.7 | median 26.8; range 26.4–26.8 | +46.4% |
| Cold repeat p95 µs (status) | median 41.6; range 40.8–44.2 | median 54.5; range 48.5–57.1 | +31.0% |
| Cold repeat p95 µs (profile.segment) | median 32.8; range 24.5–36.5 | median 43.9; range 41.9–54.8 | +33.8% |
| Lifecycle build ms (status) | median 20105.9; range 17183.4–20915.2 | median 2605.6; range 2514.3–2640.5 | -87.0% |
| Lifecycle build ms (profile.segment) | median 20105.9; range 17183.4–20915.2 | median 2605.6; range 2514.3–2640.5 | -87.0% |
| Maintenance update ms (status) | median 5.1; range 5.1–5.2 | median 6.8; range 6.4–9.4 | +33.3% |
| Maintenance update ms (profile.segment) | median 5.1; range 5.1–5.2 | median 6.8; range 6.4–9.4 | +33.3% |

| Shape | First/repeat rows | First index served | Repeat index served | Result coverage |
| --- | ---: | --- | --- | --- |
| status | 42.0 / 42.0 clean; 42.0 / 42.0 patched | True / True | True / True | True / True |
| profile.segment | 42.0 / 42.0 clean; 42.0 / 42.0 patched | True / True | True / True | True / True |

The matched runner shows the remaining repeat-latency difference directly: runtime-order `status` repeat p50 is +44.4% (28.6 µs vs 19.8 µs), and `profile.segment` is +46.4% (26.8 µs vs 18.3 µs).
Both subjects are the same commit and tree. The clean subject has working-tree digest `69121d5f2269f14d9598de66881be2de4017735eb6c110c5dc861d505746be73`; the runtime-order subject has working-tree digest `2a27753aa2a328d3bed7066b34ca4f04ec84876626e99adc9edb056484d2764d`.

## Assembly hashes

| Assembly | Clean | Runtime-order |
| --- | --- | --- |
| `TerraBase.Benchmarks` | `a31c431147bc87a23abdfa6b1ea6c8a8bb59437054663028a374d8909a03ee2d` | `a31c431147bc87a23abdfa6b1ea6c8a8bb59437054663028a374d8909a03ee2d` |
| `TerraBase.Benchmarks.dll` | `597a1564b6b41c162f5addff89e666706bec05b9d7a20a6b58f20843a81b7715` | `1d89503a42f040629824e9a76c53c055623860b147137ef745f033984fff1763` |
| `TerraBase.dll` | `91706de2d0b2f28399dd836bd2f6e9c9d943612496a0f9a214fafa3b4284114a` | `5c0f580215cf909ab650b444464b21af89d8f7cc90b9a1da581c2677c1321717` |
| `TerraScale.DotNet.dll` | `76819914bfba605bc4356f80471300e4cdc15b2aa68a1339e3a2b01f8a87a083` | `76819914bfba605bc4356f80471300e4cdc15b2aa68a1339e3a2b01f8a87a083` |

## Receipts

| Trial | Clean receipt | Runtime-order receipt |
| ---: | --- | --- |
| 1 | `/home/mariogk/Projects/TerraScale/ts-benchmark/bench/query-independent-matched-runner-20260923T021500Z/trial-1-clean/run-receipt.json` (`58c877ef97741e8dde75b70807dd57e61de772cf340a476b7f6fb4d124dc12da`) | `/home/mariogk/Projects/TerraScale/ts-benchmark/bench/query-independent-matched-runner-20260923T021500Z/trial-1-runtime-order/run-receipt.json` (`3cebbb9de25009f8e86018fc8bd3dd1c7a69bbbac8b0f4d9417d37e2852b8446`) |
| 2 | `/home/mariogk/Projects/TerraScale/ts-benchmark/bench/query-independent-matched-runner-20260923T021500Z/trial-2-clean/run-receipt.json` (`51deb81c7a214ac76bcb63369584294396fc42e0e72997ab82e0bd53a65ba01e`) | `/home/mariogk/Projects/TerraScale/ts-benchmark/bench/query-independent-matched-runner-20260923T021500Z/trial-2-runtime-order/run-receipt.json` (`0006dfb3b8db2e29f2f6027d52513814c5cbd161523fea6821f8aada803025d5`) |
| 3 | `/home/mariogk/Projects/TerraScale/ts-benchmark/bench/query-independent-matched-runner-20260923T021500Z/trial-3-clean/run-receipt.json` (`adff77839734c276652ba8e574d67aafad2cbb80afbf561233fb1c186aba8f72`) | `/home/mariogk/Projects/TerraScale/ts-benchmark/bench/query-independent-matched-runner-20260923T021500Z/trial-3-runtime-order/run-receipt.json` (`788bac0bbb9a1055a90f61398dce71aa8ec1a35ba0c0923c75d535e349b42cba`) |

Raw Markdown and console logs remain beside each receipt. The JSON summary contains per-trial data, subject provenance, runner identity, assembly hashes, and paired deltas.
