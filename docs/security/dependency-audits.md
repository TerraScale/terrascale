# Dependency advisory coverage

`scripts/check-dependencies.sh` restores and audits the SDK, SDK tests, native
advisory checker and checker validation tests in locked mode. Its inventory
check rejects a committed NuGet lock outside those four graphs. The checker
runs native `dotnet list package --no-restore --vulnerable --include-transitive`
against the restored assets, including both SDK target frameworks. Every
reported severity fails; invalid output, missing sources, diagnostics, scanner
failure and timeout also fail. The validation tests exercise these controls.

Native `dotnet list` omits RID-only packages from its normal target inventory.
`scripts/check-locked-runtime-packages.py` separately restores each such exact
ID/version into a temporary audit project, verifies its content hash against
the committed lock and runs the same checker. This covers the file-app native
compiler NuGet package, including multiple locked versions if present. These
projects are never compiled or shipped; binary contents need separate tooling.

Full build paths also run the checker validation tests. The existing CI test
job runs the advisory gate even when documentation changes or
exact main-build proof avoid repeating compilation. Full CI and release builds
run the same gate through `scripts/check-release.sh`. The package cache scope,
event filters and exact-tree proof remain unchanged; the key includes every
committed lock, including the checker-test lock. Advisory HTTP responses
are not included in the package cache.

The package consumer is a release template whose exact SDK version is supplied
by the release. It has no committed resolved lock. Reproducible package
qualification restores the actual built package into a fresh consumer, verifies
its bytes and audits the resolved graph before running its HTTP-fake checks on
net10.0 and net11.0. The output retains that consumer lock and audit result.
The registry restore job independently repeats the byte verification and audit
against the GitHub Packages consumer before the existing nuget.org publication
job may run. It retains the registry consumer lock and audit result separately.

These scans cover NuGet package advisories for the resolved graphs. They do not
qualify the host OS, SDK/runtime installation, GitHub Actions implementations,
native compiler binaries or live services. Package checksums bind qualified
bytes to the existing source revision; a clean advisory report alone is not
runtime reachability or production exploitability proof. No package publishing,
tagging, registry credentials or release approval is authorized by this change.
