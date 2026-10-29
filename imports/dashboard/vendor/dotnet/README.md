# ts-dotnet package pin

The dashboard consumes candidate TerraScale.DotNet `0.0.20` from ts-dotnet PR103,
revision `4cda2df01a936b16f0f22901db7c39c7f285b1a8`. It adds typed sign-in email
change operations. Merge the SDK and owning auth PR822 before this companion.
This is a locally qualified candidate, not a published or live-qualified release.

`provenance.json` records the immutable source revision/tree, toolchain, package,
assembly and contract hashes. Two clean locked builds produced identical package
bytes, and the installed-package HTTP consumer passed on net10.0 and net11.0.
NuGet publication and live-service qualification are separate gates. Earlier
artifacts remain retained; only `0.0.20` is selected by the central version pin.

To regenerate from a clean checkout of that exact SDK revision:

```sh
nice -n 19 python3 scripts/pack-reproducible.py /path/outside/sdk/qualified-package
```

Copy the resulting nupkg and `provenance.json` into this directory, update the
TerraScale.DotNet version in `Directory.Packages.props`, and regenerate both
NuGet lock files with `dotnet restore TerraScale.Dashboard.slnx --force-evaluate`.
Use a fresh package cache when comparing candidate bytes. Commit the package,
provenance, pin and lock files together. `scripts/verify-sdk.py` verifies their
identity, repository commit, assembly hashes and every packed contract.
