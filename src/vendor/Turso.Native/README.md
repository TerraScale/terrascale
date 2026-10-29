# Turso 0.8.2: combined FTS + encryption contract1 artifacts

This delivery path supersedes the build/delivery instructions in
`docs/terrabase/turso-fts-native.md`. Normal PR, merge-group, main, Fast, Slow and
image jobs download and verify the pinned release; they never compile Rust.
The required `native` job still gates `test`. Native maintenance is an explicit
`workflow_dispatch` operation on the existing `.github/workflows/ci.yml`.
One maintenance dispatch runs two independent clean builds per real architecture
and compares their library bytes before preparing an artifact for parent publication; they do not qualify Fast, publish images or upload a release.

Release: [`turso-native-v0.8.2-fts-contract1-aegis-wal1-20261029`](https://github.com/TerraScale/terrascale/releases/tag/turso-native-v0.8.2-fts-contract1-aegis-wal1-20261029), which adds the WAL revision contract ([2026-10-29-wal-revision.md](2026-10-29-wal-revision.md)) to the 2026-10-23 build.
`SHA256SUMS` pins both qualified libraries; downloads and managed builds reject
missing or mismatched bytes. Never replace or append assets to encryption-only
`turso-native-v1` or overwrite the combined release.

The downloader and SHA pins select the qualified runtime-dispatched C AEGIS
library. The historical pure-Rust/softaes release remains immutable.

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
- `0002-select-runtime-aegis-c-backend.patch` changes sync/sdk-kit's SDK dependency
  to an explicit path with `default-features = false` and `features = ["encryption",
  "fts"]`. A top-level `--no-default-features` alone cannot disable the inherited
  SDK default. The unified graph must not contain `pure-rust-crypto` or AEGIS
  `pure-rust`; unused target dependencies can still include the softaes crate.
- Locked AEGIS 0.9.8 archive SHA-256:
  `78412fa53e6da95324e8902c3641b3ff32ab45258582ea997eb9169c68ffa219`.
  `0003-keep-aegis-c-runtime-portable.patch` removes global `-maes`, `-mcrypto`,
  `-mneon`, and host-specific tuning from its build script. Original build.rs SHA
  `6d8f7dfa155082559521a4ee9fa7190e8ea5853201969ce7cbc2c6a503d4f37a` is checked
  before patching the downloaded, checksum-verified crate. Crypto code is unchanged.
  The archive, patch and resulting build script accompany the release. Hardware
  functions retain upstream's local target pragmas; generic code retains fallback.
  The actual C compiler command and preprocessor audit must show `HAS_HW_AES`
  undefined. Linked symbols must retain the software and CPU-dispatched hardware
  implementations, and disassembly must contain AES-NI or ARMv8 AES instructions.
  This is build/dispatch evidence, not execution proof on a CPU without AES.
- Build `cargo +1.88.0 build --locked --package turso_sync_sdk_kit --features
  turso_sdk_kit/fts --profile lib-release`. The patched dependency preserves
  encryption and FTS while selecting the runtime-dispatched C backend. The sync library supplies core and sync C exports and is
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

## Runtime AEGIS qualification

[Maintenance run 40017375521](https://github.com/TerraScale/terrascale/actions/runs/40017375521)
built twice per real architecture at `a5ec485f741e2b5d36413f15dd53d6beaa01417a`.
Both independent builds produced identical library bytes and passed FTS and
contract1 smokes, compiler/fallback audits and hardware-path disassembly checks.

| Architecture | SHA-256 |
| --- | --- |
| linux-x64 | `7464a29f3fc9bce603a1e0881720b309a276455a778937d6a9ffc2ed680bd76d` |
| linux-arm64 | `5b20e109146c1c1665e73cea1db407d9ec7b1cc34271474490a402796bd8e697` |

The exact x64 library passed 2,548/2,548 Release TerraBase Fast tests, 27/27
driver tests and 27/27 executable-only single-file driver tests at that source
commit. Actual process mappings and extracted native bytes match the x64 pin.
TerraBase publishes as a single file without a native sidecar. Managed ARM64
Fast and execution on hardware without AES were not tested.

Matched native-only shutdown acceptance passed 18/18 cases per arm in three
alternating pairs with the unchanged literal12bb fixture. Encrypted median Stop
was 1,416.028 ms: 1.0324x matched main and 1.3353x prior main, within both 1.5x
limits. The [measured report](../../../benchmark/bench/2026-10-23-runtime-aegis.md)
records exact inputs, samples and limitations. Normal CI at the final pinned
feature head and production live verification remain separate gates.

## Historical qualification evidence (pure-Rust release)

[Run 39969762871](https://github.com/TerraScale/terrascale/actions/runs/39969762871)
and independent [run 39997989468](https://github.com/TerraScale/terrascale/actions/runs/39997989468)
passed on real x64 and ARM64 runners. Every assembled asset passes its manifest;
both runs produced identical library hashes:

| Architecture | SHA-256 |
| --- | --- |
| linux-x64 | `efd5916aa9ee1eeaa04784813b08e250912ae25d89f8a92499a216c2711f3e8b` |
| linux-arm64 | `1d4f3b552d11eadb703d6abc13cd8f5f1621c7504642d6f2b875226d88cb5eef` |

The exact candidate bytes passed 2,540/2,540 TerraBase Fast tests, 23/23 driver
tests, and 14/14 focused FTS HTTP tests on linux-x64 at `52ca8e1bd`. The isolated
single-file driver also passes 23/23 and its extracted Turso library matches the
pin. `ClassifyTursoNativeForSingleFile` restores native metadata lost through
project references; otherwise .NET 11 leaves a sidecar outside the bundle.
Normal TerraBase CI repeats the executable-only loading and extracted-hash check.
ARM64 native smoke proof does not imply ARM64 managed Fast qualification.

## Maintenance and publication

Dispatch only for changed native inputs, with the parent queue/build admission.
The existing workflow supports maintenance on a pushed feature branch before
merge. Maintenance uses a distinct aggregate check identity and cannot satisfy
the ordinary required `test` check. Future native inputs require a new release
tag and reviewed pins; the published tag above is immutable.

1. Push the reviewed pipeline head, then dispatch **once**:

   ```sh
   gh workflow run ci.yml --repo TerraScale/terrascale --ref feat/full-text-search -F native_artifacts=true
   ```

   `native-build` uses two separate `ubuntu-24.04` jobs and two **real**
   `ubuntu-24.04-arm` jobs. Each invokes one uncached build; there is no retry loop. The helper
   rejects a host/target mismatch; no qemu/cross-architecture publication is
   accepted. Each build checks ELF64 little-endian and e_machine x64 `3e00` /
   arm64 `b700`, rejects RPATH/RUNPATH and absolute dynamic dependencies, and runs
   the full FTS and contract1 smokes before producing any output. Normal CI never
   substitutes an unqualified library. Parent owns the push, dispatch, queue gate
   and check watching.

2. After the maintenance run succeeds, download its assembled artifact:

   ```sh
   gh run download "$NATIVE_RUN_ID" --repo TerraScale/terrascale --name turso-native-release --dir artifacts/fts-contract1-release
   (cd artifacts/fts-contract1-release && sha256sum --check SHA256SUMS)
   ```

   Review both receipts, ABI exports, dynamic dependencies and smoke logs.
   Assets include the verified source archives, Cargo.lock, all exact patches,
   build recipe/helper, C smoke sources, upstream LICENSE/NOTICE and per-arch
   dependency-license archives with package manifests/checksum receipts. All
   release files are covered by `SHA256SUMS`; `NATIVE-SHA256SUMS` contains just the
   two published library names and real hashes.

3. Require the dispatch’s two independent builds per architecture to produce
   byte-identical libraries. Assembly verifies all four manifests and compares
   each library pair before preparing assets. Keep the run URL and all four
   job receipts with the publication evidence; ordinary downstream PRs always reuse the release.

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
   gh release create turso-native-v0.8.2-fts-contract1-runtime-aegis-20261023 \
     --repo TerraScale/terrascale --target "$PIPELINE_SHA" \
     --title 'Turso 0.8.2 FTS + contract1 + runtime AEGIS' \
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

Batch 5 advances `Engine/Turso/StorageFormat.cs` from main's version 12 to 13;
the deploy lane owns the coordinated reset. Native publication does not
qualify final feature-head CI, complete benchmarks or production activation.
Admission estimates are conservative, not strict RSS bounds, and cancellation
does not establish hard deadlines inside third-party calls.
