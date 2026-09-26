# Deterministic benchmark provenance

- Subject project: `/home/mariogk/Projects/TerraScale/terrabase/src/TerraBase/TerraBase.csproj`
- Subject commit: `47b235dcc78240a9eb5330e454a7bf3273ffcba9`
- Subject working tree digest captured by the runner: `0e6d69a0bdc0d9409e21c944127aa2f59c16d8dbf338ec449b0dc4fe332644dd`
- Build configuration: Release
- Binary: `/tmp/tsb-qidx-fastpath-final-20260924T0155Z/bin/Release/net11.0/publish/TerraBase.Benchmarks.dll`
- Binary SHA-256: `ba9596996dc3335d699ee72440e8eadafdba779484ca465b94fa54634917c522`
- Build elapsed: 15,113 ms; exit code 0
- Run command: `dotnet TerraBase.Benchmarks.dll --workload=query-independent --documents=2000 --samples=30 --maximum-indexes=2 --maintenance-documents=100 --physical-index-budget-mib=8`
- Run elapsed: 14.186 seconds; exit code 0
- `TERRABASE_JEV_API_KEY`: unset
- `TERRABASE_VON_API_KEY`: unset
- Jev: UNRUN; Von: UNRUN

The authoritative subject change list, toolchain, command, and receipt digests are in `run-receipt.json`.
