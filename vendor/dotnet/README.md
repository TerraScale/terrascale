# ts-dotnet package pin

The dashboard consumes TerraScale.DotNet `0.0.14` from merged ts-dotnet revision
`cb29e2f9ec0ca835be72af6df2549588de5976f4` (PR94). It adds purpose-bound step-up
proofs and deletion continuation grants for the coordinated auth PR767 rollout.
The package also carries current main’s contracts and fixed SDK request deadlines.

`provenance.json` records the immutable source revision/tree, toolchain, package,
assembly and contract hashes. Two clean locked builds produced identical package
bytes, and the installed-package HTTP consumer passed on net10.0 and net11.0.
NuGet publication and live-service qualification are separate gates. Earlier
artifacts remain retained; only `0.0.14` is selected by the central version pin.

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
