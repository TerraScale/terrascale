# ts-dotnet package pin

The dashboard consumes TerraScale.DotNet 0.0.8 from merged ts-dotnet revision
`c0cf10216c64844c688f4b889a870d7bec6fe101` (PR59). This adds account deletion
receipts and account/organization completion proofs used by the dashboard's
automatic deletion continuation. The owning ts-auth contract landed in
TerraScale/terrascale PR608 at `d9776939cf5d7f71d79fa91dde5a6a8788b124eb`.

The package was built with the SDK's `scripts/pack-reproducible.py`: two clean
locked builds produced identical bytes, and installed-package HTTP fake consumers
passed on net10.0 and net11.0. It is vendored, not claimed to be published to a
registry. NuGet.Config maps this package exclusively to the committed feed.

`provenance.json` records the source revision, toolchain, package and assembly
hashes, and every packed contract hash. CI runs `scripts/verify-sdk.py` to check
those bytes and the central package pin. HTTP fakes do not prove live service
compatibility.
