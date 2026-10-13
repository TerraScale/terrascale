# Three-region replication matrix — merged T0 gate, 2026-10-13

The complete local gate passed after rebasing onto merged T0 main. This is loopback evidence; Fly qualification and its run URL remain pending T2 deployment and isolated e2e credentials.

- Tested source: `98003c3d274c710490cbbea6900084962ede0d4e`; base: main `de244e661a16785f5bbfe799a1a986ed37bf7740` (#206).
- SDK: `bcd20603f11f3aa54dc54cec6e410b4fe9e24151`, matching CI's pin. The earlier SDK mismatch is no longer an active blocker.
- Host: delta, Intel i7-13620H, 16 logical CPUs; .NET SDK `11.0.100-rc.1.26425.128`, Debug, server GC; `ts-heavy` / nice 19, default parallel TUnit execution. Other host work contributes to timings.
- No TerraBase production changes remain in this branch: the catalog fixes exposed by the matrix are in T0/main.

## Full gate and proof

The gate ran detached in its own process session:

```sh
setsid -f nohup ~/.local/bin/ts-heavy bash \
  /tmp/cos-replication-matrix-proof/main-green-gate.sh \
  > /tmp/cos-replication-matrix-proof/main-green-gate.log 2>&1 < /dev/null
```

The script exports `TsDotnetRoot=/home/mariogk/.t3/worktrees/terrascale/cos-replication-matrix-sdk` and runs `make check TS_DOTNET_DIR="$TsDotnetRoot"`, followed by `TERRABASE_E2E_RESULTS=/tmp/cos-replication-matrix-proof/main-green-matrix-outcomes.jsonl make terrabase-replication-matrix`, workflow lint and the e2e summary self-check. Gate exit: **0**.

| Check | Actual result |
| --- | --- |
| Formatting, solution build, analyzers, security/OpenAPI, Go race checks | Passed through full `make check` |
| Endpoints / logging | 88/88 / 46/46 passed |
| Auth Fast / Turso driver | 664/664 / 6/6 passed |
| TerraBase Fast | 1561/1561 passed; 0 failed; 2m 56.623s |
| Three-region matrix | 16/16 actual passes; 0 failed, skipped or expected failures; 17.489s |

`actionlint -shellcheck='' .github/workflows/ci.yml`, `scripts/terrabase/e2e-summary-test.sh` and `git diff --check` passed. Shellcheck integration, Slow, Docker execution and live Fly execution were not run locally; CI performs Docker qualification before merge.

```text
Test run summary: Passed!
  total: 16
  failed: 0
  succeeded: 16
  skipped: 0
  duration: 17s 489ms
```

## Client-observed convergence

Ten documents per write direction, from successful PUT acknowledgement to first matching target read; all three copies are verified. Timings include HTTP, 20 ms polling intervals and parallel host contention. Median averages the two middle samples; nearest-rank p95 is the maximum for ten samples. These numbers do not estimate WAN lag or internal transport time.

| Direction | Samples | Min (ms) | Median (ms) | p95 (ms) | Max (ms) |
| --- | ---: | ---: | ---: | ---: | ---: |
| gru → sjc | 10 | 30.8 | 58.9 | 95.9 | 95.9 |
| sjc → gru | 10 | 31.2 | 41.4 | 2047.9 | 2047.9 |
| gru → fra | 10 | 38.9 | 50.9 | 119.9 | 119.9 |
| fra → gru | 10 | 31.8 | 44.8 | 90.3 | 90.3 |
| sjc → fra | 10 | 26.4 | 29.6 | 5059.1 | 5059.1 |
| fra → sjc | 10 | 40.5 | 60.5 | 91.6 | 91.6 |

Each rejoin case keeps its stale data, accepts 20 new documents through each survivor, deletes two pre-outage documents and merges conflicting patches. After restart, no new client writes trigger catch-up. All documents, persisted tombstones, collection counts and digests are verified in every region. Catch-up is measured from restart completion until all 20 documents from the source are readable on the rejoined node.

| Source → rejoined region | Documents | Catch-up (ms) |
| --- | ---: | ---: |
| gru → sjc | 20 | 2850 |
| sjc → gru | 20 | 8017 |
| gru → fra | 20 | 2759 |
| fra → gru | 20 | 8493 |
| sjc → fra | 20 | 3094 |
| fra → sjc | 20 | 8530 |

## Raw evidence and live gate

Raw logs and per-test outcomes are retained on delta in `/tmp/cos-replication-matrix-proof/`:

| Log | SHA-256 |
| --- | --- |
| `main-green-make-check.log` | `5aec8e1a16da514ece5d623f1db54921028a9866642d03fa362d2d0bb8260eb6` |
| `main-green-matrix.log` | `2dcb479fbf8b90586f72f9f073a209cabacca732754b81dcd79a1467ced92bd3` |

Post-deploy CI selects 13 write/delete/conflict cases pinned with `fly-prefer-region`, verifies the serving region and requires 13 actual passes on Fly machines. The earlier local selection/discovery/pinning/missing-credential dry runs are preserved in [the preflight report](2026-10-13-replication-matrix-t0-preflight.md). Live outage qualification runs separately on the disposable app. T2 owns provisioning the isolated e2e project/database/key and GitHub secrets; the key must exclude platformDB access.

T0 is merged and the local gate is green. Live WAN convergence, live outage/rejoin, abrupt power loss, permanent origin loss, rebuild certification and incarnation fencing are not qualified by this local result.
