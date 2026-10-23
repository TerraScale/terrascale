# ts-dotnet package pin

The dashboard consumes TerraScale.DotNet 0.0.4 from merged, pushed ts-dotnet
revision `8d4aa96e2585f88ae16c6e9e71cf70756356597f` (PR48). It includes
collection change webhooks, account deletion, all-regions placement and the
current generated-ULID import contract. Source primary keys remain document
fields; the dashboard no longer sends the retired missing-primary-key policy.
The package was built with ts-dotnet's `scripts/pack-reproducible.py`; it is
vendored here and is not claimed to be published to a registry. NuGet.Config
maps the package exclusively to this committed feed; other dependencies come
from nuget.org. The package supports net10.0 and net11.0; the dashboard targets
net10.0.

The source, toolchain, package/assembly hashes and all raw contract hashes are
recorded in provenance.json, including both framework assemblies and all 75
packed contracts. Two clean locked builds with independent caches
produced identical bytes, and installed-package HTTP-fake consumers passed on
both frameworks. Fake tests do not prove live service compatibility.

The package SHA-256 is
`709641e5038f01bac616f90014702bb7c32f0040ee0d315aed483eed5c23d8a8`.
The supplied dashboard provenance SHA-256 is
`2d0da4680b8730bc1fc7fb9162769d66f2125d099f5c9f0834c69977828a1ccf`.

Run `nice -n 19 python3 scripts/verify-sdk.py` to check the recorded package,
repository identity, framework assemblies, contract paths/hashes and central pin.
