# Fly benchmark credential

`benchmark/scripts/repl-fly-tenant.sh` creates and manages the credential the Fly replication benchmark uses. A user runs
it, through the morning entry point `scripts/handoff/morning-credentials.sh`. Agents never run it, and nobody but the
user mints or installs it.

| Command | Changes | What it does |
| --- | --- | --- |
| `dry-run` | nothing | Checks `flyctl`, `gh`, `curl` and `jq` logins and gru's reachability, and prints what `install` would create |
| `install` | tenant (first time), key, Fly secret, GitHub variables | Creates the tenant once and mints a key. Running it again rotates the key: a new one is minted and the previous one revoked |
| `status` | nothing | Ids, the Fly secret's digest, whether the key is active and its expiry, and the number of `bench-*` collections |
| `revoke` | key, Fly secret, `TS_BENCH_FLY_KEY_*` variables | Revokes the key in TerraBase and removes the Fly secret. The tenant and its data stay |
| `cleanup` | data | Drops every `bench-*` collection (where the 1M benchmark documents live) of the benchmark database, and nothing else |

## What it creates

- **Tenant:** organization "TerraScale benchmark" (enterprise plan), project `bench`, database `bench` (home gru, replicas
  sjc and fra). It is separate from the e2e tenant. The owner is a synthetic user id.
- **Key:** `documents:read` and `documents:write`, narrowed to the `bench` database, expiring after 30 days.
- **Secret:** Fly secret `TS_BENCH_API_KEY` on app `ts-bench`. The app is created if missing; with no machines between
  runs it costs nothing.
- **Not secret:** GitHub repository variables `TS_BENCH_FLY_OWNER`, `TS_BENCH_FLY_ORGANIZATION`, `TS_BENCH_FLY_PROJECT`,
  `TS_BENCH_FLY_DATABASE_ID`, `TS_BENCH_FLY_KEY_ID` and `TS_BENCH_FLY_KEY_EXPIRES` on TerraScale/terrascale.

The key never reaches the operator host's disk, output or command lines. The `POST /admin/api-keys` response is split in
one pipe: the key id goes to `gh variable set`, and `TS_BENCH_API_KEY=<secret>` goes to `flyctl secrets import --stage` on
stdin.

## How it reaches TerraBase

It runs `flyctl proxy` to gru's internal listener (port 8081, h2c, Fly's private network). There the platform operator
acts as the tenant owner on the Management API (`X-TerraBase-User-Id`), issues keys and drops collections. Overrides,
with their defaults: `BENCH_TERRABASE_APP=ts-terrabase`, `BENCH_FLY_APP=ts-bench`, `BENCH_FLY_ORG=terrascale`,
`BENCH_GITHUB_REPO=TerraScale/terrascale` and `BENCH_PROXY_PORT=18081`.

## Tests

`benchmark/scripts/tenant_script_test.go` (part of `make benchmark-check`) runs the real script against fake `flyctl`,
`gh` and `curl` in `testdata/fakes`. Nothing reaches Fly, GitHub or TerraBase. The tests check that:

- `dry-run` makes no mutating call;
- `install` hands the key only to the fake Fly secret store (never to output, argv logs or any file) and sets every variable;
- a second `install` rotates the key and revokes the previous one;
- `status` reports an active key;
- `cleanup` drops only `bench-*` collections;
- `revoke` revokes the key, removes the secret and forgets the key id.
