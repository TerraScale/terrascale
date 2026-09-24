# Deterministic benchmark provenance

## Subject and formatter

- Frozen TerraBase source: `/tmp/terrabase-frozen-source-20260924T1015Z`
- Source manifest SHA-256: `9d08f6d1884610af3118c7aa736d52abb4ecad7002b191af712e3391c7a409e4`
- Synthetic local source commit: `55b9ebb0d2a4cd857513691c7a992a5a1c3c3774`
- Run receipt tree digest: `ac17a1556ffd62a3e4bd959321fc3a79eca27bbc`
- Run receipt working-tree digest: `69121d5f2269f14d9598de66881be2de4017735eb6c110c5dc861d505746be73`
- Frozen subject was clean at run time (`dirty=false`, no working-tree changes).
- Formatter change verified: `FormatFirstRepeatIndexes` uses `Distinct(StringComparer.Ordinal)` before rendering candidate IDs.

## Build

- Configuration: `Release`, target framework `net11.0`
- Build command:

  `dotnet publish terrabase/TerraBase.Benchmarks/TerraBase.Benchmarks.csproj -c Release -f net11.0 -o /tmp/tsb-qidx-frozen-source-20260924T0539Z/bin/Release/net11.0/publish -p:TerraBaseRepo=/tmp/terrabase-frozen-source-20260924T1015Z -p:TerraScaleSdkProject= -p:UseAppHost=false`

- Build exit code: `0`
- Published `TerraBase.Benchmarks.dll` SHA-256: `4a7860928ddd3c489cb89ddbb3088d88aafb181e7cad56d8c2bee009b3d1dcdc`
- Published `TerraBase.dll` SHA-256: `e724b8de634404df1d6f3c15d627b4e3e88ee918900f0ef06b50a4c8d165cdfe`

## Run

- Started: `2026-09-24T05:40:12.4831693Z`
- Finished: `2026-09-24T05:40:22.0798484Z`
- Exit code: `0`
- Exact successful command:

  `env -u TERRABASE_JEV_API_KEY -u TERRABASE_VON_API_KEY dotnet /tmp/tsb-qidx-frozen-source-20260924T0539Z/bin/Release/net11.0/publish/TerraBase.Benchmarks.dll --workload=query-independent --documents=2000 --samples=30 --maximum-indexes=2 --maintenance-documents=100 --physical-index-budget-mib=8 --output=/home/mariogk/Projects/TerraScale/ts-benchmark/bench/query-independent-frozen-source-20260924T0540Z`

- Run receipt file SHA-256: `5006262ea2de85d3d53c063547ccf49b1c7771242017d229a3aaf3286b6ca201`
- Run receipt embedded `receiptSha256`: `2e9b2c5601a1c882a7676c7bff5d3a1f5cda1477ee4cb8d03310b3611d03c41e`
- Raw receipt SHA-256: `6c7a560db204d6b1912572aa3dff1cda45bf93387ae71358e4a5c742039cb338`
- Markdown summary SHA-256: `1a16326fb6b9d1e756d37680c21e97c9b47d7a99374f3027e37e5f7486627168`

The first shell attempt passed the DLL directly to `env` and returned `Permission denied` before starting the benchmark. The successful retry invoked the DLL through `dotnet`; it produced the receipt above.

## Result checks

- Eight result rows were present; all row-level and measurement-level `correct` flags were `true`.
- The deterministic status diagnostic contains `status:` exactly once and `profile.segment:` exactly once.
- Logical index bytes: `1,004,100`.
- Recognized WAL bytes: `104`.
- WAL compaction staging bytes: `0`.
- Final index storage delta: `454,624 B`.
- Jev lane: `UNRUN` (`--jev` omitted).
- Von lane: `UNRUN` (`--von` omitted).
- Both model-key variables were explicitly unset for the successful run.

## Verification limits

- This is one deterministic seeded run with 30 repeat samples per predicate.
- The formatter validation covers duplicate diagnostic rendering; it does not add a new admission-threshold test.
- No production source or tests were changed; this file is benchmark evidence only.
