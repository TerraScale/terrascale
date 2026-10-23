# ts-dotnet package pin

The dashboard consumes TerraScale.DotNet 0.0.4-dashboard.account.1, a local
composition of ts-dotnet `main` at `b89bfa3` (adds
`Platform.DeleteAccountAsync` for ts-auth `DELETE /v1/terrascale/account`; it
already contains all-regions placement and the TerraBase import client) and a
version commit. Its source revision `59c1205cb523d53e8bb66c8ae9f745b407a0b05e`
is not pushed and the package is not published. It was built with ts-dotnet's
`scripts/pack-reproducible.py`. NuGet.Config maps the package exclusively to
this committed feed; other dependencies come from nuget.org. The package
supports net10.0 and net11.0; the dashboard targets net10.0.

The source, toolchain, package/assembly hashes and all raw contract hashes are
recorded in provenance.json. Two clean locked builds with independent caches
produced identical bytes, and installed-package HTTP-fake consumers passed on
both frameworks. Fake tests do not prove live service compatibility.

Replace this pin with the first published ts-dotnet release that contains
account deletion.

Run `nice -n 19 python3 scripts/verify-sdk.py` to check the recorded package,
repository identity, framework assemblies, contract paths/hashes and central pin.
