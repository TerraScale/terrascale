# TerraBase replication end-to-end suite

`tests/TerraBase.E2E` checks leaderless multi-region writes in both directions between `gru` and `sjc`: a write in one
region is readable in the other, concurrent writes converge to one identical state, deletes converge, and a bulk load
matches in count and checksum. Each scenario waits for convergence against a deadline and logs its replication latency.

## Run locally

```sh
nice -n 19 make terrabase-e2e
```

Two in-process nodes, `gru` and `sjc`, joined by the harness's in-memory region bus (`FakeRegionNetwork`). Nothing else is needed.

## Run against Fly

Set these variables, then run the same target:

| Variable | Meaning |
|---|---|
| `TERRABASE_E2E_FLY_URL` | Public base URL, for example `https://ts-terrabase.fly.dev`. Its presence selects Fly. |
| `TERRABASE_E2E_FLY_API_KEY` | The documents client's bearer. In CI it is the 15-minute ts-auth project token of the bound e2e project (`scripts/e2e/ci-oidc-credential.sh`, docs/auth/github-actions-oidc.md), the same value as `TERRABASE_E2E_FLY_PROJECT_TOKEN`, so every request of the run is for one project. A manual run may use a TerraBase API key of a project instead (`documents:read`, `documents:write`); `/sql` and database creation still need the project token. |
| `TERRABASE_E2E_FLY_PROJECT_TOKEN` | A ts-auth project token of the same project (`databases:*`, `documents:*`), for `/sql`. |
| `TERRABASE_E2E_FLY_PROJECT` | The project id. |
| `TERRABASE_E2E_FLY_DATABASE` | The database name. |

Every request is pinned to its region with the `fly-prefer-region` request header. Fly reports the serving region in the
`fly-region` response header. Any answer from another region, or without that header, fails the test with
`RegionMismatchException`. Fly would otherwise fall back to the other region and the test would pass without testing it.

Each test creates its own `e2e-<guid>` collection in the shared database and deletes it afterwards.

## Region outage (Fly only, never in a deploy run)

```sh
nice -n 19 make terrabase-e2e-fly-outage
```

Stops the single `sjc` machine of a pinned disposable app, writes through `gru`, starts `sjc` and waits for it to catch
up. It needs `flyctl` logged in. It runs alone because it stops a machine the other tests use. It never targets
production: without `TERRABASE_E2E_FLY_URL`, `TERRABASE_E2E_FLY_REVISION` and `TERRABASE_E2E_FLY_OUTAGE=1` it is reported
as skipped, and `TERRABASE_E2E_FLY_APP` must name the disposable app (no default; production and preview apps are refused,
see `e2e-disposable-app.md`).

## Expected failures

`tests/TerraBase.E2E/Gaps.cs` lists the scenarios that wait on a slice not yet on main, each with the failure it shows
today: E11 (SQL writes, R9), E12 and the deleted-database scenario (catalog lattice, R10a #116/#121; then GC, R7 #120),
E13 (live move, R11) and E14 (GC, R7 #120: no tombstone of a deleted document is collected). A listed scenario that
passes fails the test with a message to remove its row; one that fails any other way fails it too. Every other scenario,
`Concurrent_writes_to_different_fields_both_survive` (per-field lattice) included, runs as a plain assertion.

E11's SQL statements go to `/sql`, which takes a ts-auth project token (`databases:read`), never an API key. On Fly it
uses `TERRABASE_E2E_FLY_PROJECT_TOKEN` and fails without it. A refused token (401/403) fails E11 as an unexpected failure:
only the R9 signature "only SELECT statements are supported" counts as its gap.

## CI

Run `make terrabase-e2e` after the deploys with the `TERRABASE_E2E_FLY_*` variables that `scripts/e2e/ci-oidc-credential.sh` exports from the GitHub OIDC exchange (no e2e secret is stored). The deploy job passes
`--treenode-filter "/*/*/*/*[Category!=local-only]" --minimum-expected-tests 138` (every test case but the local-only
ones): the `local-only` category (whole-database backups, every node
stopped, a changes-feed issuer stopped, the proxy and pin harness) runs instead in CI's required `test-terrabase` stage
with `make terrabase-e2e-local`, which requires all 27 tests to pass with no skip. Replication needs
both regions live, so a run after the `gru` deploy alone cannot check it. Do not set `TERRABASE_E2E_FLY_OUTAGE` in CI.
The suite is not part of `make check`.

## Local path: what depends on today's region code

`Cluster.StartNodeAsync` starts each node with the harness's `wan-regions` key and its fake region bus (`FakeRegionNetwork`). When the
region code is replaced, this is the only place that changes.
