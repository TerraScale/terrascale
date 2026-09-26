# Deterministic benchmark provenance

## Subject

- Frozen source: `/tmp/terrabase-frozen-source-20260924T1015Z`
- Source manifest SHA-256: `9d08f6d1884610af3118c7aa736d52abb4ecad7002b191af712e3391c7a409e4`
- Synthetic local commit used for benchmark subject resolution: `55b9ebb0d2a4cd857513691c7a992a5a1c3c3774`
- Receipt tree digest: `ac17a1556ffd62a3e4bd959321fc3a79eca27bbc`
- Receipt working-tree digest: `69121d5f2269f14d9598de66881be2de4017735eb6c110c5dc861d505746be73`
- Subject was clean at run time (`dirty=false`, no working-tree changes).
- No production source or test files were changed for this verification.

## Build

- Configuration: `Release`, target framework `net11.0`
- Build exit code: `0`
- Build elapsed: `12,009 ms`
- Published benchmark binary SHA-256: `cc36af68dad31a057ad13da952a7b0b0f1406c740f7299a2f23fb8c4871186a8`
- Published TerraBase library SHA-256: `1983bcce2d56577590c968db5a2e847bbafcae323744de9ed471b1627f933402`

## Run

- Started: `2026-09-24T05:32:59.313328Z`
- Finished: `2026-09-24T05:33:09.5637194Z`
- Exit code: `0`
- Command:

  `/tmp/tsb-qidx-frozen-source-20260924T0229Z/bin/Release/net11.0/publish/TerraBase.Benchmarks.dll --workload=query-independent --documents=2000 --samples=30 --maximum-indexes=2 --maintenance-documents=100 --physical-index-budget-mib=8 --output=/home/mariogk/Projects/TerraScale/ts-benchmark/bench/query-independent-frozen-source-20260924T0232Z`

- Run receipt SHA-256: `8210dc248128a1b407ab462bcc56e3df942452dd477848c314ae4f0732887712`
- Raw receipt SHA-256: `e7922d82e505432cb737a882cae4b8b03acefc690a0c7c1a6ddc47cd1b112ec1`
- Markdown summary SHA-256: `aa51a7da6aa46acb241d81ef527724fde01ca2aed45fc8cf4e197532be1a801b`
- Raw receipt references the summary hash and it was rechecked successfully.
- Run receipt references the raw receipt hash and it was rechecked successfully.

## Deterministic result checks

- Eight result rows were present; every row had `correct=true` at both row and measurement levels.
- Deterministic index row reported `1,004,100` logical document/entry bytes and `455,378` bytes of final index storage delta.
- The selected automatic-index budget was `8 MiB` (`8,388,608` bytes); WAL and compaction staging are reported separately.
- Jev lane: `UNRUN` (`--jev` omitted).
- Von lane: `UNRUN` (`--von` omitted).
- `TERRABASE_JEV_API_KEY` and `TERRABASE_VON_API_KEY` were absent in the verification environment.
- No live external comparison was requested or performed.

## Repository checks

- `git diff --check` passed in `/home/mariogk/Projects/TerraScale/terrabase`.
- `git diff --check` passed in `/home/mariogk/Projects/TerraScale/ts-benchmark`.
- Both working trees already contained unrelated changes and benchmark artifacts; this verification did not stage, commit, or alter them.

## Limits

- This is one deterministic seeded run with 30 repeated samples per predicate; it does not provide cross-trial variance or confidence intervals.
- The frozen source copy and instrumented benchmark binary make this workload reproducible, but timing should not be treated as bit-for-bit comparable with older binaries or runs that used different instrumentation.
