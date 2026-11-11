# Dependency advisory coverage

`scripts/check-dependencies.sh` restores and audits the SDK, the Entity Framework
Core provider and the native advisory checker in locked mode. Its inventory
check rejects a committed NuGet lock outside those three graphs. The checker
runs native `dotnet list package --no-restore --vulnerable --include-transitive`
against the restored assets, including both SDK target frameworks. Every
reported severity fails; invalid output, missing sources, diagnostics, scanner
failure and timeout also fail.

Native `dotnet list` omits RID-only packages from its normal target inventory.
`scripts/check-locked-runtime-packages.py` separately restores each such exact
ID/version into a temporary audit project, verifies its content hash against
the committed lock and runs the same checker. This covers the file-app native
compiler NuGet package, including multiple locked versions if present. These
projects are never compiled or shipped; binary contents need separate tooling.

The release qualification runs this gate through `scripts/check-release.sh`.

Before the release publishes to nuget.org, its `nuget-org` job downloads the
package back from GitHub Packages and requires the SHA-256 that qualification
recorded.

These scans cover NuGet package advisories for the resolved graphs. They do not
qualify the host OS, SDK/runtime installation, GitHub Actions implementations,
native compiler binaries or live services. Package checksums bind qualified
bytes to the existing source revision; a clean advisory report alone is not
runtime reachability or production exploitability proof. No package publishing,
tagging, registry credentials or release approval is authorized by this change.
