# Patched Turso native runtime

Encrypted TerraBase databases require TerraScale's native storage-error
contract, version 1. This supersedes the upstream-only native-runtime notes in
`../Turso.Data.Sqlite/UPSTREAM.md` for Linux x64 and arm64. Other platforms
retain the upstream runtime and refuse encrypted opens without this contract.

## Source and correction

The source is `tursodatabase/turso` commit
`5c168be5ef6a32ac966daf5983502a4f694b37ad` (v0.8.2), with
`patches/0001-preserve-page-authentication-damage.patch` applied. The patch maps
the typed `CompletionError::DecryptionError` to native `TURSO_CORRUPT` (133)
before it becomes an untyped string. The SQLite facade already maps that
status to `SQLITE_CORRUPT` (11), which the storage recovery paths recognize.

SQL can deliberately produce the same error text as a failed page
authentication. Error-text matching cannot distinguish those cases.
`quick_check` also evaluates schema expressions and cannot safely confirm a
physical authentication failure. No managed classifier or confirmation query
is used. The exported `turso_terrascale_storage_error_contract()` returns 1;
encrypted opens reject a missing or different contract before supplying a key.

## Building and delivery

Run `scripts/vendor/build-turso-native.sh linux-x64` or `linux-arm64` through
the admitted dev-host executor. The script pins Rust 1.88.0, uses the upstream
lock file and `release-official` profile, and builds `turso_sync_sdk_kit`.
The sync library supplies both local and sync ABI exports and is renamed to
`libturso_sdk_kit.so`, following upstream's .NET packaging convention.
Packaging uses patchelf 0.15.2 to remove builder-specific runtime search paths
from the release copy, retaining the original compiler output separately.

Each architecture produces the library, its export list and a build receipt
under ignored `artifacts/turso-native/`. The receipt identifies the source,
toolchain, target, profile, patch hash, lock-file hash and library hash. Record
both original and packaged hashes and the patchelf version. Release
assets must include these receipts and the applied patch. Preserve upstream's
license and notices from `../Turso.Data.Sqlite/` with the distribution.

`Turso.Native.targets` pins the release asset URLs and SHA-256 digests. Every
build or publish verifies the downloaded or cached bytes. Linux uses the
replacement without copying the upstream native asset. Consumer publish
targets classify the replacement as native so TerraBase's existing native
self-extraction setting includes it in the single executable. CI consumes
these artifacts and does not compile Rust.

Before publishing an artifact, verify its ABI exports, marker result, ELF
architecture and runtime dependencies. Run the genuine tamper and SQL-spoof
regressions through the final library and prove published single-file loading.
An artifact containing host-specific Nix library paths is not portable to the
production runtime and must not be released.
