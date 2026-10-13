# Package release

`TerraScale.DotNet` is published to GitHub Packages
(`https://nuget.pkg.github.com/TerraScale/index.json`, private like this
repository) and to nuget.org (public). Versions stay `0.0.X`; every release,
breaking or not, takes the next unused number. A published version is never
replaced or deleted.

## Releasing

1. Bump `<Version>` in `src/TerraScale.DotNet/TerraScale.DotNet.csproj` in a
   pull request and merge it once CI passes.
2. Tag the merged commit on `main` and push the tag:
   `git tag v0.0.X <commit> && git push origin v0.0.X`.

The tag starts [`release.yml`](../.github/workflows/release.yml). Its `package`
job checks the tag equals `v` + `<Version>` and the commit is on `main`,
builds, runs the tests on .NET 10 and 11, and qualifies two clean locked package
builds before advancing one artifact, without write access. It logs the package
SHA-256. Then:

1. `github-packages` checks that hash and pushes with the job's
   `GITHUB_TOKEN`. The package is linked to this repository through
   `RepositoryUrl`, so it inherits its private visibility and access.
2. `restore` copies
   [`tests/TerraScale.DotNet.PackageConsumer`](../tests/TerraScale.DotNet.PackageConsumer)
   out of the repository, restores the exact version from GitHub Packages with
   fresh NuGet caches, checks the restored `.nupkg` hash and runs the consumer
   on both frameworks. It fails unless the loaded assembly reports
   `<version>+<tagged commit>`.

nuget.org is never pushed automatically. After reviewing the GitHub Packages
release, run the workflow by hand on the same tag:

```bash
gh workflow run release.yml --ref v0.0.X -f sha256=<package SHA-256> -f nuget-user=<nuget.org profile name>
```

That run repeats `restore` from GitHub Packages against the given hash, then
`nuget-org` exchanges its GitHub OIDC token for a one-hour nuget.org key
([Trusted Publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing))
and pushes those bytes. No API key is stored anywhere. The nuget.org policy
names repository owner `TerraScale`, repository `ts-dotnet`, workflow file
`release.yml` and no environment; `nuget-user` is the profile name of the
account that created it. A policy for this private repository is only
temporarily active for 7 days until its first publish; restart that window on
nuget.org if it lapses. The policy cannot check the ref, and the TerraScale
organization's GitHub Free plan offers neither environments nor tag rulesets
for a private repository, so anyone who can push a workflow to this
repository can publish; keep write access to its admins.

nuget.org repository-signs packages, so its download adds
`.signature.p7s`; every other entry is byte-identical to the GitHub Packages
package. Search and restore on nuget.org can lag the push by several minutes.

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
