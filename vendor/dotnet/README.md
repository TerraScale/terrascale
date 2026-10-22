# ts-dotnet package pin

The dashboard consumes TerraScale.DotNet 0.0.4-dashboard.import.2, a local
composition of ts-dotnet `main` at `846a19e8` (all-regions database placement,
[ts-dotnet PR #41](https://github.com/TerraScale/ts-dotnet/pull/41); it already
contains the TerraBase import client from PR #12) and a version commit. Its
source revision `12aa33ece40e235168be1bca4fee403706ef7cb3` is not pushed and
the package is not published. It was built with ts-dotnet's
`scripts/pack-reproducible.py`. NuGet.Config maps the package exclusively to
this committed feed; other dependencies come from nuget.org. The package
supports net10.0 and net11.0; the dashboard targets net10.0.

The source, composition, toolchain, package/assembly hashes and all raw contract
hashes are recorded in provenance.json. Two clean locked builds with independent
caches produced identical bytes, and installed-package HTTP-fake consumers
passed on both frameworks. Fake tests do not prove live service compatibility.

Replace this pin with the first published ts-dotnet release that contains
all-regions placement.

Run `nice -n 19 python3 scripts/verify-sdk.py` to check the recorded package,
repository identity, framework assemblies, contract paths/hashes and central pin.
