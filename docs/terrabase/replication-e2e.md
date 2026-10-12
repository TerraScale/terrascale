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

Stops the single `sjc` machine of `TERRABASE_E2E_FLY_APP` (default `ts-terrabase`), writes through `gru`, starts `sjc`
and waits for it to catch up. It needs `flyctl` logged in. It runs alone because it stops a machine the other tests use.
Without `TERRABASE_E2E_FLY_URL` and `TERRABASE_E2E_FLY_OUTAGE=1` it is reported as skipped.

## Expected failure

A scenario whose slice has not landed is listed in `tests/TerraBase.E2E/Gaps.cs` with the slice it waits on, and runs as a
strict expected failure. The test passes while the scenario fails, and fails with "has landed" once it passes, so the
slice that lands it removes the entry. Test names carry no expected-failure suffix. The run summary reports each test as
actual pass, harness check, expected failure, not supported, skip or fail (`tests/TerraBase.E2E/COVERAGE.md`).

## CI

Run `make terrabase-e2e` with the `TERRABASE_E2E_FLY_*` variables from CI secrets after the `sjc` deploy. Replication needs
both regions live, so a run after the `gru` deploy alone cannot check it. Do not set `TERRABASE_E2E_FLY_OUTAGE` in CI.
The suite is not part of `make check`.

## Local path: what depends on today's region code

`Cluster.StartNodeAsync` starts each node with the harness's `wan-regions` key and its fake region bus (`FakeRegionNetwork`). When the
region code is replaced, this is the only place that changes.
