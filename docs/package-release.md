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
builds, runs the tests on .NET 10 and 11 and packs once, without write access.
The later jobs only push those bytes, checking their SHA-256 first:

1. `github-packages` pushes with the job's `GITHUB_TOKEN`. The package is
   linked to this repository through `RepositoryUrl`, so it inherits its
   private visibility and access.
2. `verify-github-packages` copies
   [`tests/TerraScale.DotNet.PackageConsumer`](../tests/TerraScale.DotNet.PackageConsumer)
   out of the repository, restores the exact version from GitHub Packages with
   fresh NuGet caches, checks the restored `.nupkg` hash and runs the consumer
   on both frameworks. It fails unless the loaded assembly reports
   `<version>+<tagged commit>`.
3. `nuget-org` exchanges the job's GitHub OIDC token for a one-hour nuget.org
   key ([Trusted Publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing))
   and pushes. The nuget.org policy names owner `TerraScale`, repository
   `ts-dotnet`, workflow `release.yml` and no environment. No API key is
   stored anywhere. If this job fails because the policy is missing, re-run
   the failed job after adding it; it pushes the same artifact.

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
