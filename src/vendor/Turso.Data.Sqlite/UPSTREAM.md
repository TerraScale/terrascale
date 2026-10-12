# Turso .NET driver (vendored)

The C# source of the Turso ADO.NET driver that TerraBase uses, copied from upstream and built in this solution, so
TerraScale can fix it without waiting for a release. MIT licensed: `LICENSE.md` and `NOTICE.md` are upstream's.

## Upstream

- Repository: https://github.com/tursodatabase/turso
- Path: `bindings/dotnet/src/Turso.Raw`, `bindings/dotnet/src/Turso.Data`, `bindings/dotnet/src/Turso.Data.Sqlite`
  (here `src/…`), plus the repository's `LICENSE.md` and `NOTICE.md`
- Tag: `v0.8.2`
- Commit: `5c168be5ef6a32ac966daf5983502a4f694b37ad`
- NuGet equivalents: `Turso.Data.Sqlite.Provider` (Turso.Data.Sqlite), `Turso.Data.Common` (Turso.Data, Turso.Raw)

Only these three projects come over: no upstream tests, samples, benchmarks or other providers.

## Native library

The Rust core (`libturso_sdk_kit`) is not built here: the upstream package `Turso.Data.Native` at the same version as
the tag (`Directory.Packages.props`) supplies the prebuilt library for every runtime. TerraScale's fixes are in the C#
driver only, and building the Rust core would add a Rust toolchain and its crates to every build. Keep the package
version equal to the tag.

## What is ours

- `UPSTREAM.md`, `patches/`, `Directory.Build.props`, `Directory.Build.targets` and `locks/`: how the driver builds in
  this solution (target framework, warnings as errors, lock files, the native package). They replace upstream's
  `bindings/dotnet` build files, which are not copied, and keep the repository's own analyzer and style rules, which
  are for TerraScale's code, off upstream's.
- `patches/*.patch`: TerraScale's changes to upstream files, one fix per patch, applied in name order. Every file
  under `src/`, `LICENSE.md` and `NOTICE.md` is upstream's with exactly these patches applied.
- The driver's regression tests: `tests/Turso.Data.Sqlite.Tests`.

## Updating

```sh
scripts/vendor/update-turso-driver.sh v0.8.3 --commit   # pristine import commit, then a commit with the patches
```

Then set `Turso.Data.Native` in `Directory.Packages.props` to the same version, restore (`make restore`, which
rewrites `locks/`), build and run `make terrabase-check` and the driver tests, and redo `SECURITY-AUDIT.md` (the
provenance, package, vulnerability, license and patch checks) for the new tag. Without `--commit` the script only
changes the working tree. Re-importing the current tag must leave no diff.

A patch that no longer applies stops the script with conflict markers in the working tree: resolve them, commit, and
regenerate that patch. A patch upstream has merged is deleted.

## Adding a fix

Edit the upstream file under `src/`, add its regression test to `tests/Turso.Data.Sqlite.Tests`, commit, then save
the commit's change to the vendored files as the next patch and amend the commit with it:

```sh
git format-patch -1 HEAD --relative=src/vendor/Turso.Data.Sqlite --start-number <next> -o src/vendor/Turso.Data.Sqlite/patches
git add src/vendor/Turso.Data.Sqlite/patches && git commit --amend --no-edit
```
