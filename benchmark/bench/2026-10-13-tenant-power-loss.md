# Tenant WAL power-loss proof — 2026-10-13

Status: 3 of 3 repeat runs passed at the current head `d69c4eba` on `main` `d875729d` (12 of 12 test executions, 0 skipped) within the fault model below. No local `make check` ran at this head (user decision: the PR CI is the required gate). The earlier passing sets at `ff44f7ef` on `a96ccbac` and `4ee2158d` on `a537727b`, and their `make check` results, are historical. See Required gates.

## Environment

| Field | Value |
| --- | --- |
| Date | 2026-10-13–14 UTC (latest repeats crossed midnight) |
| Test commit | `d69c4eba23037bbeada83ac929b762c0286ba9e7` (current passing three-run set; earlier attempts below) |
| Base | `d875729d1f6d50f31b1d87150708766cd939cf5e` (branch rebase base on `main`) |
| Machine | `delta`, Intel Core i7-13620H, 16 logical CPUs, 31 GiB RAM |
| OS | Linux 7.2.10, x86_64 |
| SDK | .NET `11.0.100-rc.1.26425.128` |
| Native storage | `Turso.Data.Native` 0.8.2, vendored Turso driver |
| External SDK checkout | ts-dotnet `bcd20603f11f3aa54dc54cec6e410b4fe9e24151` |

## Reproduction

Run this command three times from the repository root. `ts-heavy` supplies the shared heavy-run slot and `nice -n 19`.

```sh
TsDotnetRoot=/home/mariogk/Projects/TerraScale/ts-dotnet \
  ~/.local/bin/ts-heavy make terrabase-e2e \
  TEST_ARGS='--treenode-filter /*/*/TenantDatabasePowerLossTests/* --minimum-expected-tests 4' \
  E2E_RESULTS=/tmp/b3-sdkpin-run-1.jsonl
```

Change the output suffix to 2 and 3 for subsequent runs. The outcome JSONL records the raw B3 counts and WAL byte lengths in each test's `detail`.

## Fault model

Each seeded case creates an isolated tenant through the real management HTTP API, writes 12–24 documents through the real document listener, and verifies that every acknowledgement has a new successful sync of that tenant's WAL. The shim intercepts the next tenant commit's sync and kills the process before it runs. The test requires an unsynced transaction with at least two frames and exactly one final commit frame.

Power loss first drops all unsynced WAL bytes. The test then restores only complete non-commit frames and a torn final commit frame (1 byte, 12 bytes, or 2048 bytes). Reopening must recover every acknowledged document with its exact body and omit the interrupted document. Every earlier acknowledged document and a new acknowledged write must survive a second power loss without publishing the interrupted transaction.

The FULL-barrier case primes three NORMAL transactions through a native connection inside the same node process, holds that connection open, and then writes a document through HTTP. The test requires the NORMAL rows to be absent from a main-file-only snapshot and unsynced in the shim ledger before the HTTP write. The FULL write must sync beyond their frames. After power loss, all three NORMAL rows and the acknowledged HTTP document must exist.

Both cases require unchanged main-database bytes during the relevant write window. A checkpoint therefore invalidates the scenario rather than silently supplying recovery data. No production configuration or storage code is changed.

This is evidence for the existing WAL fault-shim model on Turso 0.8.2, not a hardware power-cut experiment. It does not simulate unsynced main-database pages after checkpoints, directory persistence, arbitrary sector corruption, or remotely durable acknowledgement.

## Raw results

The initial fixture attempt at `37e5530752a32b1e934e75a07866aecc4a840bb0` ran at 20:39 UTC: 4 tests, 0 passed, 4 failed, 0 skipped. Three failed before writing tenant documents because a one-node member directory cannot discover the second placement region. The barrier case recovered all three NORMAL rows, then its restarted test host repeated `CREATE TABLE` and closed the HTTP connection before the document read completed. No acknowledged document was observed missing or changed; this attempt does not establish the requested proof.

Fixture corrections: explicitly name `sjc` in the test database placement and consume the NORMAL-prime marker before restarting the host. The stopped database's NORMAL rows are read from a copy, leaving the HTTP recovery path untouched. Raw local evidence: `/tmp/b3-fixture-first.log`, `/tmp/b3-fixture-first.jsonl`.

Second fixture attempt at `919f5ab16a53e714084488efdc00661e1b16d376` (machine `delta`, wrapper start 2026-10-13T20:51:19Z, end 20:55:42Z, run 1 of 3; the wrapper stopped after this run): 4 tests, 0 passed, 4 failed, 0 skipped. All four failed at `CreateTenantAsync`, before writing any tenant document: `POST /v1/management/commits` answered 400 `invalid_request` ("the body is not a valid request", op 2). Cause: the fixture names the field `replicaRegions`, but commit ops take snake_case `replica_regions` (as `tests/TerraBase.E2E/Cluster.cs:719` and `tests/TerraBase.Fast/Management/CommitApiTests.cs:41` use). This is a fixture failure. No document was acknowledged, so none could be lost or changed, and no B3 counts or WAL byte lengths were produced. Raw local evidence (preserved copy of that run's `/tmp/b3-run-1.*`): `/tmp/b3-fixture-second.log`, `/tmp/b3-fixture-second.jsonl`. Runs 2 and 3 did not run.

Fixture correction: commit ops use `replica_regions`.

Third attempt at `d564fbeb2e085a6285f383761a92d9c10a4bfcf9` (base `df043b49`, machine `delta`, wrapper start 2026-10-13T20:58:12Z, end 21:05:04Z, run 1 of 3; the wrapper stopped after this run): 4 tests, 1 passed, 3 failed, 0 skipped.

| Test | Outcome | Raw detail |
| --- | --- | --- |
| `A_FULL_tenant_commit_syncs_earlier_NORMAL_frames_as_well_as_its_own_document` | pass | normal_commits=3 normal_recovered=3 full_acknowledged=1 full_recovered=1 before_sync_bytes=98912 normal_bytes=111272 full_sync_bytes=152472 |
| `A_random_cut_…(181, 1)` | fail | first post-restart read: GET answered 503 `catalog_unverified` |
| `A_random_cut_…(13013, 12)` | fail | first post-restart read: GET answered 503 `catalog_unverified` |
| `A_random_cut_…(802, 2048)` | fail | first post-restart read: GET answered 503 `catalog_unverified` |

All three random-cut failures are at `TenantDatabasePowerLossTests.cs:138`, the first `Docs.ReadAsync` of an acknowledged document after `node.StartAgainAsync()`. The node answered 503 `catalog_unverified` ("this machine's catalog is not verified against the other machines yet"): `UseTerraBaseCatalogGate` (`src/TerraBase/Features/Replication/CatalogGate.cs:638`) refuses authorized requests until the restarted machine's restore gate opens, and `Docs.AdmittedAsync` (`tests/TerraBase.E2E/Docs.cs:141`) retries only 429. This is a fixture readiness failure. The node refused to serve; no acknowledged document was read back missing or changed. The pre-restart assertions (per-acknowledgement WAL syncs, SIGKILL before the interrupted sync, frame layout of the torn tail) passed, but those cases emit no counts until they finish, so no random-cut counts were produced. Raw local evidence: `/tmp/b3-fixture-third.log`, `/tmp/b3-fixture-third.jsonl` (copies of `/tmp/b3-run-1.*`).

Third-attempt aggregate (one barrier run): NORMAL rows 3 primed, 3 recovered; FULL acknowledgements 1, recovered 1. Random-cut counts: none in that attempt.

The failed fixture assumed a member directory containing only the node itself could verify its existing catalog. With no trusted peer and no anchor, that gate remains closed; waiting cannot fix it. Both cases now use the standalone host already exercised by the passing barrier. That host supplies no member directory and uses the production catalog gate's existing standalone path; it does not install a fake gate. No peer can supply or repair tenant data in this proof.

Fourth attempt (passing set before the rebase; historical) at `4ee2158dfe9f8b6345d920f064ad027e8998a37e` (base `a537727b`, machine `delta`). One `ts-heavy` slot ran `/tmp/b3-proof-runs.sh`, which calls the reproduction command three times and stops at the first failure. Wrapper start 2026-10-13T21:12:23Z; JSONL written at 21:25:49Z, 21:26:10Z and 21:26:30Z. Each run: 4 total, 4 succeeded, 0 failed, 0 skipped.

| Run | Test | Outcome | Ack | Recovered | Interrupted | Discarded | Post-recovery ack / recovered | Synced WAL bytes | Unsynced bytes | Retained bytes | Torn bytes |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | random cut (181, 1) | pass | 15 | 15 | 1 | 1 | 1 / 1 | 1133032 | 70040 | 65921 | 1 |
| 1 | random cut (13013, 12) | pass | 12 | 12 | 1 | 1 | 1 / 1 | 708672 | 57680 | 53572 | 12 |
| 1 | random cut (802, 2048) | pass | 21 | 21 | 1 | 1 | 1 / 1 | 1203072 | 49440 | 47368 | 2048 |
| 2 | random cut (181, 1) | pass | 15 | 15 | 1 | 1 | 1 / 1 | 1133032 | 70040 | 65921 | 1 |
| 2 | random cut (13013, 12) | pass | 12 | 12 | 1 | 1 | 1 / 1 | 708672 | 57680 | 53572 | 12 |
| 2 | random cut (802, 2048) | pass | 21 | 21 | 1 | 1 | 1 / 1 | 1203072 | 49440 | 47368 | 2048 |
| 3 | random cut (181, 1) | pass | 15 | 15 | 1 | 1 | 1 / 1 | 1133032 | 70040 | 65921 | 1 |
| 3 | random cut (13013, 12) | pass | 12 | 12 | 1 | 1 | 1 / 1 | 708672 | 57680 | 53572 | 12 |
| 3 | random cut (802, 2048) | pass | 21 | 21 | 1 | 1 | 1 / 1 | 1194832 | 57680 | 55608 | 2048 |

| Run | Test | Outcome | NORMAL commits | NORMAL recovered | FULL ack | FULL recovered | WAL bytes before sync | After NORMAL | FULL sync |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | FULL barrier | pass | 3 | 3 | 1 | 1 | 98912 | 111272 | 152472 |
| 2 | FULL barrier | pass | 3 | 3 | 1 | 1 | 98912 | 111272 | 152472 |
| 3 | FULL barrier | pass | 3 | 3 | 1 | 1 | 98912 | 111272 | 152472 |

Aggregates over the three runs:

- Random cut: 144 acknowledged documents before power loss (48 per run: 15 + 12 + 21), 144 recovered with exact bodies, 0 missing or changed. 9 interrupted transactions, 9 discarded. 9 post-recovery acknowledged writes, 9 recovered after the second power loss, together with every earlier acknowledged document.
- FULL barrier: 9 NORMAL rows primed, 9 recovered; 3 FULL acknowledgements, 3 recovered. In every run the FULL sync (152472 bytes) covered the NORMAL frames (ending at 111272 bytes).
- Runs 1 and 2 produced byte-identical JSONL. In run 3 the seed-802 case synced 8240 fewer bytes before its cut and had a larger unsynced tail (57680 bytes); its counts are the same.

Raw local evidence: `/tmp/b3-run-{1,2,3}.log`, `/tmp/b3-run-{1,2,3}.jsonl` (SHA-256 run 1 and 2 `58ceb496…edce61`, run 3 `a02f6978…be954`).

### Rebased validation at `ff44f7ef` (historical)

The branch was rebased onto `main` `a96ccbac035470816aee8cffe40fce3abfe291ba`, which adds native reader `Abort` cleanup and the PITR benchmark race fix. The test is unchanged. Validated head: `ff44f7ef9fc2f53dba9904635bb25d3e0679e5f4` (machine `delta`, ts-dotnet `bcd20603`, `TsDotnetRoot=/home/mariogk/Projects/TerraScale/ts-dotnet`). One `ts-heavy` slot ran `/tmp/b3-rebased-runs.sh`, which calls the reproduction command three times (`E2E_RESULTS=/tmp/b3-rebased-run-N.jsonl`) and stops at the first failure. Queued at 2026-10-13T21:50:05Z waiting for a slot. Run 1 22:16:23Z–22:17:03Z (includes `terrabase-build`), run 2 22:17:03Z–22:17:21Z, run 3 22:17:21Z–22:17:35Z. Wrapper exit 0. Each run: 4 total, 4 succeeded, 0 failed, 0 skipped.

| Run | Test | Outcome | Ack | Recovered | Interrupted | Discarded | Post-recovery ack / recovered | Synced WAL bytes | Unsynced bytes | Retained bytes | Torn bytes |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | random cut (181, 1) | pass | 15 | 15 | 1 | 1 | 1 / 1 | 1133032 | 70040 | 65921 | 1 |
| 1 | random cut (13013, 12) | pass | 12 | 12 | 1 | 1 | 1 / 1 | 708672 | 57680 | 53572 | 12 |
| 1 | random cut (802, 2048) | pass | 21 | 21 | 1 | 1 | 1 / 1 | 1203072 | 49440 | 47368 | 2048 |
| 2 | random cut (181, 1) | pass | 15 | 15 | 1 | 1 | 1 / 1 | 1133032 | 70040 | 65921 | 1 |
| 2 | random cut (13013, 12) | pass | 12 | 12 | 1 | 1 | 1 / 1 | 708672 | 57680 | 53572 | 12 |
| 2 | random cut (802, 2048) | pass | 21 | 21 | 1 | 1 | 1 / 1 | 1194832 | 57680 | 55608 | 2048 |
| 3 | random cut (181, 1) | pass | 15 | 15 | 1 | 1 | 1 / 1 | 1133032 | 70040 | 65921 | 1 |
| 3 | random cut (13013, 12) | pass | 12 | 12 | 1 | 1 | 1 / 1 | 708672 | 57680 | 53572 | 12 |
| 3 | random cut (802, 2048) | pass | 21 | 21 | 1 | 1 | 1 / 1 | 1203072 | 49440 | 47368 | 2048 |

| Run | Test | Outcome | NORMAL commits | NORMAL recovered | FULL ack | FULL recovered | WAL bytes before sync | After NORMAL | FULL sync |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | FULL barrier | pass | 3 | 3 | 1 | 1 | 98912 | 111272 | 152472 |
| 2 | FULL barrier | pass | 3 | 3 | 1 | 1 | 98912 | 111272 | 152472 |
| 3 | FULL barrier | pass | 3 | 3 | 1 | 1 | 98912 | 111272 | 152472 |

Aggregates over the three rebased runs:

- Random cut: 144 acknowledged documents before power loss (48 per run: 15 + 12 + 21), 144 recovered with exact bodies, 0 missing or changed. 9 interrupted transactions, 9 discarded. 9 post-recovery acknowledged writes, 9 recovered after the second power loss, together with every earlier acknowledged document.
- FULL barrier: 9 NORMAL rows primed, 9 recovered; 3 FULL acknowledgements, 3 recovered. In every run the FULL sync (152472 bytes) covered the NORMAL frames (ending at 111272 bytes).
- Runs 1 and 3 produced JSONL byte-identical to the pre-rebase run 1 (SHA-256 `58ceb496…edce61`). Run 2 (`a881b797…50bc2cd`) holds the same four records as pre-rebase run 3 in a different line order: seed 802 synced 8240 fewer bytes before its cut and had a larger unsynced tail (57680 bytes); its counts are the same.
- Counts above were read from the JSONL `detail` fields; every record has category `pass` (12 of 12) and each log reports `skipped: 0`.

Combined with the pre-rebase set: 6 of 6 runs passed, 288 of 288 acknowledged documents recovered, 18 of 18 interrupted transactions discarded, 18 of 18 post-recovery documents recovered, 18 of 18 NORMAL rows and 6 of 6 FULL documents recovered.

Raw local evidence: `/tmp/b3-rebased-run-{1,2,3}.log`, `/tmp/b3-rebased-run-{1,2,3}.jsonl`, with per-run `.start`/`.end` UTC stamps.

### SDK-pin rebased validation (current)

The branch was rebased onto `main` `d875729d1f6d50f31b1d87150708766cd939cf5e`, which adds persisted feed sequences and derived shape storage, monotonic local origin stamps, stored-digest binding to collection and id, independent Slow CI, and the shared Docker SDK pin to the locked RC1 toolchain (`global.json` `rollForward: disable`). The test is unchanged. Validated head: `d69c4eba23037bbeada83ac929b762c0286ba9e7` (machine `delta`, SDK `11.0.100-rc.1.26425.128`, Turso 0.8.2, ts-dotnet `bcd20603`, `TsDotnetRoot` and `TS_DOTNET_DIR` set to `/home/mariogk/Projects/TerraScale/ts-dotnet`). `/tmp/b3-sdkpin-runs.sh` calls the reproduction command through `ts-heavy` three times (`E2E_RESULTS=/tmp/b3-sdkpin-run-N.jsonl`) and stops at the first failure. Run 1 2026-10-13T23:40:57Z–2026-10-14T00:12:11Z (includes waiting for a slot and `terrabase-build`, 0 warnings, 0 errors), run 2 00:12:11Z–00:12:26Z, run 3 00:12:26Z–00:12:45Z. Each run exited 0: 4 total, 4 succeeded, 0 failed, 0 skipped.

| Run | Test | Outcome | Ack | Recovered | Interrupted | Discarded | Post-recovery ack / recovered | Synced WAL bytes | Unsynced bytes | Retained bytes | Torn bytes |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | random cut (181, 1) | pass | 15 | 15 | 1 | 1 | 1 / 1 | 1252512 | 78280 | 74161 | 1 |
| 1 | random cut (13013, 12) | pass | 12 | 12 | 1 | 1 | 1 / 1 | 803432 | 65920 | 61812 | 12 |
| 1 | random cut (802, 2048) | pass | 21 | 21 | 1 | 1 | 1 / 1 | 1380232 | 57680 | 55608 | 2048 |
| 2 | random cut (181, 1) | pass | 15 | 15 | 1 | 1 | 1 / 1 | 1252512 | 78280 | 74161 | 1 |
| 2 | random cut (13013, 12) | pass | 12 | 12 | 1 | 1 | 1 / 1 | 803432 | 65920 | 61812 | 12 |
| 2 | random cut (802, 2048) | pass | 21 | 21 | 1 | 1 | 1 / 1 | 1363752 | 74160 | 72088 | 2048 |
| 3 | random cut (181, 1) | pass | 15 | 15 | 1 | 1 | 1 / 1 | 1252512 | 78280 | 74161 | 1 |
| 3 | random cut (13013, 12) | pass | 12 | 12 | 1 | 1 | 1 / 1 | 803432 | 65920 | 61812 | 12 |
| 3 | random cut (802, 2048) | pass | 21 | 21 | 1 | 1 | 1 / 1 | 1363752 | 74160 | 72088 | 2048 |

| Run | Test | Outcome | NORMAL commits | NORMAL recovered | FULL ack | FULL recovered | WAL bytes before sync | After NORMAL | FULL sync |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | FULL barrier | pass | 3 | 3 | 1 | 1 | 94792 | 107152 | 156592 |
| 2 | FULL barrier | pass | 3 | 3 | 1 | 1 | 94792 | 107152 | 156592 |
| 3 | FULL barrier | pass | 3 | 3 | 1 | 1 | 94792 | 107152 | 156592 |

Aggregates over the three SDK-pin runs:

- Random cut: 144 acknowledged documents before power loss (48 per run: 15 + 12 + 21), 144 recovered with exact bodies, 0 missing or changed. 9 interrupted transactions, 9 discarded. 9 post-recovery acknowledged writes, 9 recovered after the second power loss, together with every earlier acknowledged document.
- FULL barrier: 9 NORMAL rows primed, 9 recovered; 3 FULL acknowledgements, 3 recovered. In every run the FULL sync (156592 bytes) covered the NORMAL frames (ending at 107152 bytes).
- WAL byte lengths differ from the earlier sets on every case; the counts are the same. In run 1 the seed-802 case synced 16480 more bytes before its cut than in runs 2 and 3 and had a smaller unsynced tail (57680 bytes); its counts are the same. JSONL SHA-256: run 1 `d120e3f6…e063f`, run 2 `57829539…1e5bd`, run 3 `c59d8766…bc495` (runs 2 and 3 hold the same records in a different line order).
- Counts above were read from the JSONL `detail` fields; every record has category `pass` (12 of 12) and each log reports `skipped: 0`.

Combined with both earlier sets: 9 of 9 runs passed, 432 of 432 acknowledged documents recovered, 27 of 27 interrupted transactions discarded, 27 of 27 post-recovery documents recovered, 27 of 27 NORMAL rows and 9 of 9 FULL documents recovered.

Raw local evidence: `/tmp/b3-sdkpin-run-{1,2,3}.log`, `/tmp/b3-sdkpin-run-{1,2,3}.jsonl`, with per-run `.start`/`.end` UTC stamps and `.exit` (`0`); `/tmp/b3-sdkpin-runs.sh`; `/tmp/b3-sdkpin-head.txt` (`d69c4eba`).

## Required gates

### Current head `d69c4eba`

No local `make check` ran at `d69c4eba`. The user directed that the PR's CI is the required gate once the B3 proof passes, so no full local suite was queued. No local required-check pass is claimed at this head. The checks below ran at earlier heads and are historical.

### Rebased check retry at `ff44f7ef` (historical, passing)

The `main` test gate is open (user decision after `df043b49`). Deployment is tracked separately.

Local `make check` at `ff44f7ef9fc2f53dba9904635bb25d3e0679e5f4` (machine `delta`, one `ts-heavy` slot with no slot override, `TsDotnetRoot` and `TS_DOTNET_DIR` set to `/home/mariogk/Projects/TerraScale/ts-dotnet`, ts-dotnet `bcd20603`). The wrapper refused to run unless `git rev-parse HEAD` was `ff44f7ef`. It ran from 2026-10-13T22:57:17Z to 23:11:41Z and exited 0. Whitespace formatting, the solution and area builds (0 warnings, 0 errors) and the analyzer build check passed. The deliberate analyzer-fixture build failed with the expected 20 errors (15 TB0001, 5 RS0030). The Go benchmark vet, build and `go test -race` passed.

| Suite | Total | Passed | Failed | Skipped |
| --- | --- | --- | --- | --- |
| TerraScale.Endpoints.Tests | 88 | 88 | 0 | 0 |
| TerraScale.Logging.Tests | 46 | 46 | 0 | 0 |
| TerraScale.Authentication.Fast | 668 | 668 | 0 | 0 |
| Turso.Data.Sqlite.Tests | 7 | 7 | 0 | 0 |
| TerraBase.Fast | 1595 | 1595 | 0 | 0 |

Total: 2404 of 2404 passed, 0 skipped. The rebase onto `main` added one vendored-driver test and two TerraBase Fast tests. The 17 tests that failed in the interrupted attempt below all passed here. Raw local evidence: `/tmp/b3-rebased-check-retry.log` and its `.start`, `.end` and `.exit` (`0`) files. Slow was not run.

### Rebased check, first attempt (interrupted)

The inherited local `make check` at `ff44f7ef9fc2f53dba9904635bb25d3e0679e5f4` (machine `delta`, external SDK `bcd20603f11f3aa54dc54cec6e410b4fe9e24151`) was queued at 2026-10-13T22:20:01Z through `~/.local/bin/ts-heavy`, with no slot override. Its `make` process started at 22:29:17Z at nice 19. At 22:50:35Z, TerraBase Fast was cancelled by SIGTERM. By 22:51:00Z, the outer shell, slot waiter, `make`, and test processes were all gone; no build/test process remained in this worktree. `/tmp/b3-rebased-check.exit` and `/tmp/b3-rebased-check.end` were not written. The command's numeric exit status and the sender of SIGTERM are unknown. No exit 0 or complete required-check pass is claimed. The replacement validation agent observed this existing run and started no build or test.

Completed gates before cancellation: whitespace/style formatting; solution and area builds (0 build warnings, 0 errors); NuGet audit (0 vulnerable packages); both auth OpenAPI documents validated (3 non-fatal OpenAPI warnings); the deliberate analyzer fixture produced exactly the expected 15 TB0001 and 5 RS0030 errors; handoff script tests (4 passed); Go vet, build and `go test -race` (the PITR benchmark package ran for 256.110 seconds; other package results were cached).

| Suite | Reported total | Passed | Failed | Skipped | Completion |
| --- | --- | --- | --- | --- | --- |
| TerraScale.Endpoints.Tests | 88 | 88 | 0 | 0 | complete |
| TerraScale.Logging.Tests | 46 | 46 | 0 | 0 | complete |
| TerraScale.Authentication.Fast | 668 | 668 | 0 | 0 | complete; 8m 28s 207ms |
| Turso.Data.Sqlite.Tests | 7 | 7 | 0 | 0 | complete; 57s 035ms |
| TerraBase.Fast | 1383 | 1366 | 17 | 0 | interrupted; 5m 59s 806ms |

TerraBase Fast's 1383 is the interrupted run's reported total, not a claim that the whole suite ran. Across the five summaries: 2192 reported, 2175 passed, 17 failed, 0 skipped. All 17 failure records are preserved in the TUnit JSON. Three failures completed before the cancellation:

| Test in `BackupShutdownTests` | Observed failure | Failure time (UTC) |
| --- | --- | --- |
| `Multiple_cached_customer_configuration_deadlines_cannot_precede_healthy_final_wal_and_restore` | HTTP 503 in `ManagementApi.CreateManagedDatabaseAsync`, while creating the organization; test call at `BackupShutdownTests.cs:104` | 22:45:06 |
| `A_failed_customer_upload_then_detachment_flushes_a_final_platform_snapshot_without_waiting_for_backoff` | HTTP 503 in the same setup path; test call at `BackupShutdownTests.cs:36` | 22:45:15 |
| `A_failing_tap_disposal_still_returns_every_other_tap_and_lets_the_engine_close` | test timed out after 00:02:00; declared at `BackupShutdownTests.cs:563` | 22:47:06 |

The remaining 14 failures ended at 22:50:35Z during cancellation: nine `OperationCanceledException`, four `AggregateException` records containing cancelled tasks, and one connection-refused `HttpRequestException`.

| Test | Observed failure during cancellation |
| --- | --- |
| `PeerStreamTests.Probe_r3_quiet_ranges_never_bridge_a_local_group` | AggregateException: cancelled tasks |
| `PeerStreamTests.A_slow_refill_does_not_hold_back_another_databases_batch_past_the_progress_timeout` | AggregateException: cancelled tasks |
| `PeerStreamTests.An_index_made_under_one_name_in_two_regions_while_apart_ends_as_the_same_definition_in_both` | AggregateException: cancelled tasks |
| `PeerStreamTests.A_copied_database_does_not_inherit_its_sources_peer_acks` | AggregateException: cancelled tasks |
| `PeerStreamTests.Many_databases_share_bounded_refill_admission_across_origins_while_healthy_batches_progress` | OperationCanceledException |
| `PeerStreamTests.A_refill_without_a_byte_reservation_is_refused_and_can_retry` | HttpRequestException: connection refused on loopback |
| `PeerStreamTests.A_refill_from_a_machine_that_does_not_hold_the_database_is_refused` | OperationCanceledException |
| `PeerStreamTests.Remembered_quiet_ranges_stay_bounded_over_many_retention_passes` | OperationCanceledException |
| `PeerStreamTests.A_peer_that_leaves_the_machine_list_loses_its_open_stream_until_it_is_listed_again` | OperationCanceledException |
| `PeerStreamTests.A_live_peer_is_not_told_to_refill_after_its_ack_rises_over_joined_rows` | OperationCanceledException |
| `PeerStreamTests.A_peer_silent_for_24_hours_stops_holding_change_groups_and_refills_when_it_returns` | OperationCanceledException |
| `PeerStreamTests.A_refill_brings_an_index_redefined_while_away_to_its_new_fields` | OperationCanceledException |
| `CorruptChangeLogTests.A_held_database_resumes_once_the_damaged_row_is_repaired` | OperationCanceledException |
| `UsageAnalyticsTests.Storage_sampling_covers_more_databases_than_a_minute_samples_whatever_order_they_list_in` | OperationCanceledException |

Raw local evidence: `/tmp/b3-rebased-check.log`, `/tmp/b3-rebased-check.start`, `/tmp/b3-rebased-check-terrabase-fast.json` (SHA-256 `40bb7916f9c9e71a939c95cf99e82f3bc83fae7ea2bae2e0bc9476283e941028`) and `/tmp/b3-rebased-check-terrabase-fast.html` (SHA-256 `1bfecf44d65338e604142b0b94344904099919638768b3c7209a8fd748a97a2b`). The JSON/HTML are preserved copies of the run's generated TUnit reports, including the seventeenth failure not printed as a named failure in the log. No rerun was started from this attempt. The retry above is the bounded retry at this same head, and this failed log is preserved.

### Pre-rebase check (historical)

Local `make check` at `4ee2158d` (machine `delta`, one `ts-heavy` slot, `TS_DOTNET_DIR=/home/mariogk/Projects/TerraScale/ts-dotnet`), 2026-10-13T21:26:38Z to 21:48:14Z: exit 0. Whitespace and style formatting, the solution build (0 warnings, 0 errors), and the analyzer build check all passed. That check expects the deliberate analyzer-fixture build to fail with 15 TB0001 and 5 RS0030 errors, and it did. The Go benchmark vet, build and `go test -race` passed.

| Suite | Total | Passed | Failed | Skipped |
| --- | --- | --- | --- | --- |
| TerraScale.Endpoints.Tests | 88 | 88 | 0 | 0 |
| TerraScale.Logging.Tests | 46 | 46 | 0 | 0 |
| TerraScale.Authentication.Fast | 668 | 668 | 0 | 0 |
| Turso.Data.Sqlite.Tests | 6 | 6 | 0 | 0 |
| TerraBase.Fast | 1593 | 1593 | 0 | 0 |

Raw local evidence: `/tmp/b3-check.log`. Slow was not run.
