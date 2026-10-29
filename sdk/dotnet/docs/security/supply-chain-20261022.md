# Supply-chain audit — 2026-10-22

Base: `c3e3ea69d45b1d5de674e6d7bbb6dc2c0a4a86b7`.

`dotnet list TerraScale.DotNet.slnx package --vulnerable --include-transitive --format json` audited both SDK/test projects with nuget.org advisory coverage: zero findings (high/critical 0; lower 0). Dependency files are unchanged.

The installed-package release consumer is not part of the solution. Its standalone audit failed because `TerraScaleDotNetVersion` is supplied only during release; a diagnostic restore confirmed the package is absent from nuget.org. No clean claim covers that release fixture or a published package.

Both active workflows declare `contents: read`; CI has no writers or repository secrets. Release scopes are job-specific: `packages: write` publishes retained package bytes; `id-token: write` exchanges NuGet Trusted Publishing identity after successful restore/hash verification. Other requested write scopes (contents/deployments/issues/pull-requests/actions) are absent. Read-only repository defaults remain appropriate.

All five distinct remote action pins were verified through their upstream repository commit API. No PR-target/workflow-run release path, fork secret exposure, public self-hosted runner or free-form event-to-shell interpolation was found.

Release defense improvement: require tag refs before the manual restore and OIDC publication jobs start. Existing validation still requires the version tag, artifact SHA-256 and qualified restore. This blocks unsupported branch dispatches earlier; no confirmed secret exposure prompted it.

Raw scanner logs, exact commands/base SHAs and writer inventory are retained by the owning security audit under `.cos/security-20261022/evidence/ts-dotnet/` in the workspace. No product version/API/storage changes. CI is the build/test gate; no local full suites were run.
