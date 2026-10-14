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
| `TERRABASE_E2E_FLY_API_KEY` | A TerraBase API key of the dedicated e2e project (`documents:read`, `documents:write`, no expiry by user decision 2026-10-12), issued inside the Fly org by TerraBase's operator listener. It stays valid until a human revokes it: `scripts/handoff/morning-credentials.sh install e2e` installs and rotates it, `revoke e2e` revokes it and deletes the secrets, `cleanup e2e` also deletes the e2e tenant (`dry-run`, `status` change nothing); agents never run it. E12 and E13 create databases, which needs `databases:write` (ts-auth project tokens only), so on Fly they are skipped as BLOCKED (`Gaps.cs`) until the OIDC-federated token replaces the key. |
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

E11's SQL statements go to `/sql`, which takes a ts-auth project token (`databases:read`), never the documents API key.
On Fly it uses the existing `TERRABASE_E2E_FLY_PROJECT_TOKEN` (nothing is minted) and fails without it; the deploy job
does not set it, and the app does not yet trust a project-token issuer, so E11 stays a failure on Fly until a narrow
project token and the platform issuer trust are approved.

## CI

Run `make terrabase-e2e` with the `TERRABASE_E2E_FLY_*` variables from CI secrets after the `sjc` deploy. The deploy job passes
`--treenode-filter "/*/*/*/*[Category!=local-only]" --minimum-expected-tests 139` (every test case but the local-only
ones): the `local-only` category (whole-database backups, every node
stopped, a changes-feed issuer stopped, the proxy and pin harness) runs instead in CI's required `test-terrabase` stage
with `make terrabase-e2e-local`, which requires all 26 tests to pass with no skip. Replication needs
both regions live, so a run after the `gru` deploy alone cannot check it. Do not set `TERRABASE_E2E_FLY_OUTAGE` in CI.
The suite is not part of `make check`.

## Local path: what depends on today's region code

`Cluster.StartNodeAsync` starts each node with the harness's `wan-regions` key and its fake region bus (`FakeRegionNetwork`). When the
region code is replaced, this is the only place that changes.
