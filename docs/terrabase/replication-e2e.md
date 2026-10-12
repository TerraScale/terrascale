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
| `TERRABASE_E2E_FLY_API_KEY` | A TerraBase API key of the dedicated e2e project (`documents:read`, `documents:write`, 90-day expiry), issued inside the Fly org by TerraBase's operator listener. A human installs, rotates and revokes it with `scripts/e2e/install-ci-credential.sh` (`--dry-run`, `--revoke`); agents never run it. E12 and E13 create databases, which needs `databases:write` (ts-auth project tokens only), so they fail on Fly until the OIDC-federated token replaces the key. |
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

`Concurrent_writes_to_different_fields_both_survive_expected_to_fail_until_per_field_lattice` passes while the regions
keep one of two concurrent field edits, which is whole-document last-writer-wins. The gap is tracked in
`.cos/fly-migration-plan.md` (Product focus + replication design). When the per-field lattice lands, the test fails
with a message to remove the marker. Then turn it into a plain assertion.

## CI

Run `make terrabase-e2e` with the `TERRABASE_E2E_FLY_*` variables from CI secrets after the `sjc` deploy. Replication needs
both regions live, so a run after the `gru` deploy alone cannot check it. Do not set `TERRABASE_E2E_FLY_OUTAGE` in CI.
The suite is not part of `make check`.

## Local path: what depends on today's region code

`Cluster.StartNodeAsync` starts each node with the harness's `wan-regions` key and its fake region bus (`FakeRegionNetwork`). When the
region code is replaced, this is the only place that changes.
