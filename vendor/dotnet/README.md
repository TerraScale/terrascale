# ts-dotnet package pin

The dashboard consumes TerraScale.DotNet 0.0.4-dashboard.import.1, a local
composition of the accepted v0.0.3 source `ff1d1126087d40d112ca27383fbb3111e320cb4a`
with the TerraBase import client from [ts-dotnet PR #12](https://github.com/TerraScale/ts-dotnet/pull/12)
(`ed2e352` and `314664b`, cherry-picked) and a version commit. Its source
revision `796b450196fbcfdad9f2eff10ec630a1458261fb` is not pushed and the
package is not published. NuGet.Config maps the package exclusively to this
committed feed; other dependencies come from nuget.org. The package supports
net10.0 and net11.0; the dashboard targets net10.0.

The source, composition, toolchain, package/assembly hashes and all raw contract
hashes are recorded in provenance.json. The composed SDK passed its 383 tests on
both frameworks, two clean locked builds with independent caches produced
identical bytes, and installed-package HTTP-fake consumers passed on both
frameworks. Fake tests do not prove live service compatibility, and the
TerraBase import engine the client targets is not merged or deployed.

Replace this pin with the first published ts-dotnet release that contains the
import client.

Run `nice -n 19 python3 scripts/verify-sdk.py` to check the recorded package,
repository identity, framework assemblies, contract paths/hashes and central pin.
