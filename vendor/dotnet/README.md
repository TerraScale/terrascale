# ts-dotnet package pin

The dashboard consumes TerraScale.DotNet `0.0.19` from merged ts-dotnet revision
`632440121a76b6a3860db127a4bc00c365de266d` (after PR101). It adds
`Platform.SignOutEverywhereAsync` for account-wide sign-out, on top of 0.0.15 to
0.0.18 (transient-failure retries, database rename, HTTP/2 negotiation).

`provenance.json` records the immutable source revision/tree, toolchain, package,
assembly and contract hashes. Two clean locked builds produced identical package
bytes, and the installed-package HTTP consumer passed on net10.0 and net11.0.
NuGet publication and live-service qualification are separate gates. Earlier
artifacts remain retained; only `0.0.19` is selected by the central version pin.

To regenerate from a clean checkout of that exact merged SDK revision:

```sh
nice -n 19 python3 scripts/pack-reproducible.py /path/outside/sdk/qualified-package
```

Copy the resulting nupkg and `provenance.json` into this directory, update the
TerraScale.DotNet version in `Directory.Packages.props`, and regenerate both
NuGet lock files with `dotnet restore TerraScale.Dashboard.slnx --force-evaluate`.
Use a fresh package cache when comparing candidate bytes. Commit the package,
provenance, pin and lock files together. `scripts/verify-sdk.py` verifies their
identity, repository commit, assembly hashes and every packed contract.
