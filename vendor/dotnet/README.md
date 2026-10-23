# ts-dotnet package pin

The dashboard consumes TerraScale.DotNet 0.0.5 from merged, pushed ts-dotnet
revision `fa797b0c9d2e33763bd8ce2de45a00cfc418c4db` (PR54). It adds the API key
`LastUsedAt` field on top of PR48's collection change webhooks, account
deletion, all-regions placement and generated-ULID import contract, PR51's
public-origin region pinning and PR52's project tenant management client.
The package was built with ts-dotnet's `scripts/pack-reproducible.py`; it is
vendored here and is not claimed to be published to a registry. NuGet.Config
maps the package exclusively to this committed feed; other dependencies come
from nuget.org. The package supports net10.0 and net11.0; the dashboard targets
net10.0.

The source, toolchain, package/assembly hashes and all raw contract hashes are
recorded in provenance.json, including both framework assemblies and all 76
packed contracts. Two clean locked builds with independent caches
produced identical bytes, and installed-package HTTP-fake consumers passed on
both frameworks. Fake tests do not prove live service compatibility.

The package SHA-256 is
`a02060cbbdf4fa280d0e79f37fbb448f8c88c2d895e5d376c38bec65dfb087fd`.
The supplied dashboard provenance SHA-256 is
`f20434c18caafdf04dbaedacfcf44160806cc79b7d6d5dd0073fbbf8ca419270`.

Run `nice -n 19 python3 scripts/verify-sdk.py` to check the recorded package,
repository identity, framework assemblies, contract paths/hashes and central pin.
