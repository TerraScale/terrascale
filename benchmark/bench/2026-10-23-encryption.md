# Local database encryption comparison (TS-861)

## Final patched-native measurement — local commit fc27c2186

Measured 2026-10-23 on `delta`, SDK `11.0.100-rc.1.26425.128`, runtime `11.0.0`, Release, exact local commit `fc27c2186a344cee1c85497b66981f2ad5a8c152` (not pushed during this verification). The admitted wrapper fetched an older remote branch; its command explicitly changed to the absolute local worktree before every build/test/benchmark. All three plain/aegis256 pairs completed with payload and reopened row-count verification.

Final native source is `5c168be5ef6a32ac966daf5983502a4f694b37ad` with patch SHA-256 `eadb14ec88ac351c938908443bcb89da324411e4eb8f8d068b3cc755fc0808ae`, Rust `1.88.0`, package `turso_sync_sdk_kit`, profile `release-official`, and patchelf `0.15.2` RUNPATH removal. Pinned final artifact digests:

- linux-x64: `a8e66163067435de33627e2231f1f409a78fb5e2ed1a10ade478a815d4ad702f`.
- linux-arm64: `c14fe3965580b762fd4f8d128a8b5043c393ad3e0ab98559875175917b8dccb4`.

The benchmark's actual `bin/Release/net11.0/libturso_sdk_kit.so` SHA-256 matched the x64 pin. Full native build/portability receipts are retained in `artifacts/turso-native/{linux-x64,linux-arm64}/`; original compiler outputs and private patched source trees were preserved. These local artifacts were not published.

Exact admitted validation command:

```sh
TS_HEAVY_HOST=delta /home/mariogk/.local/bin/ts-heavy-any nice -n 19 bash -c 'cd /home/mariogk/.t3/worktrees/terrascale/feat/database-encryption && bash artifacts/final-validation/run.sh' --repo terrascale --branch feat/database-encryption
```

The retained validation script checks the exact local commit, exports `TsDotnetRoot="$HOME/Projects/TerraScale/ts-dotnet"`, runs the focused tests, then executes this benchmark subprocess once (the program itself supplies three repeats):

```sh
cd /home/mariogk/.t3/worktrees/terrascale/feat/database-encryption
export TsDotnetRoot="$HOME/Projects/TerraScale/ts-dotnet"
dotnet run --configuration Release --project benchmark/dotnet/DatabaseEncryption
```

Final program output, in actual execution order; all times are milliseconds:

| Repeat | Cipher | Write ms | Read ms | Checkpoint ms | Reopen ms | DB bytes | WAL bytes before checkpoint |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | plain | 302.825 | 274.329 | 2.951 | 1.434 | 14086144 | 22618832 |
| 1 | aegis256 | 972.998 | 164.399 | 1.947 | 3.448 | 14090240 | 22622952 |
| 2 | aegis256 | 4065.919 | 199.250 | 1.784 | 2.048 | 14090240 | 22622952 |
| 2 | plain | 148.468 | 139.111 | 1.460 | 0.959 | 14086144 | 22618832 |
| 3 | plain | 139.782 | 155.245 | 1.525 | 1.007 | 14086144 | 22618832 |
| 3 | aegis256 | 278.891 | 169.298 | 1.388 | 1.869 | 14090240 | 22622952 |

Final medians, three samples per mode; percent is `(encrypted / plain - 1) * 100`:

| Operation | Plain median ms | aegis256 median ms | Observed difference |
| --- | ---: | ---: | ---: |
| Write with commit | 148.468 | 972.998 | +555.36% |
| 10,000 point reads | 155.245 | 169.298 | +9.05% |
| Truncating checkpoint | 1.525 | 1.784 | +16.98% |
| Reopen and count | 1.007 | 2.048 | +103.38% |

The measured median write difference is +824.530 ms; reopen adds 1.041 ms. Every encrypted database is 4,096 bytes larger and its pre-checkpoint WAL is 4,120 bytes larger. Encrypted writes range from 278.891 to 4,065.919 ms, while plain writes range from 139.782 to 302.825 ms. This uncontrolled shared-host, warm-cache run establishes these observations only: three samples with this variation cannot establish stable production overhead or a performance regression relative to the earlier run. No speedup is claimed. Workload, timing boundaries and key handling remain as documented below.

## Final focused regression proof

Same local commit and x64 native pin: **170 passed, 0 failed, 0 skipped**. Vendor encryption: 16; TerraBase classes: 89 DatabaseRecoveryTests, 6 DatabaseEncryptionTests, 4 CopySourceFaultTests, 12 RebuildLedgerRecoveryTests, 26 RebuildCertificationLedgerSafetyTests, 13 RebuildFaultSafetyTests; auth StorageRecoveryTests: 4. All relevant builds succeeded with zero warnings/errors. Vendor coverage includes both real native page-tamper paths, SQL CHECK spoofing, and all seven SQL RAISE spoof strings (including forged primary/extended damage codes).

Commands inside the admitted script:

```sh
dotnet build tests/Turso.Data.Sqlite.Tests/Turso.Data.Sqlite.Tests.csproj --configuration Release
dotnet run --project tests/Turso.Data.Sqlite.Tests --configuration Release --no-build -- --treenode-filter '/*/*/EncryptionTests/*' --minimum-expected-tests 16 --output detailed --no-ansi
dotnet build tests/TerraBase.Fast/TerraBase.Fast.csproj --configuration Release
for class in DatabaseRecoveryTests DatabaseEncryptionTests CopySourceFaultTests RebuildLedgerRecoveryTests RebuildCertificationLedgerSafetyTests RebuildFaultSafetyTests; do
  dotnet run --project tests/TerraBase.Fast --configuration Release --no-build -- --maximum-parallel-tests 16 --treenode-filter "/*/*/$class/*" --minimum-expected-tests 1 --output detailed --no-ansi
done
make auth-test-fast CONFIGURATION=Release 'TEST_ARGS=--treenode-filter /*/*/StorageRecoveryTests/* --minimum-expected-tests 4 --output detailed --no-ansi'
```

SDK sibling commit: `bcd20603f11f3aa54dc54cec6e410b4fe9e24151`. Logs are retained in `artifacts/final-validation/`. This is focused local verification, not a full-suite or CI result. No branch push or CI rerun was performed.

## Final single-file and pin proof

`make terrabase-publish RUNTIME=linux-x64 PUBLISH_DIR="$PWD/artifacts/final-validation/publish"` succeeded with the normal project single-file/native extraction properties and `--self-contained`. Published output contained no `libturso` sidecar. The isolated published child reached HTTP health 200 with all listener ports assigned from 0. Ambient `TS_`, `TERRABASE_`, `AWS_`, bucket and region settings were cleared; Development used only the test harness SHA-256 synthetic master in the child environment, never in argv or a master-key file.

The child created nonempty encrypted `_system.db` and `_intents.db`, each with a valid wrapped-only `.key` record. The library extracted from the bundle matched x64 SHA-256 `a8e66163067435de33627e2231f1f409a78fb5e2ed1a10ade478a815d4ad702f`, was present in the published child's memory mappings, and exported contract1. The owned child was stopped and its temporary data/extraction directory cleaned up.

A dedicated copy of the cached native artifact passed `VerifyTerraScaleTursoNative`, then failed the same target after a single byte was flipped, with the expected reviewed SHA-256 mismatch. The copy was restored in `finally` to the exact original bytes and accepted again; the real cache and pins were unchanged.

Exact admitted proof command:

```sh
TS_HEAVY_HOST=delta /home/mariogk/.local/bin/ts-heavy-any nice -n 19 bash -c 'set -euo pipefail; cd /home/mariogk/.t3/worktrees/terrascale/feat/database-encryption; export TsDotnetRoot="$HOME/Projects/TerraScale/ts-dotnet"; test "$(git rev-parse HEAD)" = fc27c2186a344cee1c85497b66981f2ad5a8c152; python3 artifacts/final-validation/bundle-and-pin-proof.py' --repo terrascale --branch feat/database-encryption
```

Retained non-secret proof: `artifacts/final-validation/bundle-proof.json`, `pin-proof.json`, and clean/corrupt/restored target logs. The proof script's target invocation retained the reviewed digest and changed only `_TerraScaleTursoRoot` to the dedicated temporary cache:

```sh
dotnet msbuild src/vendor/Turso.Data.Sqlite/src/Turso.Raw/Turso.Raw.csproj -t:VerifyTerraScaleTursoNative -p:TargetFramework=net11.0 -p:RuntimeIdentifier=linux-x64 -p:_TerraScaleTursoRoot=<dedicated-temp-cache>/ -nologo
```

## Earlier measurement — retained history

Measured 2026-10-23 on dev host `delta`, .NET SDK `11.0.100-rc.1.26425.128`, runtime `11.0.0`, commit `7d0d378d66d0ab3e657739e6d8e90af75eb77656` (Release). All three plain/aegis256 pairs completed, including payload and reopen row-count verification. This shared host was running other admitted jobs; timings include scheduling and storage contention. These are warm-filesystem-cache microbenchmark results, not production or cold-cache guarantees.

Exact admitted command (no keys in arguments):

```sh
TS_HEAVY_HOST=delta /home/mariogk/.local/bin/ts-heavy-any nice -n 19 bash -c 'set -euo pipefail; hostname; dotnet --version; git rev-parse HEAD; dotnet run --configuration Release --project benchmark/dotnet/DatabaseEncryption; dotnet build tests/Turso.Data.Sqlite.Tests/Turso.Data.Sqlite.Tests.csproj --configuration Release; dotnet run --project tests/Turso.Data.Sqlite.Tests --configuration Release --no-build -- --treenode-filter "/*/*/EncryptionTests/*" --minimum-expected-tests 6 --output detailed --no-ansi' --repo terrascale --branch feat/database-encryption
```

## Every measurement

Times are milliseconds. Rows below preserve program output and execution order.

| Repeat | Cipher | Write ms | Read ms | Checkpoint ms | Reopen ms | DB bytes | WAL bytes before checkpoint |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | plain | 1121.381 | 524.250 | 39.664 | 2.266 | 14086144 | 22618832 |
| 1 | aegis256 | 1156.546 | 536.100 | 31.128 | 4.303 | 14090240 | 22622952 |
| 2 | aegis256 | 2614.680 | 382.236 | 174.080 | 14.248 | 14090240 | 22622952 |
| 2 | plain | 1378.560 | 575.339 | 257.911 | 4.698 | 14086144 | 22618832 |
| 3 | plain | 1038.305 | 551.218 | 180.625 | 4.245 | 14086144 | 22618832 |
| 3 | aegis256 | 1021.262 | 487.453 | 21.179 | 9.475 | 14090240 | 22622952 |

## Observed overhead

Percent difference is `(encrypted median / plain median - 1) * 100`, calculated from the displayed milliseconds. Each median has only three samples.

| Operation | Plain median ms | aegis256 median ms | Observed difference |
| --- | ---: | ---: | ---: |
| Write with commit | 1121.381 | 1156.546 | +3.14% |
| 10,000 point reads | 551.218 | 487.453 | -11.57% |
| Truncating checkpoint | 180.625 | 31.128 | -82.77% |
| Reopen and count | 4.245 | 9.475 | +123.20% |

The encrypted database is 4,096 bytes larger and its pre-checkpoint WAL is 4,120 bytes larger in every repeat. Reopen adds 5.230 ms at the median. Write times vary from 1,021.262 to 2,614.680 ms for encryption; checkpoint times vary from 21.179 to 257.911 ms across both modes. Lower encrypted read/checkpoint medians are observations under contention, not evidence that encryption accelerates those operations. This run cannot establish a stable production overhead percentage.

## Workload and key handling

Each repeat creates separate plain and aegis256 files with pooling disabled for both. Order alternates. Both store the same 10,000 ULID-shaped IDs and 1 KiB payload, use prepared statements, WAL, synchronous FULL, disabled automatic checkpointing, one write transaction, and 10,000 verified point reads. Timings include writes with commit, point reads, explicit truncating checkpoint, and reopen with row-count verification. File size and pre-checkpoint WAL size are bytes.

Each encrypted file gets a fresh process-memory random key. Keys are never printed or written separately; temporary database files are deleted in finally. The benchmark measures local driver encryption, not IMasterKey wrapping, OCI Vault delivery, production key rotation, or end-to-end TerraBase backup/recovery.

## Earlier vendor regression result

Historical focused rerun on `delta`, SDK `11.0.100-rc.1.26425.128`, commit `f6a967d39b2620d4b2cb5dc8d0647280404e6043`: **6 passed, 0 failed, 0 skipped**, duration 316 ms, runner exit code 0. Vendor regression build succeeded with zero warnings/errors (1.87 seconds).

Historical rerun command:

```sh
TS_HEAVY_HOST=delta /home/mariogk/.local/bin/ts-heavy-any nice -n 19 bash -c 'set -euo pipefail; hostname; dotnet --version; git rev-parse HEAD; dotnet build tests/Turso.Data.Sqlite.Tests/Turso.Data.Sqlite.Tests.csproj --configuration Release; dotnet run --project tests/Turso.Data.Sqlite.Tests --configuration Release --no-build -- --treenode-filter "/*/*/EncryptionTests/*" --minimum-expected-tests 6 --output detailed --no-ansi' --repo terrascale --branch feat/database-encryption
```

All cases passed: builder option preservation; all three malformed/incomplete option cases; raw encrypted backup creation with facade readback; encrypted database and live-WAL copies requiring the original key (including raw-reader interoperability and missing/wrong-key rejection).

The original measured run passed five cases and failed `RawConnectionCanCreateEncryptedBackup`: its combined raw `CREATE TABLE; INSERT` setup executed only the first statement. The test now issues those statements separately, preserving its payload assertion; the final focused rerun above verifies the fix. Benchmark measurements were retained because this change affects test setup only. A preceding rerun attempt on `mariogk-dev` failed Git fetch with `Could not resolve hostname github.com` (exit 128), before any build/test; the successful retry used `delta` through the same admission wrapper.

Earlier attempts produced no measurements: commit `02c3dbb5f521c5d56efdc9e23daef3fc8807a6d4` failed vendor CS8604; `3f862dd02940830899e5639661e01784966d6618` failed benchmark IDE0011/IDE0300. Parent pushed the vendor guard; benchmark/test braces and collection syntax were fixed before the measured run. No production engine suite or CI result is claimed here.
