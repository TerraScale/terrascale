# Encryption Fast qualification, 2026-10-23

These are focused development-host results, not a passing complete Fast gate.
The final unified FTS/encryption native bytes passed focused qualification at
`1c3b609f65588ed712b41a81dfb1d807b00f44a4`: 172 passed, zero failures/skips.
Single-file encrypted startup and clean/corrupt/restored cache checks passed.
Three benchmark pairs are published in `benchmark/bench/2026-10-23-encryption.md`.
The durable unified report SHA-256 is
`3d0bd5a3e0e7fb985a53454210d57333f4c06ef1b9ba1a82f078fbc5c8d8bcaa`.
Exact-head CI and live activation remain pending; the orch format hold remains.
The earlier focused runs below used the historical encryption-only native asset.
Execution used admitted `ts-heavy-any` on delta, `nice -n 19`, Release, SDK
`11.0.100-rc.1.26425.128`, and the existing Makefile parallelism: TerraBase 16,
auth 4. The SDK sibling was clean at
`bcd20603f11f3aa54dc54cec6e410b4fe9e24151`; native assets were `turso-native-v1`.

## Initial complete suites

At `042020beeb0b27b62c2d8e8e77985a52c457589b`, each complete Fast suite ran once:

| Suite | Passed | Failed | Skipped | Total |
| --- | ---: | ---: | ---: | ---: |
| TerraBase | 2463 | 8 | 1 | 2472 |
| Auth | 703 | 2 | 0 | 705 |
| Vendored driver | 38 | 0 | 0 | 38 |

The existing TerraBase skip is the #437 frontier fixture, owner R10 d83ac492,
deadline 2026-10-24T09:00Z. Neither its quarantine nor any assertions were weakened.
The outer launcher exit was misleadingly zero; suite exits and aggregate exit 2
identify this as a failed run.

## Repairs and focused proof

At `44ab258f881fb5995d68eb0fdddac8ba96b9584e`:

| Filter | Repetitions | Passed / total per repetition |
| --- | ---: | --- |
| QoS cold-plan/membership memory refusal | 5 | 2 / 2 |
| Certified GC baseline / ambiguous salvage | 5 | 2 / 2 |
| ShapeSeal damaged CDC variants | 5 | 4 / 4 |
| LiveRestoreDrillSafetyTests | 1 | 5 / 5 |
| DataDirectoryTests | 1 | 34 / 34 |

All listed cases had zero failures/skips. Fast and restore-drill builds passed.
QoS now excludes background convergence reads while retaining request refusal
and successful-recovery assertions. Certified GC waits for the receiver's cached
membership to exclude the removed source. Decoder errors remain contained during
shutdown while requested cancellation still propagates.

The CDC fixture first failed because it selected the first captured transaction
after a separately sampled head. It now selects the intended document's commit,
with an unrelated preceding transaction deliberately retained as a regression.
Background gauges inspect only one local group, so the fixture explicitly reads
the damaged batch through production code before checking durable refusal.

At `d8b9a9d8ab052849240f0f80bbd365637b8dc8a8`, the entire
`/*/*/CorruptChangeLogTests/*` filter passed five repetitions:

| Repetition | Passed / total | Runner seconds | Wall seconds | Exit |
| --- | --- | ---: | ---: | ---: |
| 1 | 19 / 19 | 44.909 | 47.730 | 0 |
| 2 | 19 / 19 | 32.738 | 34.385 | 0 |
| 3 | 19 / 19 | 30.833 | 34.229 | 0 |
| 4 | 19 / 19 | 29.461 | 32.227 | 0 |
| 5 | 19 / 19 | 48.116 | 49.749 | 0 |

Total 95 passed, zero failures/skips; aggregate exit 0. Build passed with zero
warnings/errors. All mutation variants and post-repair refusal assertions remain.

Three unchanged deadline controls passed individually at `44ab258f`: delayed
stable reset, refill certification, and remembered quiet-range retention. This
does not establish five-repeat stability or repair their earlier full-suite
timeouts.

## Auth bootstrap prerequisite

Before the bootstrap setup correction, collection-loss readiness repeats at
`44ab258f` returned 8/14 and 0/14 passes. An accidentally started third repetition
was interrupted: its partial four failures are not a complete-suite result.
Most completed-run failures occurred before collection deletion; the interrupted
run also reached post-drop failures. Host load reached approximately 75.

The test now awaits `SigningKeyRotation.Bootstrapped` before its unchanged
10-second readiness assertion. This matches an existing startup fixture and
establishes a real prerequisite. The 15-second post-drop assertion, missing
collection check, production two-second probe deadline and five-second interval
are unchanged.

At `42fa34f54f1d140c894a898c33e83f0524abad50`, all 14 collection-loss variants
passed five complete repetitions: 70 passed, zero failures/skips. Each command
returned exit 0. Runner durations were 92.468, 48.475, 63.643, 52.038 and
49.441 seconds. This validates the setup correction for this focused filter;
it does not isolate every earlier timeout stage or establish a complete Fast gate.

## Latest CI failure and focused correction proof

At `d8b9a9d8ab052849240f0f80bbd365637b8dc8a8`, CI run `39973624411` reported
2,480 TerraBase passes, one failure and one existing skip (2,482 total).
The aggregate `test` job inherited the TerraBase failure; libs and auth passed.
The sole failure was the retained-native-damage certification case at the
rebuild-publishing checkpoint: its native read returned without corruption.

The fixture overwrote an encrypted byte with `0xFF`, which can already be the
ciphertext value. Commit `0c98fec1ece51918e88ba6ff60384e8e30925769` instead reads
the existing byte, rejects EOF, flips bit `0x80`, and flushes the write. All
native damage and refusal assertions remain unchanged. Independent static
review found no introduced defect.

At that exact commit, all 26 `RebuildCertificationLedgerSafetyTests` variants
passed five complete repetitions: 130 passed, zero failures/skips, and every
command exited 0. Runner durations were 17.383, 6.697, 9.769, 12.432 and
6.597 seconds. The initial build exited nonzero without a compiler diagnostic;
its cause remains unproven and the failed attempt is retained. After artifact,
process and journal inspection, one authorized same-commit retry built with
zero warnings/errors and verified both assemblies' commit identity before tests.
This is focused proof; exact-head CI and unified-native compatibility remain
separate gates.

## Idle-pool fixture isolation

CI run `39982646431` at `93e23bd9656719ca785677bc1f9d87f23ffa8464` reported
2,508 TerraBase passes, one failure and zero skips. The idle-pool test expected
eight idle sessions but observed seven. A two-second catalog-convergence read
can rent a SYSTEM session from this shared pool during the exact-count check.
The interleaving exists on main; CI does not identify the actual renter and
encryption's timing contribution is unmeasured.

Commit `7708b9ba2c8daadf8e9674e47b6d81a09e4bcc43` freezes that fixture's host
clock and forwards the registered clock through its existing engine factory.
Pool caps, exact count, LRU, file-handle and reopening assertions are unchanged.
Independent review found no functional timeout regression in shared callers.

At that literal commit, five complete `StorageTransactionTests` repetitions
passed 19/19 each, with runner durations 13.947, 15.858, 39.296, 25.390 and
9.684 seconds. Shared-factory controls passed once: StorageBeginCancellation
2/2, StampingTransaction 6/6, StoragePool 9/9 and TenantLookupCache 9/9.
Total 121 passed, zero failures/skips; all nine commands and aggregate exited 0.
One Release build passed with zero warnings/errors. The subsequent QoS conflict
resolution preserves main's request-log drain and request-scoped read counting;
that union passed static review and awaits CI on the rebased head.

## Split shutdown proof and control cleanup

On mariogk-dev, literal `e62b9d8e57396b6b679388d894c8190d247eb905`
passed five complete seven-case `BackupBacklogRecoveryTests` repetitions:
35 passed, zero failures/skips. Runner exits were all 0; wall durations were
67.123, 44.654, 45.978, 35.274 and 39.068 seconds. The bounded raw-reader
abort regression also passed once (1/1). Both Release builds passed, with
SDK `11.0.100-rc.1.26425.128` and the owned clean ts-dotnet sibling
`bcd20603f11f3aa54dc54cec6e410b4fe9e24151`. Normal native downloads and
actual process mappings matched runtime-AEGIS x64 SHA-256
`7464a29f3fc9bce603a1e0881720b309a276455a778937d6a9ffc2ed680bd76d`.

The one-read shutdown control failed at the required delivered-read assertion
(expected two, received one). The deadline control exited nonzero, but host
teardown rethrew cleanup cancellation and masked its primary eight-second
watchdog exception. Its exact failure criterion remains unproved; the
Abort-to-Dispose control was not run. Overall qualification therefore exited
1 and remains incomplete despite the 36 clean passes. A fixture-only correction
preserves the primary failure and attaches host-disposal failure as secondary
evidence; successful test bodies still propagate disposal failures. Budgets,
assertions, workload and parallelism remain unchanged. This correction has
not yet been runtime qualified, nor does the earlier proof validate subsequent
rebases or the regional authentication fixture correction.

Original reports, commands, native maps, control diffs and root-cause analysis
are retained on delta and mariogk-dev under
`~/.t3/test-artifacts/database-encryption/quiet-e62-dev-20261023T211919Z/`.
All 167 manifested files were independently rehashed on both hosts. Manifest
SHA-256: `5da11fba6e3a1eeedee9c62be78e08c94c9db8be896fae5552a6ce3bd7285e13`.

## Retained evidence

Raw logs are outside Git under
`~/.t3/test-artifacts/database-encryption/`:

- `fullfast-20261023T133805Z/`: original complete suites.
- `repair-proof-20261023T144342Z/`: fixture proof and deadline controls;
  `results.json` SHA-256
  `1d5b857da80a904e43231bc4cfe7c93bf672324818a73b231b1fe8282176c2c2`.
- `repair-proof-cdc-d8b9-20261023T150211Z/`: corrected CDC proof;
  `report.md` SHA-256
  `ce2acb1d9fd33d903c64bf8dff73f4d7bfc3b9f20357b83c41d719360c857693`.
- `repair-proof-auth-42fa-20261023T150928Z/`: auth prerequisite proof;
  original per-repeat results and exit codes are retained in `results.json`
  (SHA-256 `e38cf31800d64fa8ff6fa1d0074ee371a2a4f00597441b75f2b9b0126826aa2f`).
  Recovered `report.md` SHA-256 is
  `8b9ce9f48bf1f5a3d42514c4ccc799dfa616c5076a300f5d6892e54f88c2bc9f`.
  Original logs 1–4 and the original managed TUnit JSON for repetition 5
  independently support the counts. Empty original wrapper/aggregate output
  remains preserved; aggregate 0 is calculated from individual exits rather
  than recovered wrapper output. No auth tests were rerun for this recovery.
- `repair-proof-ledger-0c98-20261023T152355Z/`: guaranteed mutation proof;
  `report.md` SHA-256
  `8f0a400d860ad55a2385317a5ba858a6279da9ae1eb88288fb2b0ffa71292c48`.
  Retry results and exits are under `authorized-retry/`; the original failed
  build and its nonzero exits are preserved separately.
- `repair-proof-pool-7708-20261023T162221Z/`: idle-pool isolation and shared
  factory controls; `report.md` SHA-256
  `6b5bef2dec313e95c35dc6785bd26aaedac695782db49ff26bb0c3db6226b5fb`.
  All nine original runner JSON/HTML reports and their hashes are retained.

Reports retain exact commands, commits, SDK/native identity, loads, timings,
per-command exits, and actual runner totals. Encryption cost measurements remain
in `benchmark/bench/2026-10-23-encryption.md`.
