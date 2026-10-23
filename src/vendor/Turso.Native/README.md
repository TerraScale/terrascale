# Turso 0.8.2: combined FTS + encryption contract1 artifacts

This delivery path supersedes the build/delivery instructions in
`docs/terrabase/turso-fts-native.md`. Normal PR, merge-group, main, Fast, Slow and
image jobs download and verify the pinned release; they never compile Rust.
The required `native` job still gates `test`. Native maintenance is an explicit
`workflow_dispatch` operation on the existing `.github/workflows/ci.yml`.
Maintenance runs build once per architecture and prepare an artifact for parent
publication; they do not qualify Fast, publish images or upload a release.

Release: `turso-native-v0.8.2-fts-contract1-20261023`.
The tag and release were absent when checked on 2026-10-23. Never replace or
append assets to `turso-native-v1`: it contains encryption-only libraries.
`SHA256SUMS` deliberately has no binary hashes until real combined artifacts
pass qualification. Downloads and managed builds fail closed until it is filled.

## Pinned inputs and native behavior

- Turso v0.8.2, commit `5c168be5ef6a32ac966daf5983502a4f694b37ad`, archive SHA-256
  `7ea83d5e28577cf9d8655b77705902379e06b82fb29424f6ea328b5f26f17ecb`.
- Rust `1.88.0-bookworm`, multiarch image digest
  `sha256:af306cfa71d987911a781c37b59d7d67d934f49684058f96cf72079c3626bfe0`;
  locked Cargo dependencies, `lib-release`, fixed `SOURCE_DATE_EPOCH=1791270348`
  and the existing source-path remap and `-z,nodelete` flags.
- Exact contract1 patch from PR464 head
  `042020beeb0b27b62c2d8e8e77985a52c457589b`, SHA-256
  `eadb14ec88ac351c938908443bcb89da324411e4eb8f8d068b3cc755fc0808ae`,
  followed by the reviewed `scripts/vendor/turso-native-budget.patch`.
  The build checks the contract patch hash before applying it.
- Build `cargo +1.88.0 build --locked --package turso_sync_sdk_kit --features
  turso_sdk_kit/fts --profile lib-release`. Keep defaults, including encryption
  and pure-rust crypto. The sync library supplies core and sync C exports and is
  packaged as `libturso_sdk_kit.so`.
- `index_method` must be enabled in each database's experimental feature list for
  FTS; `encryption` plus cipher `aegis256` and a key are required for encrypted
  opens. These are native API requirements, not product feature switches.

The native error patch maps typed `CompletionError::DecryptionError` to
`TURSO_CORRUPT` (133) and exports
`turso_terrascale_storage_error_contract() == 1`. The SQLite facade maps corruption
to SQLite status 11. SQL-generated error text alone remains insufficient to
classify physical damage. No managed encryption implementation is imported here.

The FTS smoke retains transaction/rollback, bounded loading/scoring, statement
lifetime, physical-clause and positional/carrying-slop admission, cancellation,
count/ordered readers, recovery and CAST candidate seek-plan coverage. The
expression probe reports actual support without rejecting a healthy artifact
when an optional expression form is unsupported. The engine uses derived columns.
The added contract smoke tests persisted encrypted FTS after reopen, marker 1,
a SQL trigger spoof remaining a constraint, and real ciphertext tamper producing
native corruption. Native admission bounds opaque scoring allocations; it does
not promise a hard deadline inside third-party work.

## Parent bootstrap after the push/queue gate opens

The existing CI workflow already supports dispatch on default, allowing its
maintenance route to run from this pushed branch before merge. A new standalone
workflow would not have that bootstrap property. This route is prepared and
statically checked; its first real dispatch remains to be proven.

1. Push the reviewed pipeline head, then dispatch **once**:

   ```sh
   gh workflow run ci.yml --repo TerraScale/terrascale --ref feat/full-text-search -F native_artifacts=true
   ```

   `native-build` uses `ubuntu-24.04` and **real** `ubuntu-24.04-arm`. The helper
   rejects a host/target mismatch; no qemu/cross-architecture publication is
   accepted. Each build checks ELF64 little-endian and e_machine x64 `3e00` /
   arm64 `b700`, rejects RPATH/RUNPATH and absolute dynamic dependencies, and runs
   the full FTS and contract1 smokes before producing any output. Initial normal
   CI fails quickly on pending pins; it cannot run Fast against an unqualified
   substitute. Parent owns the push, dispatch, queue gate and check watching.

2. After the maintenance run succeeds, download its assembled artifact:

   ```sh
   gh run download "$NATIVE_RUN_ID" --repo TerraScale/terrascale --name turso-native-release --dir artifacts/fts-contract1-release
   (cd artifacts/fts-contract1-release && sha256sum --check SHA256SUMS)
   ```

   Review both receipts, ABI exports, dynamic dependencies and smoke logs.
   Assets include the verified source archive, Cargo.lock, both exact patches,
   build recipe/helper, C smoke sources, upstream LICENSE/NOTICE and per-arch
   dependency-license archives with package manifests/checksum receipts. All
   release files are covered by `SHA256SUMS`; `NATIVE-SHA256SUMS` contains just the
   two published library names and real hashes.

3. Prove byte-identical library SHA-256 values from a second independently
   authorized maintenance run using the **same native inputs**. There is no loop
   or automatic second build in CI. Keep both run URLs and receipts with the
   publication evidence; ordinary downstream PRs always reuse the release.

   Before release publication, copy `NATIVE-SHA256SUMS` to the checked-in pin
   file and install the two candidate libraries under
   `src/vendor/Turso.Data.Sqlite/native/runtimes/<rid>/native/libturso_sdk_kit.so`.
   Parent must run the admitted Release/Fast and published single-file loading
   qualification with these exact candidate bytes and the integrated managed
   changes. This supplies pre-publication Fast proof without an unpublished
   release download or a second native build inside normal CI.

4. Parent publishes the new release after qualification, using all assembled
   assets and reviewed evidence as release notes:

   ```sh
   gh release create turso-native-v0.8.2-fts-contract1-20261023 \
     --repo TerraScale/terrascale --target "$PIPELINE_SHA" \
     --title 'Turso 0.8.2 FTS + storage contract 1' \
     --notes-file artifacts/fts-contract1-release-notes.md artifacts/fts-contract1-release/*
   ```

   Verify the release/tag is still absent before creation. Do not use `--clobber`
   or overwrite published assets. Relay the exact new tag to the parent/COS
   publication owner after upload. Commit/push the actual pins in the final batch.

5. Qualify normal CI at the final pinned head. `native` downloads and verifies
   both architectures once; Fast, Slow and image jobs consume that verified run
   artifact. Docker and MSBuild verify hashes and ELF architecture again before
   using cached bytes. Retain final Fast and published single-file loading proof,
   including the parent-owned encryption regressions. No NuGet fallback exists.

For an admitted native dev-host build, the same helper accepts
`nice -n 19 scripts/vendor/build-turso-native.sh amd64 artifacts/native-x64`
or `arm64` on a real arm64 host. Parent alone coordinates heavy jobs.
Assembly is `scripts/vendor/prepare-turso-native-release.sh <x64-output>
<arm64-output> <empty-release-directory>`; inputs must pass manifests and both
smokes, with identical shared source/patch receipts.

**Needs format bump.** StorageFormat.cs is unchanged; the parent storage lane
owns the integrated on-disk format decision. Pending combined native builds,
independent-run reproducibility, final binary pins, publication and managed/Fast
qualification are not implied by static replay or C syntax checks.
