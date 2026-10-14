# Security and dependency audit of the vendored Turso .NET driver

Audited 2026-10-12 on the phase-1 head `1c800e32`, then patch 0004 and the Rust-side audit (section 6) on top; on
delta, locally; nothing live changed. Scope: the vendored C# driver (`src/`, `LICENSE.md`, `NOTICE.md`), the native package it builds on, and the
TerraScale patches (`patches/`). Conclusion: provenance and the package are verified as far as upstream's unsigned
release allows, no known vulnerable or deprecated package, MIT obligations met, and the patches contain nothing beyond
their stated fixes. Open items are listed at the end.

## 1. Provenance of the pristine import

- Upstream: https://github.com/tursodatabase/turso, tag `v0.8.2`: an annotated tag (object
  `5d68fe09c36149d395cbf870bf5c25b2e5970157`, tagger Pekka Enberg, message "Version 0.8.2") pointing at commit
  `5c168be5ef6a32ac966daf5983502a4f694b37ad` ("Turso 0.8.2", tree `def933c4…`).
- **Neither the tag nor the commit is signed**: no signature in either object, and GitHub's API reports
  `verification.verified = false, reason = unsigned` for both. Provenance therefore rests on fetching the tag from
  github.com/tursodatabase/turso over HTTPS; there is no upstream signature to check.
- Byte identity: a fresh clone of the tag (not the clone used for the import), compared by git object id (which hashes
  content and file modes) with the trees of the pristine import commit `b3e9812e`:

| Upstream path at `v0.8.2` | Object id | In `b3e9812e` |
| --- | --- | --- |
| `LICENSE.md` | `9573cadaf0b69893bf64bb595f06c4be150366e3` | identical |
| `NOTICE.md` | `c145d5660f75db5c3511b2874b28acc346eabe22` | identical |
| `bindings/dotnet/src/Turso.Raw` | `b64e346b734ab23341ead79a2de2fe19b4a89f37` | identical (`src/Turso.Raw`) |
| `bindings/dotnet/src/Turso.Data` | `792ebb057becd184bb5390959b950ac13366c4f8` | identical (`src/Turso.Data`) |
| `bindings/dotnet/src/Turso.Data.Sqlite` | `c94b5b1846f84aa9118d2cbfca8f6d0514f75b66` | identical (`src/Turso.Data.Sqlite`) |

  Commands: `git -C <fresh clone> rev-parse v0.8.2:<path>` against
  `git rev-parse b3e9812e:src/vendor/Turso.Data.Sqlite/<path>`. The update script's re-import of `v0.8.2` also
  reproduces the patched tree with no diff (`UPSTREAM.md`).

## 2. The native package `Turso.Data.Native` 0.8.2

- Pinned in `Directory.Packages.props` (`0.8.2`) and referenced by Turso.Raw only (`Directory.Build.targets`). Every
  lock file (`locks/*.json`, `src/TerraBase/packages.lock.json`) resolves `0.8.2` with contentHash
  `3ReOw1hTID2OmhQ3PNO3Vyk5DQ7iyPoXSLy8VeMgiaqTefUiTUFNmz7WpMyKTbno9y859nFXy0DBV0suPnaVbg==`.
- Source feed: nuget.org only (`NuGet.config` clears every other source; `https://api.nuget.org/v3/index.json`).
- Verified: a locked-mode restore of Turso.Raw into an empty packages folder
  (`dotnet restore --locked-mode --packages <empty> --force`) downloaded the package from nuget.org and accepted that
  content hash (NuGet's content hash excludes the repository signature). The `.nupkg` file itself (159,968,857 bytes) has
  SHA-512 `bZcJSB9I4msp6gi3E+KNr2k32mn/IU9d3VrTr0GWlZ727oNKA0x3YKX0I6G+JKNmREqPGCyn9FEcXLYPx8kl8Q==`, the same for a direct
  download from nuget.org's flat container and the copy in the local cache.
- Signature: `dotnet nuget verify --all` exits 0. The package carries nuget.org's repository signature
  (`CN=NuGet.org Repository by Microsoft`, certificate SHA-256 `1F4B311D9ACC115C8DC8018B5A49E00FCE6DA8E2855F9F014CA6F34570BC482D`,
  valid 2024-02-22 to 2027-05-18) and **no author signature**.
- Same upstream release: the nuspec's `<repository url="https://github.com/tursodatabase/turso"
  commit="5c168be5ef6a32ac966daf5983502a4f694b37ad">` is the commit the vendored source comes from, and its
  `buildTransitive/Turso.Data.Native.targets` is byte-identical to the vendored
  `src/Turso.Raw/buildTransitive/Turso.Data.Native.targets` (SHA-256 `07b897db…`).
- Native binaries it ships (`libturso_sdk_kit`, prebuilt by upstream; not rebuilt or reproduced here):

| Runtime | File | Bytes | SHA-256 (first 16) |
| --- | --- | ---: | --- |
| linux-x64 | `runtimes/linux-x64/native/libturso_sdk_kit.so` | 72,601,360 | `56378e447d2efef7` |
| linux-arm64 | `runtimes/linux-arm64/native/libturso_sdk_kit.so` | 74,983,576 | `4824cbc88260101a` |
| osx-x64, osx-arm64 | `libturso_sdk_kit.dylib` | 17,538,304, 14,415,720 | `5352143d776a0d3a`, `a22de5b3495aa881` |
| win-x64, win-arm64 | `turso_sdk_kit.dll` | 15,021,840, 12,250,888 | `08d5c581e4640dec`, `487d6002d61b38e0` |
| android-arm64, -arm, -x64, -x86 | `libturso_sdk_kit.so` | 77,403,216, 59,787,296, 75,142,944, 65,112,764 | `b25caa755eca917f`, `0902dda24b687df8`, `a3c4d57f39cde0a9`, `8931133b22f333a5` |
| ios-universal | xcframework (arm64, arm64-simulator) | 14,385,608, 14,382,800 | `09cd745fc9c8fd5e`, `b9356ae38d4dee8e` |

- What TerraBase ships: the release is `dotnet publish --runtime linux-x64` (and linux-arm64). NuGet's asset selection
  for `net11.0/linux-x64` takes only `runtimes/linux-x64/native/libturso_sdk_kit.so` (linux-arm64 likewise), as
  `src/TerraBase/obj/project.assets.json` shows, and a local `dotnet publish src/TerraBase -c Release -r linux-x64
  --self-contained` embedded it in the single-file `terrabase`. RID-less development builds copy every runtime folder.

## 3. Known vulnerable and deprecated packages

Run on delta against nuget.org (`dotnet list <project> package --vulnerable|--deprecated --include-transitive`):

```text
$ dotnet list src/vendor/Turso.Data.Sqlite/src/Turso.Raw/Turso.Raw.csproj package --vulnerable --include-transitive
The following sources were used:
   https://api.nuget.org/v3/index.json
The given project `Turso.Raw` has no vulnerable packages given the current sources.
$ dotnet list src/vendor/Turso.Data.Sqlite/src/Turso.Raw/Turso.Raw.csproj package --deprecated --include-transitive
The following sources were used:
   https://api.nuget.org/v3/index.json
The given project `Turso.Raw` has no deprecated packages given the current sources.
$ dotnet list src/vendor/Turso.Data.Sqlite/src/Turso.Data/Turso.Data.csproj package --vulnerable --include-transitive
The following sources were used:
   https://api.nuget.org/v3/index.json
The given project `Turso.Data` has no vulnerable packages given the current sources.
$ dotnet list src/vendor/Turso.Data.Sqlite/src/Turso.Data/Turso.Data.csproj package --deprecated --include-transitive
The following sources were used:
   https://api.nuget.org/v3/index.json
The given project `Turso.Data` has no deprecated packages given the current sources.
$ dotnet list src/vendor/Turso.Data.Sqlite/src/Turso.Data.Sqlite/Turso.Data.Sqlite.csproj package --vulnerable --include-transitive
The following sources were used:
   https://api.nuget.org/v3/index.json
The given project `Turso.Data.Sqlite` has no vulnerable packages given the current sources.
$ dotnet list src/vendor/Turso.Data.Sqlite/src/Turso.Data.Sqlite/Turso.Data.Sqlite.csproj package --deprecated --include-transitive
The following sources were used:
   https://api.nuget.org/v3/index.json
The given project `Turso.Data.Sqlite` has no deprecated packages given the current sources.
$ dotnet list src/TerraBase/TerraBase.csproj package --vulnerable --include-transitive
The following sources were used:
   https://api.nuget.org/v3/index.json
The given project `TerraBase` has no vulnerable packages given the current sources.
$ dotnet list src/TerraBase/TerraBase.csproj package --deprecated --include-transitive
The following sources were used:
   https://api.nuget.org/v3/index.json
The given project `TerraBase` has no deprecated packages given the current sources.
```

These checks cover NuGet packages only; the Rust crates compiled into `libturso_sdk_kit` are in section 6.

## 4. Licenses

- The vendored C# source is MIT (`LICENSE.md`, "Copyright 2024 the Turso authors"), kept verbatim with upstream's
  `NOTICE.md`; the MIT condition (keep the copyright and permission notice with copies) is met. The three projects
  declare `PackageLicenseExpression` MIT. No other license, copyright or third-party attribution text appears in the
  vendored source (searched for license, copyright, SPDX, "derived from", "ported from").
- `NOTICE.md` is upstream's repository-wide notice: it lists Apache-2.0 and MIT dependencies of other parts of the
  repository (Java and Python bindings, extensions, perf tools) and dual MIT/Apache-2.0 crates of the native core
  (crossbeam-skiplist, pastey, windows). None of them is in the vendored C# source; its `licenses/...` paths point at
  files of the upstream repository that are not vendored.
- Packages the vendored projects pull in: only `Turso.Data.Native` 0.8.2 (MIT expression; the package carries no license
  file), per `locks/*.json`. TerraBase no longer references `Turso.Data.Sqlite.Provider` or `Turso.Data.Common` (its
  lock file has neither). The standalone benchmarks `benchmark/dotnet/DrainBench` and `TursoSpike`, outside the
  solution, still use the NuGet provider.
- Our patches are changes to MIT code and stay under the same license.

## 5. Review of the patches

`patches/0001` to `0004` (the commits `39f687aa`, `17e02b3d`, `f4dcaa3a`, `22528c09`) change only
`src/Turso.Data.Sqlite/` `SqliteCommand.cs`, `SqliteDataReader.cs` and `SqliteConnection.cs`, with no new dependency,
I/O, configuration, reflection or public API.

- 0001 (`SqliteCommand.Execute`): wraps the existing statement handling in a `try`, whose `catch` disposes the statement
  and rethrows; the success path, including handing the statement to a reader, is unchanged.
- 0002 (`SqliteDataReader.NextResult`): the existing step and row count move into a `try` whose `finally` disposes the
  statement (previously disposed on success only).
- 0003 (`SqliteConnection.CheckTransactionAfterFailure`, called from four catch blocks): after a failed statement in an
  open, non-remote transaction, prepares and steps `BEGIN;` (disposed; its failure means still inside the transaction)
  and, when it succeeds, `ROLLBACK;` (disposed), and calls the provider's existing `MarkCompletedExternally(rolledBack:
  true)`. It runs on the failing call's thread, and only after a failure.
- 0004 (the residual risk this audit first found in 0003, fixed): the probe runs before the caller's error is rethrown,
  so a probe that failed itself (preparing its `BEGIN`, or its `ROLLBACK` after a successful `BEGIN`) replaced the
  caller's exception. The probe now swallows its own failures and marks the transaction ended before its `ROLLBACK`.
  Its statements are prepared through an internal per-connection hook, `ProbeStatements`, unset in production; the
  driver's tests see it through `InternalsVisibleTo` in our vendor build targets. Test:
  `TransactionTests.AFailingProbeNeverReplacesTheOriginalError` (probe failing at `BEGIN` and at `ROLLBACK`), which failed
  both ways before the fix ("the test breaks the probe" instead of the read's error) and passes after.

Patch 0005 was reviewed as a source delta on 2026-10-13. `TursoBindings.ThrowIfError` preserves the existing native
status in a new `TursoNativeException` subtype. Local command and connection error conversion maps only
`Corrupt` (133), `IoError` (134) and `NotADatabase` (132) to SQLite damage codes 11, 10 and 26; other local errors retain
code 1. The native ABI, package version, dependencies, I/O and configuration are unchanged. This lets TerraBase refuse
damaged copies on statement and open failures, rather than losing that information in generic error conversion.
`DamageErrorCodeTests` covers corrupt-page reads/writes, a non-database file and ordinary errors; runtime validation of
this delta remains pending. No failing-disk experiment qualifies the I/O mapping here.

Our build files (`Directory.Build.props`, `Directory.Build.targets`; not patches) set net11.0, warnings as errors, lock
files under `locks/`, not packable, `NU1507;NU1801` as in the root props, Turso.Raw's package id to its project name,
and the `Turso.Data.Native` reference.

### Patch 0005 (2026-10-13)

`SqliteDataReader.Abort()` resets an unfinished native statement before disposing it and discards unexecuted batch
statements. The existing native finalizer drains rows, which can invoke cancelled scan callbacks and roll back an
explicit transaction. `Turso.Raw` binds the `turso_statement_reset` export already present in the pinned v0.8.2 C API;
the native package, dependencies, formats and configuration do not change. Reset errors use the existing transaction
failure probe and exception conversion; cleanup still releases the statement and reader callback.

The real-native regression proves that unread callbacks and later SQL never execute, previous writes survive commit,
and abort is idempotent. The original TerraBase Slow regression proves cancellation between SQL rows releases the
reader and allows another query in the same transaction. Both targeted tests passed on mariogk-dev. This supplement
covers the patch; it does not repeat the package or Rust advisory audit above.

## 6. Rust crates in `libturso_sdk_kit`

Read-only, against upstream's workspace at `5c168be5ef6a32ac966daf5983502a4f694b37ad` (a fresh clone), with tools from
the nix store and nothing installed system-wide: `nix shell nixpkgs#cargo nixpkgs#rustc nixpkgs#cargo-audit
nixpkgs#cargo-deny` (cargo 1.95.0, cargo-audit 0.22.1, cargo-deny 0.19.6), `CARGO_HOME` in a scratch directory.

- Scope: the native library is the `turso_sdk_kit` crate (`crate-type = ["lib", "cdylib", "staticlib"]`). Its normal
  (shipped) dependency graph, `cargo tree --locked -p turso_sdk_kit -e normal`, has 219 crates for all targets and 158
  for `x86_64-unknown-linux-gnu` (six of them upstream's own: turso_core, turso_ext, turso_macros, turso_parser,
  turso_sdk_kit, turso_sdk_kit_macros). The workspace `Cargo.lock` has 849 packages (bindings, tests, tools).
- Advisories: `cargo-audit audit --file Cargo.lock` against the RustSec database (1,299 advisories, commit `25a288f6`,
  2026-10-12) reports 6 vulnerabilities and 13 warnings for the whole workspace. Only one is in the native library's
  graph:
  - **RUSTSEC-2026-0204, `crossbeam-epoch` 0.9.18** (pulled in by `turso_core`), "Invalid pointer dereference in
    `fmt::Pointer` impl for `Atomic` and `Shared`": formatting a null `Atomic`/`Shared` with `{:p}` dereferences it.
    Fixed in 0.9.20; no CVSS. Upstream code never formats these with `{:p}` (none of the six files using
    `crossbeam_epoch`, the MVCC checkpoint and the skip list, has a `:p}` format), so the flawed code path is not
    reached. Only an upstream release with crossbeam-epoch >= 0.9.20 removes it from the binary.
  - Not in the native library: h2 0.4.12 (RUSTSEC-2026-0258), quick-xml 0.26.0 (RUSTSEC-2026-0194, -0195), rkyv 0.7.46
    (RUSTSEC-2026-0235), rustls 0.23.35 (RUSTSEC-2026-0285), and the unmaintained, unsound and yanked warnings (bincode,
    bitmaps, number_prefix, paste, proc-macro-error2, unsafe-libyaml, yaml-rust, anyhow, lru, memmap2, scc, chacha20).
- Licenses: `cargo-deny --manifest-path sdk-kit/Cargo.toml list` resolved every crate of the linux-x64 graph: MIT (129),
  Apache-2.0 (105), Unicode-3.0 (22), Apache-2.0 WITH LLVM-exception (4), Unlicense (3), Zlib (3), BSD-3-Clause (2),
  ISC, BSL-1.0, BSD-2-Clause; all permissive, no copyleft, none unknown or missing. Across all targets the only copyleft
  option is `r-efi` (UEFI targets only, `MIT OR Apache-2.0 OR LGPL-2.1-or-later`), and `zerocopy-derive` 0.8.26 (a
  compile-time proc-macro, other targets) is the one crate cargo-deny did not list.
- Obligations: Apache-2.0, BSD, Zlib and Unicode-3.0 ask for attribution when the binary is distributed. TerraBase is
  not distributed (it runs as TerraScale's service; no published binaries), so none applies today; distributing a
  TerraBase binary would need these notices.

### Patch 0006 (2026-10-13)

Refused local connections use the existing connection/statement deinit exports instead of graceful close, which can
checkpoint. Native operations must have stopped and executing write statements must have ended before this terminal
cleanup. Statement ownership uses long weak references and one per-connection release lock so teardown can release a
statement whose SafeHandle finalizer already marked it closed. No native package, ABI, dependency or wire format changes.

The pinned source and Linux x64 binary were inspected: connection Drop releases locks and ownership without the graceful
checkpoint; statement Drop can finish an executing write, which is why this is not an interrupt API. Driver regressions
cover actual last-connection healthy checkpointing, refused byte preservation and zero file descriptors, provider disposal
and rollback failures, and deterministic disposal/finalizer races. TerraBase integration coverage holds a reader across
swap and verifies retained bytes and replacement isolation after late disposal. These checks do not qualify arbitrary
power loss, concurrent native abandonment or every native architecture.

## Open items

- The upstream release is unsigned (tag and commit): nothing to verify beyond the fetch from GitHub over HTTPS.
- The prebuilt native library is trusted as upstream built it (repository-signed by nuget.org, not author-signed, not
  rebuilt here); its graph is audited from the source lock file (section 6), not from the binary itself.
- RUSTSEC-2026-0204 (`crossbeam-epoch` 0.9.18) is in the native library: not reachable through upstream's code (no
  `{:p}` formatting of crossbeam pointers); it goes with an upstream release on crossbeam-epoch >= 0.9.20.
- Distributing a TerraBase binary would bring attribution duties for the Rust crates (section 6).
