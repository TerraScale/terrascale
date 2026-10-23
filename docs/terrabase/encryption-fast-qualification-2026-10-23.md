# Encryption Fast qualification, 2026-10-23

These are focused development-host results, not a passing complete Fast gate.
Final unified FTS/encryption native delivery and live activation remain pending.
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

## Latest CI failure and pending correction

At `d8b9a9d8ab052849240f0f80bbd365637b8dc8a8`, CI run `39973624411` reported
2,480 TerraBase passes, one failure and one existing skip (2,482 total).
The aggregate `test` job inherited the TerraBase failure; libs and auth passed.
The sole failure was the retained-native-damage certification case at the
rebuild-publishing checkpoint: its native read returned without corruption.

The fixture overwrote an encrypted byte with `0xFF`, which can already be the
ciphertext value. Commit `0c98fec1ece51918e88ba6ff60384e8e30925769` instead reads
the existing byte, rejects EOF, flips bit `0x80`, and flushes the write. All
native damage and refusal assertions remain unchanged. Independent static
review found no introduced defect; five-repeat runtime proof remains pending.
Guaranteed mutation alone does not establish that this explains the CI failure.

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
  per-repeat results and exit codes are retained in `results.json`.

Reports retain exact commands, commits, SDK/native identity, loads, timings,
per-command exits, and actual runner totals. Encryption cost measurements remain
in `benchmark/bench/2026-10-23-encryption.md`.
