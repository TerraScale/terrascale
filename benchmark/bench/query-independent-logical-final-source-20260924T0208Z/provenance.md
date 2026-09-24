# Final deterministic benchmark provenance

- Subject project: `/home/mariogk/Projects/TerraScale/terrabase/src/TerraBase/TerraBase.csproj`
- Subject commit: `47b235dcc78240a9eb5330e454a7bf3273ffcba9`
- Subject working tree digest: `0640954d4c075a513121a9b6a58c4f1559ce892649d21346de73b2a7046da940`
- Build configuration: Release
- Publish output: `/tmp/tsb-qidx-final-source-20260924T0208Z/bin/Release/net11.0/publish/`
- TerraBase.Benchmarks.dll SHA-256: `ba9596996dc3335d699ee72440e8eadafdba779484ca465b94fa54634917c522`
- TerraBase.dll SHA-256: `62d01ca2867ac47fadff46a61b4c1c1ce681dea5cc1c082e6e8682c7bb870896`
- Build elapsed: 28,312 ms; exit code 0
- Run elapsed: 19.357 seconds; exit code 0
- Run command: `dotnet TerraBase.Benchmarks.dll --workload=query-independent --documents=2000 --samples=30 --maximum-indexes=2 --maintenance-documents=100 --physical-index-budget-mib=8`
- `TERRABASE_JEV_API_KEY`: unset
- `TERRABASE_VON_API_KEY`: unset
- Jev: UNRUN; Von: UNRUN

Comparison: deterministic index build was 6,096.0 ms, versus 31,791.3 ms in the prior accepted report and 4,807.4 ms in the intermediate fastpath run.

The authoritative source change list, toolchain, command, and receipt digests are in `run-receipt.json`.
