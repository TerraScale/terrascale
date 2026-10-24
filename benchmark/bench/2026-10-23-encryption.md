# Local database encryption comparison (TS-861)

## Shutdown regression: matched baseline and one-Stop profile

**The original-workload performance gate passed with the combined correction**
reported below. The original 48-document (~43 MB) workload requires all 18
executions to pass with median `StopAsync` at most 1.5 times matched main.
The prior baseline median is 1,060.487 ms, giving a separate historical
1,590.731 ms comparison ceiling. Split multi-read durability and deadline
coverage remains a separate gate; its clean build initially failed analyzers.

Three alternating matched pairs used the same six-case fixture and native
library. Main `7b80cacbe` carried only the diagnostic fixture patch; encryption
`12bb92320` was clean. All observations, including failures, are retained.

| Original workload | Stop samples ms | Median ms | Passed / failed / skipped |
| --- | --- | ---: | --- |
| Fixture-patched main | 1104.992 / 793.136 / 1060.487 | 1060.487 | 18 / 0 / 0 |
| Encryption before correction | 5745.727 / 5041.421 / 3079.548 | 5041.421 | 16 / 2 / 0 |

This is a real encryption-branch regression. It is a whole-branch comparison
under competing host workloads, rather than an isolated cipher benchmark.
Both arms loaded native SHA-256
`efd5916aa9ee1eeaa04784813b08e250912ae25d89f8a92499a216c2711f3e8b`.
The complete baseline, fixture identity and evidence hashes are in
[the matched comparison](2026-10-23-encryption-shutdown.md).

A subsequent **single profiled original Stop** on a dirty, instrumented
`12bb92320` build took **2,859 ms** and passed all six class cases. It retained
the original four-second deadline and parallelism 16. This sample locates
costs; it is neither an after-fix result nor stability qualification.

| Target-tenant phase | Wall ms | Thread CPU ms |
| --- | ---: | ---: |
| Early reader/command disposal, including native statement finalize | 1403.9 | about 1400 |
| Retention DELETE (two calls) | 804.3 | 280.7 |
| Native reader stepping | 272.9 | 261.4 |
| Destination writes / log serialization / blob copies | 27.8 / 24.1 / 15.1 | not separately measured |
| Queues, writer semaphore and resumption combined | at most 6 | not separately measured |

The tap reads at most 4 MiB per page from a SELECT limited to 1,024 rows. Native
statement finalization runs a partially consumed SELECT to completion. The
first read's eight disposal intervals were 386 / 260 / 227 / 175 / 169 / 84 /
60 / 46 ms; they shrink as the unread tail shrinks. This identifies redundant
execution during early statement close as the dominant measured cost. No key
file reads, unwraps or connection opens occurred inside Stop. The retained
writer session was reused for both cleanup calls.

The DELETE spent about 523 ms off CPU, with only 0.85 ms of scheduler runqueue
wait. No syscall trace isolated fsync, and no native stack sample isolated
cipher cost. Native `run_io` was not called: synchronous step and finalize
include their I/O and decryption. S3 completion and about 250 ms of other gaps
were not separately timed.

Static disassembly of the actual pinned library independently confirms that
its AEGIS-256 path calls software AES. The SDK defaults enable
`pure-rust-crypto`; locked AEGIS 0.9.8 chooses hardware AES only when the build
enables the AES target feature, which the release flags omit. This confirms
the selected implementation; the profile does not assign a measured fraction
of Stop to that implementation. Changes to the shared native artifact remain
with its existing owner.

Profile evidence:
`~/.t3/test-artifacts/database-encryption/stop-profile-12bb-20261023T193019Z/`.
The dirty instrumentation patch SHA-256 is
`9ea02cc3b6dc1910007f4c5eab723fa539851978631426d89207bd639b9c8f88`;
`report.md` SHA-256 is
`5f8fb0fa4a9a318126a3672fa753b1ae3fdc5237a2f158e099f947626b9503e9`.
One Release build and one six-case invocation passed; SDK, sibling and native
identities match the retained baseline. No failed run was discarded.

## Managed Abort correction with old native: failed, stopped early

The opt-in reader reset correction at
`89311f5d2f629a9a091a8334cf446b969d1d95bc` was measured against the same
fixture-patched main `7b80cacbe`, using the original physical-backlog fixture
in both arms and the unchanged `efd5916a…3e8b` software-AES native library.
The original four-second budget, workload, assertions and parallelism 16
remained unchanged.

| Completed invocation | Stop ms | Passed / failed / skipped | Runner exit |
| --- | ---: | --- | ---: |
| Pair 1 main | 905.723 | 6 / 0 / 0 | 0 |
| Pair 1 correction | 4328.039 | 5 / 1 / 0 | 2 |
| Pair 2 correction | 1521.069 | 6 / 0 / 0 | 0 |

**The correction alone did not qualify.** In the failed Stop, the first read
took 608.115 ms; the second took 3584.695 ms and threw
`OperationCanceledException`, leaving one segment and an unread backlog.
The first failed pair already violated the all-pass requirement. On explicit
stop instruction, pair 2 main was interrupted and all remaining pairs, clean
split-test builds/repeats and vendor-reset tests were left unrun. There is no
complete three-pair median or five-repeat proof from this attempt.

The two Release builds passed once each. The first admission had stopped on
a harness status-parser defect before any build or test; that failure and the
second-admission deviation are preserved separately. The stopped wrapper exit
was 137; no aggregate exit was produced. Host loads were high and uneven.
Completed results and interruption errors remain separate in the evidence.

Partial evidence:
`~/.t3/test-artifacts/database-encryption/fix-gate-harness-20261023T201055Z/supervision-partial/`.
`PARTIAL-REPORT.md` SHA-256:
`3885d806b342589c686b684350ba94b5c6bf5d588997dd198d72774c6bc6475d`.
The Abort correction is retained. A full combined correction plus the corrected
runtime-dispatched native comparison remains pending; it must meet the original
18/18 and median Stop at most 1.5 times matched-main acceptance bar.

## Combined Abort and runtime-dispatched AEGIS: performance gate passed

Exactly three alternating pairs measured literal managed correction
`89311f5d2f629a9a091a8334cf446b969d1d95bc` against fixture-patched main
`7b80cacbeb683a3501a2c5f0d40a7c618f453c6f`. Both arms used the original
physical-backlog fixture from `12bb92320`, SHA-256
`af5611a4a7ef757ac18f16ca9203b8142dda7b514415afec9dd71306ab3783fe`.
The split fixture was not used for these measurements. The original workload,
four-second deadline, assertions and parallelism 16 were unchanged.

| Arm | Stop samples ms, chronological within arm | Median ms | Passed / failed / skipped |
| --- | --- | ---: | --- |
| Fixture-patched main | 1843.863 / 1855.891 / 912.903 | 1843.863 | 18 / 0 / 0 |
| Abort correction + runtime AEGIS | 3291.651 / 1296.128 / 546.144 | 1296.128 | 18 / 0 / 0 |

All six runner exits were zero, with no retries or discarded samples. Median
Stop was **0.702942 times matched main** and **1.222201 times prior main**,
passing both 1.5-times comparisons. Matched main itself was 1.738695 times its
prior median. Shared-host load and the broad sample ranges limit throughput
claims; the result demonstrates the specified acceptance bar, not an encryption
speedup. The earlier encryption median was 5,041.421 ms with two failures;
the combined correction measured 1,296.128 ms with all 18 passing.

Every actual loaded native mapping in all six runs matched linux-x64 SHA-256
`7464a29f3fc9bce603a1e0881720b309a276455a778937d6a9ffc2ed680bd76d`
from `turso-native-v0.8.2-fts-contract1-runtime-aegis-20261023`. The candidate
was staged into each disposable arm's root and RID native outputs after its
one Release build; this measurement is separate from ordinary-target pin
consumption. SDK and sibling matched the earlier comparison.

The software-AES native was a material contributor to the encryption regression.
K03 separately measured the unchanged `12bb92320` managed code with only the
corrected runtime-dispatched native: main/encryption medians were
1,371.589 / 1,416.028 ms, ratio 1.0324, with 18/18 passing per arm on
`mariogk-dev`. Those owner-reported results and identity receipts accompany
[the published release](https://github.com/TerraScale/terrascale/releases/tag/turso-native-v0.8.2-fts-contract1-runtime-aegis-20261023).
The combined run does not isolate the additional benefit of Abort; the earlier
phase profile independently identified its redundant-finalization target.

Performance evidence:
`~/.t3/test-artifacts/database-encryption/combined89311-aegis-acceptance-20261023T205356Z/`.
`report.md` SHA-256:
`93032b8ef0b432099edca025fa603f29820f8fe7d7581426bd273930be336c8a`.
All six fresh original JSON/HTML reports, commands, counts, Stop events,
assembly identities, loaded mappings and host loads were retained and the
87-entry manifest independently rehashed and fsynced.

The same harness's subsequent clean split-fixture build failed CA2016,
IDE0305 (two locations) and CA1001. Its aggregate and wrapper exits were 1;
no split/vendor tests ran from that build. Fixture-only analyzer corrections
are recorded separately at `6627ea72d`; five split repeats and the vendor
regression proof remain pending. The successful performance gate is not a
claim that the entire harness passed.

## Shared FTS/encryption native measurement — pushed commit 1c3b609f6

Measured 2026-10-23 on `delta`, exact pushed commit `1c3b609f65588ed712b41a81dfb1d807b00f44a4`, Release, SDK `11.0.100-rc.1.26425.128` through `/home/mariogk/.local/share/mise/dotnet-root/dotnet`; runtime `11.0.0`. SDK sibling: `bcd20603f11f3aa54dc54cec6e410b4fe9e24151`. The admitted disposable checkout and sibling were clean at completion.

Shared release: `turso-native-v0.8.2-fts-contract1-20261023`. Pinned linux-x64 SHA-256: `efd5916aa9ee1eeaa04784813b08e250912ae25d89f8a92499a216c2711f3e8b`; pinned linux-arm64 SHA-256: `1d4f3b552d11eadb703d6abc13cd8f5f1621c7504642d6f2b875226d88cb5eef`. Normal MSBuild targets downloaded/verified this release; no native rebuild or release action occurred.

The actual benchmark library `/home/mariogk/remote-builds/terrascale/feat-database-encryption-7e16f5f66283f2812bd2abf77ced0226c970da80c976738eb28283c0931884bb/benchmark/dotnet/DatabaseEncryption/bin/Release/net11.0/libturso_sdk_kit.so` matched the x64 pin. The benchmark assembly contains the exact commit; SHA-256 `63058d4c4e0a9662b19fa1f628b2273975f7d8c57dcca0b47786d08f4e4e6cd4`.

One benchmark process supplied all three pairs, without extra warmup/repetition. UTC `2026-10-23T18:18:42.062748+00:00` to `2026-10-23T18:18:48.424849+00:00`; wall 6.362 s; exit 0. Host load averages (1/5/15 minute) were `[10.509765625, 12.66162109375, 13.18310546875]` at start and `[9.908203125, 12.5009765625, 13.1279296875]` at end. Every mode completed all 10,000 payload checks, truncating checkpoint validation and reopened row-count verification before its row was printed.

Actual execution order, milliseconds:

| Repeat | Cipher | Write ms | Read ms | Checkpoint ms | Reopen ms | DB bytes | WAL bytes before checkpoint |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | plain | 273.558 | 198.431 | 35.747 | 1.015 | 14086144 | 22618832 |
| 1 | aegis256 | 962.771 | 244.932 | 34.504 | 3.535 | 14090240 | 22622952 |
| 2 | aegis256 | 1373.187 | 202.265 | 350.707 | 3.848 | 14090240 | 22622952 |
| 2 | plain | 162.614 | 127.997 | 19.427 | 1.207 | 14086144 | 22618832 |
| 3 | plain | 163.387 | 129.418 | 33.361 | 1.017 | 14086144 | 22618832 |
| 3 | aegis256 | 952.571 | 196.221 | 36.192 | 2.724 | 14090240 | 22622952 |

Three-sample medians; difference is encrypted minus plain, percent is `(encrypted / plain - 1) * 100`:

| Operation | Plain median ms | aegis256 median ms | Difference ms | Difference % |
| --- | ---: | ---: | ---: | ---: |
| Write with commit | 163.387 | 962.771 | +799.384 | +489.26% |
| 10,000 point reads | 129.418 | 202.265 | +72.847 | +56.29% |
| Truncating checkpoint | 33.361 | 36.192 | +2.831 | +8.49% |
| Reopen and count | 1.017 | 3.535 | +2.518 | +247.59% |

Observed ranges (milliseconds):

| Operation | Plain min–max | aegis256 min–max |
| --- | ---: | ---: |
| Write with commit | 162.614–273.558 | 952.571–1373.187 |
| 10,000 point reads | 127.997–198.431 | 196.221–244.932 |
| Truncating checkpoint | 19.427–35.747 | 34.504–350.707 |
| Reopen and count | 1.015–1.207 | 2.724–3.848 |

Every encrypted database is 4,096 bytes larger than its plain counterpart; its pre-checkpoint WAL is 4,120 bytes larger in all three pairs.

These are three warm-filesystem-cache samples per mode on a shared host, including scheduling/storage contention. They establish the displayed observations only. They cannot establish stable production overhead, isolate the cost of FTS/encryption changes from host variation, or prove a performance regression/speedup against the retained historical runs. The workload and key handling below remain unchanged.

### Focused managed qualification on the same unified bytes

**172 passed, 0 failed, 0 skipped**, one invocation per requested filter; normal TerraBase parallelism 16, auth 4 and vendor runner default. All focused builds passed once with 0 warnings/errors. Exact source SHA was verified in built server, test and benchmark assemblies; every output native library matched the x64 pin.

| Filter | Passed/total | Runner duration | Exit |
| --- | ---: | --- | ---: |
| EncryptionTests | 16/16 | 277ms | 0 |
| DatabaseEncryptionTests | 6/6 | 2s 672ms | 0 |
| DatabaseRecoveryTests | 89/89 | 11s 924ms | 0 |
| CopySourceFaultTests | 4/4 | 4s 263ms | 0 |
| RebuildLedgerRecoveryTests | 12/12 | 2s 133ms | 0 |
| RebuildCertificationLedgerSafetyTests | 26/26 | 6s 075ms | 0 |
| RebuildFaultSafetyTests | 13/13 | 4s 388ms | 0 |
| StorageRecoveryTests | 4/4 | 29s 331ms | 0 |
| QosAdmissionTests (memory-refusal bools only) | 2/2 | 1s 600ms | 0 |

Vendor coverage retains both genuine native page-tamper cases and all SQL CHECK/RAISE spoof assertions. SQL RAISE reports typed raw constraint 129 / facade 1; genuine encrypted-page damage reports raw 133 / facade 11. QoS ran only `Process_memory_refusal_precedes_cold_plan_or_membership_materialization` both bools with the existing `RequestLogMemoryFixture` and drain. No assertion, timeout or parallelism was weakened.

### x64 single-file and isolated native-pin qualification

Normal `nice -n 19 make terrabase-publish RUNTIME=linux-x64` passed with the project’s single-file/native extraction properties. Published output had no Turso sidecar. The owned published test child reached HTTP health 200; nonempty SYSTEM/intents databases had encrypted headers and valid wrapped-only key records. Its extracted and mapped native library matched the new x64 pin and exported contract marker 1. The synthetic test master was delivered only through the child environment, never argv, logs or a master-key file. The child was stopped and temporary data/extraction removed.

Dedicated disposable native cache: clean target exit 0; one-byte XOR tampering rejected with the expected digest-mismatch error (exit 1); exact bytes restored and accepted (exit 0). The real cache was verified unchanged. No native build, ARM managed execution, production host, format activation, CI or merge claim is made.

### Reproduction and retained evidence

Admitted launch (the launcher pins PATH/DOTNET_ROOT to SDK 11, exports the SDK sibling and verifies the exact pushed SHA inside the managed checkout):

```sh
bash /home/mariogk/.t3/test-artifacts/database-encryption/unified-native-1c3b-20261023T181523Z/launch.sh
```

Benchmark subprocess, executed once through the external Makefile:

```sh
nice -n 19 dotnet run --configuration Release --project benchmark/dotnet/DatabaseEncryption --no-build
```

Evidence outside Git: `/home/mariogk/.t3/test-artifacts/database-encryption/unified-native-1c3b-20261023T181523Z`. `results.json` retains exact commands, exits, durations and start/end loads; original per-filter TUnit JSON/HTML, raw benchmark output, assembly/native digests, bundle proof and clean/corrupt/restored pin receipts are retained. Completed files and directories were fsynced. Overall qualification aggregate/wrapper exit 0. Admission reported beta unreachable and skipped its supersede check; execution occurred on delta. This is focused local proof, not a full-suite or CI result.

## Retained history — earlier encryption-only runs

The rest of this document preserves earlier measurements and qualification of the old encryption-only artifact. Those numbers and proofs do not describe the shared native bytes measured above.

## Earlier encryption-only patched-native measurement — local commit fc27c2186

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

## Historical encryption-only focused regression proof

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

## Historical encryption-only single-file and pin proof

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
