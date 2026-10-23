# Three-region replication matrix — local proof, 2026-10-13

- Implementation commit: `98a265ab05bc973616e369f9bb358b9f936a284a`; based on main `29e9da2f61653cf986ccc1a19c8440bfe447150c`.
- SDK dependency: isolated ts-dotnet checkout `78e311fe1dcb6dd603190800ed540d08e06de5fd`. CI currently pins `bcd20603f11f3aa54dc54cec6e410b4fe9e24151`; main-green T0 owns that mismatch.
- Host: delta, Intel i7-13620H, 16 logical CPUs; .NET SDK `11.0.100-rc.1.26425.128`, Debug, server GC. Three in-process real application nodes per case, ephemeral ports and separate data directories. Default parallel TUnit execution under `ts-heavy` / nice 19.
- Result: 16 actual passes, 0 failures, 0 skips, 0 expected failures; test duration 17.449 seconds.
- This is local loopback proof. No WAN run or live run URL exists for this candidate yet; live qualification waits for COS T2.

## Command and output

```sh
TsDotnetRoot=/home/mariogk/.t3/worktrees/terrascale/cos-replication-matrix-sdk \
TERRABASE_E2E_RESULTS=/tmp/cos-replication-matrix-proof/local-outcomes-4.jsonl \
~/.local/bin/ts-heavy make terrabase-replication-matrix
```

```text
Test run summary: Passed!
  total: 16
  failed: 0
  succeeded: 16
  skipped: 0
  duration: 17s 449ms
```

## Write convergence by direction

Ten distinct documents per direction. Each sample starts after the successful PUT response and ends at the first matching read in the target region; the other two copies are also verified before the next sample. Timings include HTTP, polling (20 ms between misses), scheduler and parallel-test contention. They do not measure internal shipping time. Median is the mean of the two middle samples; p95 is nearest rank (the maximum for ten samples).

| Direction | Samples | Min (ms) | Median (ms) | p95 (ms) | Max (ms) |
| --- | ---: | ---: | ---: | ---: | ---: |
| fra → gru | 10 | 27.0 | 30.0 | 3204.9 | 3204.9 |
| fra → sjc | 10 | 10.8 | 30.1 | 55.9 | 55.9 |
| gru → fra | 10 | 28.4 | 31.5 | 975.8 | 975.8 |
| gru → sjc | 10 | 28.9 | 35.0 | 955.2 | 955.2 |
| sjc → fra | 10 | 29.0 | 31.2 | 198.2 | 198.2 |
| sjc → gru | 10 | 29.2 | 31.2 | 3060.6 | 3060.6 |

## Region restart catch-up

Each of the three outage cases stops its own node cleanly, retains its data, accepts 20 new documents through each survivor, deletes one old document through each survivor, and patches a shared conflict. After restart, no additional client writes trigger replication. All 40 documents, both tombstones and the merged conflict are checked in every region, including the full collection count and digest. These are client-observed times from restart completion until the target returns all 20 matching documents from that source.

| Source → rejoined region | Documents | Catch-up (ms) |
| --- | ---: | ---: |
| fra → gru | 20 | 8753 |
| fra → sjc | 20 | 4824 |
| gru → fra | 20 | 8264 |
| gru → sjc | 20 | 8161 |
| sjc → fra | 20 | 8590 |
| sjc → gru | 20 | 8530 |

## Regressions exposed

1. Catalog writes entered the client-only changes feed and failed with `no such table: _tb_feed_floor`. The catalog now stays out of that feed; storage schemas are unchanged.
2. Replication stability operations opened the system catalog as a tenant database and failed canonical-ULID validation. Those operations now use the existing system/client dispatch helper.

The initial analyzer build failure and both pre-fix 16/16 failing runs are uncredited. The passing run still logged unobserved socket-copy cancellation exceptions from the existing TCP proxy; no pass is inferred from those logs. This run proves graceful local stop/rejoin, not abrupt power-loss durability, permanent origin loss, rebuild certification or incarnation fencing.

Raw local log: `/tmp/cos-replication-matrix-proof/local-matrix-4.log`; SHA-256 `d52f1e93bf5c47af4674f858c2e978926713e30d86a4e6de8800b6fe65f919b8`. Per-test outcomes: `/tmp/cos-replication-matrix-proof/local-outcomes-4.jsonl`. Both are preserved on delta until the COS resumes live qualification.

## Required-check gate

`TsDotnetRoot=/home/mariogk/.t3/worktrees/terrascale/cos-replication-matrix-sdk ~/.local/bin/ts-heavy make check TS_DOTNET_DIR=/home/mariogk/.t3/worktrees/terrascale/cos-replication-matrix-sdk` exited 2. Formatting, the solution build, library checks, auth security/OpenAPI checks, TerraBase analyzer checks and Go race checks passed. Endpoints tests passed 88/88; logging tests passed 46/46. Authentication Fast passed 667/670 and failed these three existing tests:

| Test | Failure |
| --- | --- |
| `A_new_project_jwks_must_not_create_unbounded_keys_while_its_local_copy_lags` | `RegionalReviewTests.cs:136`: expected one key, found zero |
| `Only_exact_discovery_paths_can_select_local_storage` | `RegionalReviewTests.cs:44`: expected the original signing key ID, received another |
| `Dump_served_client_routes` | `ScratchPr5RegionalE2ETests.cs:60`: null output-file path |

Those auth files are unchanged by this branch. `make check` stopped before running TerraBase Fast and the Turso driver tests. Slow was not run. Log: `/tmp/cos-replication-matrix-proof/make-check.log`.

Workflow structure passed `actionlint -shellcheck='' .github/workflows/ci.yml`; the shellcheck shim has no selected version, so its integration was not run. No Docker execution or live cloud execution was performed locally.

The branch is committed locally and unpushed. No PR exists yet: repository rules require `make check` to pass before pushing. Publication and merge wait on **COS: terrascale main green (T0)**; live six-direction and disposable-app outage qualification wait on production qualification. The CI wiring above is implemented but has not run in GitHub yet.
