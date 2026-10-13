# Three-region replication matrix — T0 preflight, 2026-10-13

The local matrix passes on the T0 scratch integration. Full required checks remain blocked by five TerraBase Fast failures. No live Fly result or run URL exists yet.

## Candidate and environment

- Main-based, unpushed PR candidate: `d01a905e8a2886fbc17ffd23299d8b398a85e551`, based on main `29e9da2f61653cf986ccc1a19c8440bfe447150c`.
- Full-check scratch: `15097a4107a15395af52f7c8ede869392acd554a`, rebased onto T0 `ae4bac417bb48cd1fec36ab3ac18622b9d2bdd05`.
- Final matrix scratch: `060507f2acc83403d122515833df0b05707a3cfd`, including the final CI changes and rebased onto newer T0 `6506446444795d86d59d8d2c897f2dfa4625241a`. Both rebases completed without conflicts. T0 now includes both catalog fixes the original matrix exposed; neither production file differs from newer T0.
- Host: delta; .NET SDK `11.0.100-rc.1.26425.128`, Debug, server GC, default parallel TUnit execution under `ts-heavy` / nice 19. Concurrent host work makes these loopback timings unsuitable as WAN estimates or performance comparisons.
- Isolated SDK: `78e311fe1dcb6dd603190800ed540d08e06de5fd`. CI still pins `bcd20603f11f3aa54dc54cec6e410b4fe9e24151`; resolving the known auth region-API mismatch remains with T0.

## Full required checks

From `/home/mariogk/.t3/worktrees/terrascale/cos-replication-matrix-t0-scratch` at `15097a41`:

```sh
TsDotnetRoot=/home/mariogk/.t3/worktrees/terrascale/cos-replication-matrix-sdk \
~/.local/bin/ts-heavy make check \
  TS_DOTNET_DIR=/home/mariogk/.t3/worktrees/terrascale/cos-replication-matrix-sdk
```

Exit status: **2**. Formatting, solution build, library checks, security/OpenAPI checks, analyzer checks and Go race checks passed. Endpoints: 88/88; logging: 46/46; auth Fast: 664/664; Turso driver: 6/6. TerraBase Fast: **1553 passed, 5 failed, 0 skipped**, 1558 total, 2m 35.371s. These Fast files are unchanged by the matrix branch.

| Failed case | Assertion and location |
| --- | --- |
| `Review_probe_a_cursor_taken_on_a_refilled_machine_never_skips_the_repaired_event_on_another_region(True)` | Cursor after `p`, including the pre-repair cursor, resumes on gru with HTTP 200 but omits repaired `e`; `DatabaseChangesTests.cs:499` |
| Same cursor-refill case `(False)` | Cursor after `p` resumes on gru with HTTP 200 but omits repaired `e`; `DatabaseChangesTests.cs:499` |
| `Many_databases_share_bounded_refill_admission_across_origins_while_healthy_batches_progress` | Expected ACK `Stable=0`, received `Stable=Applied`; `PeerStreamTests.cs:1038` |
| `A_tombstone_waits_for_every_live_peer_and_is_collected_everywhere_once_they_hold_it` | Expected retained tombstone state, found none; `PeerStreamTests.cs:1623` |
| `A_peer_that_leaves_the_machine_list_loses_its_open_stream_until_it_is_listed_again` | Departed peer returns the second document instead of null; `PeerStreamTests.cs:302` |

The failures and log were sent to the COS parent immediately. No assertion was removed, relaxed or reclassified. This is not a green required-check result or a merge-readiness claim.

## Final scratch matrix

From the scratch worktree at `060507f2`:

```sh
TsDotnetRoot=/home/mariogk/.t3/worktrees/terrascale/cos-replication-matrix-sdk \
TERRABASE_E2E_RESULTS=/tmp/cos-replication-matrix-proof/t0-scratch-matrix-outcomes.jsonl \
~/.local/bin/ts-heavy make terrabase-replication-matrix
```

```text
Test run summary: Passed!
  total: 16
  failed: 0
  succeeded: 16
  skipped: 0
  duration: 18s 791ms
```

Every direction checks the written document in all three regions; delete cases inspect persisted tombstones locally. The conflicting-write case requires every writer's independent field before accepting identical state. Each rejoin case retains its data, accepts writes/deletes/conflicts through both survivors, restarts without further client writes, and verifies documents, tombstones, collection counts and digests.

### Client-observed write convergence

Ten samples per direction, from successful PUT acknowledgement to the first matching target read. Timings include HTTP, 20 ms polling intervals and host contention. Median averages the two middle samples; nearest-rank p95 is the maximum for ten samples.

| Direction | Samples | Min (ms) | Median (ms) | p95 (ms) | Max (ms) |
| --- | ---: | ---: | ---: | ---: | ---: |
| gru → sjc | 10 | 59.3 | 86.9 | 227.3 | 227.3 |
| sjc → gru | 10 | 27.3 | 43.3 | 2135.6 | 2135.6 |
| gru → fra | 10 | 77.3 | 95.3 | 167.6 | 167.6 |
| fra → gru | 10 | 11.4 | 34.4 | 3099.4 | 3099.4 |
| sjc → fra | 10 | 27.1 | 42.3 | 118.9 | 118.9 |
| fra → sjc | 10 | 29.8 | 36.4 | 211.5 | 211.5 |

| Source → rejoined region | Documents | Catch-up (ms) |
| --- | ---: | ---: |
| gru → sjc | 20 | 717 |
| sjc → gru | 20 | 1222 |
| gru → fra | 20 | 623 |
| fra → gru | 20 | 6790 |
| sjc → fra | 20 | 6294 |
| fra → sjc | 20 | 6898 |

## Live job preparation without live machines

CI's `e2e` job depends on deployment. Its first test step now selects `Category=replication-matrix-live`: six write directions, six delete directions and one three-region conflict. Each client uses the existing `fly-prefer-region` handler and refuses a mismatched `X-TerraBase-Region`. The step requires 13 actual passes recorded on Fly machines. The broader suite still runs afterwards, and both sets of logs/outcomes are uploaded even after failure. Live machine outages remain a separate disposable-app qualification.

The exact live selection was exercised locally on `d01a905e`:

```sh
TsDotnetRoot=/home/mariogk/.t3/worktrees/terrascale/cos-replication-matrix-sdk \
~/.local/bin/ts-heavy make terrabase-e2e \
  E2E_LABEL=live-selection-local-dry-run \
  E2E_RESULTS=/tmp/cos-replication-matrix-proof/live-selection-local-outcomes-2.jsonl \
  TEST_ARGS='-- --treenode-filter "/*/*/*/*[Category=replication-matrix-live]" --minimum-expected-tests 13 --output detailed --no-ansi'
```

Result: **13 actual passes, 0 failures, 0 skips, 0 expected failures**, 11.511s. This is local evidence only; the CI outcome guard rejects it as Fly evidence.

| Preparation check | Measured result |
| --- | --- |
| Runner discovery with `--list-tests` and the live category | Exactly 13 cases: every write/delete direction plus the conflict |
| `RegionPinTests` with `--minimum-expected-tests 8` under `ts-heavy` | 8/8 harness checks passed, 304 ms; region and machine pins reject incorrect identity |
| Fly URL set, API key/project/database/revision unset, same live selection | All 13 cases failed with the named missing API-key requirement before HTTP; negative-control runner exit 2 |
| CI outcome guard, using synthetic outcome rows only | Accepts 13 Fly passes; rejects missing cases, skip, expected failure and local fallback |
| `nice -n 19 actionlint -shellcheck='' .github/workflows/ci.yml`, extracted step `bash -n`, and `nice -n 19 make -n terrabase-e2e ...` | Passed; filter stays quoted through make. Shellcheck integration was not run because the local shim has no selected version |

The first live-selection dry run failed before tests because an unquoted filter expanded into filesystem paths (`Argument list too long`). The workflow now quotes the filter through make, and the corrected real run passed 13/13. The failed attempt is preserved and uncredited.

`gh secret list --json name` showed no `TERRABASE_E2E_FLY_API_KEY`, `TERRABASE_E2E_FLY_PROJECT` or `TERRABASE_E2E_FLY_DATABASE`. T2 needs to provision the dedicated scoped e2e resources and GitHub secrets after the machines are live. No live requests, machines or production credentials were used in this preflight.

## Evidence and remaining gates

All raw artifacts are retained on delta under `/tmp/cos-replication-matrix-proof/`:

| Log | SHA-256 |
| --- | --- |
| `t0-scratch-make-check.log` | `f77144436dc9bd9685313f60fee7c3fd18c4c5e04b927727a0f74bc96b1e776b` |
| `t0-scratch-matrix.log` | `1a03184e51b053fb7f5787aaa3dd49f053a573590a345db2a18f58e59bc0256a` |
| `live-selection-local-dry-run-2.log` | `f29cdd7b62008c5129dc3928db63b5f5faed2066f7e811812be641f6e606fb8f` |

Discovery, region-pin, missing-credential and make dry-run logs and per-test JSONL outcomes are in the same directory. Both task worktrees are clean and preserved for the COS resume. No branch has been pushed and no PR exists because required checks are not green. Slow, Docker execution and live Fly execution were not run. Local proof qualifies graceful stop/rejoin, not abrupt power loss, permanent origin loss, rebuild certification or incarnation fencing.

**WAITING ON T0/T2**: T0 must resolve required-check failures and land; T2 must supply the live three-region environment and dedicated e2e credentials. The COS then resumes PR publication, exact-commit checks, live six-direction and disposable-app outage qualification, Fly lag publication and merge/cleanup.
