# Package release

`TerraScale.DotNet` is published to GitHub Packages
(`https://nuget.pkg.github.com/TerraScale/index.json`) and to nuget.org (public). Versions stay `0.0.X`; every release,
breaking or not, takes the next unused number. A published version is never
replaced or deleted.

## Releasing

The repository is public at [TerraScale/terrascale](https://github.com/TerraScale/terrascale), with this SDK in `sdk/dotnet/`.

Ship a version bump in `src/TerraScale.DotNet/TerraScale.DotNet.csproj`, then tag the green `main` commit with `sdk/dotnet/v0.0.X`. The monorepo workflow is `.github/workflows/sdk-dotnet-release.yml`.

See [automatic tagged NuGet releases](automatic-nuget-releases.md) for exact qualification, GitHub Packages verification and nuget.org publication. Both supported target frameworks, net10.0 and net11.0, remain part of qualification. Package identities and `0.0.X` versioning stay the same.

## Consuming

From nuget.org, reference the package as usual:
`<PackageReference Include="TerraScale.DotNet" Version="0.0.X" />`.

From GitHub Packages, add the feed and map the package to it:

```xml
<configuration>
  <packageSources>
    <add key="github" value="https://nuget.pkg.github.com/TerraScale/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="github"><package pattern="TerraScale.DotNet" /></packageSource>
    <packageSource key="nuget.org"><package pattern="*" /></packageSource>
  </packageSourceMapping>
  <packageSourceCredentials>
    <github>
      <add key="Username" value="YOUR_GITHUB_USER" />
      <add key="ClearTextPassword" value="%GITHUB_PACKAGES_TOKEN%" />
    </github>
  </packageSourceCredentials>
</configuration>
```

Locally, `GITHUB_PACKAGES_TOKEN` is your own classic token with
`read:packages`. Another repository's workflow can use its `GITHUB_TOKEN`
(`permissions: packages: read`) only after a package admin adds that
repository under the package's **Manage Actions access**.

## Reproducible local qualification

The SDK toolchain is pinned in `global.json`; source/test lock files pin the
restored graph. CI and release builds restore in locked mode. A dependency change
must regenerate and review the affected lock files before advancing the artifact.
Both workflows run `bash scripts/check-release.sh`, including a locked restore
of the vulnerability checker and tests on both frameworks. That test build keeps
source paths intact for auth fixture lookup; only package builds normalize paths.

From a clean committed worktree, run:

```sh
nice -n 19 python3 scripts/pack-reproducible.py /tmp/ts-dotnet-qualified-package
```

The script exports that exact commit twice, restores into independent NuGet
caches, builds both package targets with normalized source paths and NuGet archive
timestamps fixed to the source commit time (`SOURCE_DATE_EPOCH`), and requires
identical package bytes. It then restores the exact package into the existing
installed-package consumer with another fresh cache and runs its HTTP-fake
checks on .NET 10 and .NET 11. The output contains the package and source,
toolchain, package/assembly hashes and qualification in `provenance.json`.
The package includes `contracts/auth/**` and `contracts/terrabase/**` with their
relative hierarchy intact, including binary producer archives and explicitly
unmerged inputs. Qualification compares every packed contract's raw bytes with
the source and records its SHA256 by path. Packaging a pending producer input
does not accept that producer change. Tests copy the same hierarchy beneath
`AppContext.BaseDirectory`; manifest artifact paths remain relative to their
contract root.

These local bytes are neither a published release nor live-service qualification.
Select a new `0.0.X` version only after the owning producer contracts and SDK
changes are accepted. Consumers advance their package, provenance and locks
atomically to the same qualified artifact. A frozen monorepo SDK source pin is a
handoff request to its owner, never an excuse to mix consumer revisions.
