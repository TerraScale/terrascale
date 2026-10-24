# ts-dotnet package pin

The dashboard consumes TerraScale.DotNet 0.0.9 from merged ts-dotnet revision
`1c5d6b7564e2699adb45361c4701d42f3ede90c3` (PR60). It adds typed hosted
branding and configuration settings for the auth contract merged in terrascale
PR609 (`8cf991cc1f06a436ec3000934cabdf3335b968d4`).

This revision also contains PR59's account deletion receipts and account/organization
completion proofs used by the dashboard's automatic deletion continuation. That
contract landed in TerraScale/terrascale PR608 at
`d9776939cf5d7f71d79fa91dde5a6a8788b124eb`; SDK PR59 merged at
`c0cf10216c64844c688f4b889a870d7bec6fe101` as 0.0.8. The existing 0.0.8
vendor artifact is retained, while the central pin and locks select only 0.0.9.
Query explain and nullable query-shape latency APIs remain available.

The package was built with the SDK's `scripts/pack-reproducible.py`: two clean
locked builds produced identical bytes, and installed-package HTTP fake consumers
passed on net10.0 and net11.0. It is vendored, not claimed to be published to a
registry. NuGet.Config maps this package exclusively to the committed feed.

`provenance.json` records the source revision, toolchain, package and assembly
hashes, and every packed contract hash. CI runs `scripts/verify-sdk.py` to check
those bytes and the central package pin. HTTP fakes do not prove live service
compatibility.
