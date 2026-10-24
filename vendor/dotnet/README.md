# ts-dotnet package pin

The dashboard consumes TerraScale.DotNet 0.0.6 from merged ts-dotnet revision
`408044452f0a71c3725866bcc6b95603714acb1f` (PR50). Account and project deletion
return true only for a confirmed 204; a pending 202 returns false. Organization
deletion retains its 204-only contract. Dashboard callers preserve local records
until deletion is confirmed and display closure/binding conflicts for retry.

The package was built with the SDK's `scripts/pack-reproducible.py`: two clean
locked builds produced identical bytes, and installed-package HTTP fake consumers
passed on net10.0 and net11.0. It is vendored, not claimed to be published to a
registry. NuGet.Config maps this package exclusively to the committed feed.

`provenance.json` records the source revision, toolchain, package and assembly
hashes, and every packed contract hash. CI runs `scripts/verify-sdk.py` to check
those bytes and the central package pin. HTTP fakes do not prove live service
compatibility.
