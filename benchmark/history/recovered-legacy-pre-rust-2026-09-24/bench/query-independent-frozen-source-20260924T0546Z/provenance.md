# Deterministic benchmark provenance

## Subject and bounded measurement changes

- Frozen TerraBase source: `/tmp/terrabase-frozen-source-20260924T1015Z`
- Source manifest SHA-256: `9d08f6d1884610af3118c7aa736d52abb4ecad7002b191af712e3391c7a409e4`
- Synthetic local source commit: `55b9ebb0d2a4cd857513691c7a992a5a1c3c3774`
- Run receipt tree digest: `ac17a1556ffd62a3e4bd959321fc3a79eca27bbc`
- Run receipt working-tree digest: `69121d5f2269f14d9598de66881be2de4017735eb6c110c5dc861d505746be73`
- Frozen subject was clean at run time (`dirty=false`, no working-tree changes).
- Report wording: storage is described as the final sum-of-file-lengths delta.
- Diagnostic status repeat: one ActivityListener-instrumented probe immediately after the first query; its latency is reported separately and excluded from aggregate repeat samples, p50, p95, and max.
- Aggregate repeats: exactly 30 uninstrumented samples per predicate; each raw receipt row contains 30 latency samples.

## Build

- Configuration: `Release`, target framework `net11.0`
- Build command:

  `dotnet publish terrabase/TerraBase.Benchmarks/TerraBase.Benchmarks.csproj -c Release -f net11.0 -o /tmp/tsb-qidx-frozen-source-20260924T0545Z/bin/Release/net11.0/publish -p:TerraBaseRepo=/tmp/terrabase-frozen-source-20260924T1015Z -p:TerraScaleSdkProject= -p:UseAppHost=false`

- Build exit code: `0`
- Published `TerraBase.Benchmarks.dll` SHA-256: `743a08e0fe2efd693a780a5af2f6e38da3b1b28182a7f2cf97f6946b82b6155e`
- Published `TerraBase.dll` SHA-256: `e724b8de634404df1d6f3c15d627b4e3e88ee918900f0ef06b50a4c8d165cdfe`

## Run

- Started: `2026-09-24T05:46:07.1631234Z`
- Finished: `2026-09-24T05:46:16.6994546Z`
- Exit code: `0`
- Exact command:

  `env -u TERRABASE_JEV_API_KEY -u TERRABASE_VON_API_KEY dotnet /tmp/tsb-qidx-frozen-source-20260924T0545Z/bin/Release/net11.0/publish/TerraBase.Benchmarks.dll --workload=query-independent --documents=2000 --samples=30 --maximum-indexes=2 --maintenance-documents=100 --physical-index-budget-mib=8 --output=/home/mariogk/Projects/TerraScale/ts-benchmark/bench/query-independent-frozen-source-20260924T0546Z`

- Run receipt file SHA-256: `4b77b0ede5f4653a297f1b1dcb82710634df549c9931fb39a89c0bcd58ecb0de`
- Run receipt embedded `receiptSha256`: `8c8e0565b6fef5f4eadc3b332f291204a14784367ea646a6f284ec3f331d0087`
- Raw receipt SHA-256: `385feb0f66eb7d985c6514b8e8e1fd1257951a8e97abcefc57ddf0d3f421368b`
- Markdown summary SHA-256: `53ff4b9761e1e6b97aa25e091e0227422d765390703eaa5557a609defb137360`

## Result checks

- Eight result rows were present; all row-level and measurement-level `correct` flags were `true`.
- Every row contained exactly 30 uninstrumented repeat latency samples.
- Deterministic status diagnostic latency: `16,284.452 µs`, stored separately from aggregate repeats.
- Deterministic status diagnostic contains `status:` exactly once and `profile.segment:` exactly once.
- Logical index bytes: `1,004,100`.
- Recognized WAL bytes: `104`.
- WAL compaction staging bytes: `0`.
- Final sum-of-file-lengths index delta: `455,193 B`.
- Jev lane: `UNRUN` (`--jev` omitted).
- Von lane: `UNRUN` (`--von` omitted).
- Both model-key variables were explicitly unset for the run.

## Limits

- This is one deterministic seeded run with 30 repeat samples per predicate.
- It does not add an admission-threshold exceedance test or cross-trial confidence interval.
- No production source or tests were changed; the bounded changes are benchmark measurement/reporting behavior only.
