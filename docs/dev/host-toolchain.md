# Dev-host test toolchain

Delta, mariogk-dev and beta run the monorepo's heavy jobs through `ts-heavy-any`.
Host-local tools live in `~/.local/bin`; global mise pins live in
`~/.config/mise/config.toml`. Capacity and tool availability are separate placement
requirements. Keep the two-core / 4 GiB delta reserve and existing admission limits.

## Pins and native dependencies

The 2026-11-04 alignment uses these versions on all three NixOS hosts:

| Tool | Installed version / source |
| --- | --- |
| .NET SDK | `11.0.100-rc.3.26522.148`, from `global.json`; .NET 10 and 11 runtimes for the .NET SDK tests |
| Node | `24.21.0` (the dashboard asset pin), with `26.13.0` retained; `sdk-js-test-fast` runs npm `11.18.0` |
| Go | `1.27.3`, from `sdk/go/.go-version` |
| Python | `3.15.2`, plus `3.12.15` for the Python SDK; uv `0.13.7` |
| Rust | `1.99.1`, from `sdk/rust/rust-toolchain.toml`; `wasm32-unknown-unknown` and `wasm32-wasip1` targets |
| Playwright | .NET package `1.63.0`, Chromium/headless-shell revision `1243` (`153.0.8010.12`) |
| PostgreSQL client | `18.6`, Nix binary cache installation with a GC root; `psql` on PATH |
| NATS server | `2.15.2`, supplied by the host |
| Container engine | Rootless Podman `6.1.4`; a `docker` wrapper delegates to Podman for Makefile callers |

Install browsers using the pinned .NET package's Node and CLI, rather than an
unpinned npm Playwright release. `install --with-deps chromium` attempts to use
`apt-get` on these NixOS hosts and fails. Their existing nix-ld library closure
supports Chromium: use `install chromium` and verify a real headless launch.
Set `PLAYWRIGHT_SKIP_BROWSER_GC=1` during installation so another job's cached
browser revision is retained. Do not delete caches used by running jobs.

`ts-heavy-any` seeds platform clones from
`~/.cache/terrascale/turso-native/<sha256-of-SHA256SUMS>/`. Both architecture files
must pass the candidate's `scripts/vendor/verify-turso-native.sh`. A per-pin lock
protects cache publication, and restoration copies files rather than linking
writable artifacts between jobs. A cold or invalid cache downloads the pinned
release under admission; a cache miss alone does not disqualify a host.

## Placement preflight

`ts-slot preflight <label>` prints JSON with `supported` and `missing`, returning
78 when a tool is absent. `ts-slot status --json --label <label>` includes those
fields in `fit[label]`; `fits` also requires available admission capacity.

Known platform labels check the exact SDK pin and runtime. Dashboard and auth Fast/Slow
additionally launch the pinned Playwright Chromium, which checks the driver,
browser revision, loader and system libraries together. SDK labels check their
language tools; region-sim checks NATS, and PostgreSQL/container labels check their
clients. Combined labels combine requirements. These probes never install tools.

`ts-host` excludes unsupported hosts instead of merely ranking them last.
`ts-heavy-any` checks an explicitly requested initial host and selects an equipped
peer when necessary, before registering or superseding a job. `ts-ship` filters
placements and redistribution separately for each suite. Queued portable jobs
use the same `ts-host` capability filter when transferring after 60 seconds.
No host with all tools available bypasses the ordinary resource queue.

Before replacing helpers, preserve `<name>.before-toolchain` for `ts-heavy-any`,
`ts-ship`, `ts-slot` and `ts-host`, then compare SHA-256 hashes on all three hosts.
Replace files atomically. Running jobs keep their original owner; waiters reload
updated slot code while preserving their queue age.

The negative placement proof used `postgres-psql-smoke` before installing clients
on the two peers: `ts-host` excluded both, and an explicit mariogk-dev request
printed `skipping mariogk-dev ... missing: [psql 18+]` and selected delta.

## Delta proof, 2026-11-04

One run of each target on delta, through a partitioned `ts-heavy-any` job,
using monorepo revision `7e537eb1a45ffdfb083b85a46643702b3e857e0b`. Times include restore/build
and exclude scheduling and checkout preparation. Each suite retained its normal
parallelism.

| Make target | Wall seconds | Result |
| --- | ---: | --- |
| `dashboard-test-fast` | 67.112 | Pass |
| `terrabase-test-fast` | 83.626 | Pass |
| `auth-test-fast` | 92.602 | Pass |
| `sdk-dotnet-test-fast` | 23.356 | Pass |
| `sdk-js-test-fast` | 7.498 | Pass |
| `sdk-go-test-fast` | 5.695 | Pass |
| `sdk-python-test-fast` | 2.211 | Pass |
| `sdk-rust-test-fast` | 34.437 | Pass |

The JS suite reported 52 passed and one pre-existing optional live C# SQL test
skipped because live connection settings were absent. Dashboard (105), TerraBase
(87), auth (51), and both .NET SDK runtimes (266 each) reported zero skips.
The cold dashboard, TerraBase, auth and Rust targets exceed the 30-second Fast
policy; auth's runner took 42.358 seconds. These remain performance-policy debt
([#1084](https://github.com/TerraScale/terrascale/issues/1084));
this tooling change does not change timing enforcement or test selection.

All 18 capability labels passed on each host. SHA-256 fingerprints for the
installed helpers match on delta, mariogk-dev and beta:

```text
c0dcb9eff37603cd3fffab8339cc6f0103b88e43a71fa03e58f94e9236ff62dd  /home/mariogk/.local/bin/ts-heavy-any
b4c5fc17fd9bbb9ea5b81283bc22bcfcd0b67e3dbb855f9fac42c1596dec9750  /home/mariogk/.local/bin/ts-ship
88ba88d846dc344f42215ff13af1318697616047142ea9592dcdc6350447bcb4  /home/mariogk/.local/bin/ts-slot
521353e4e2fa81e60a020b0cd72af10f796cd6b63401f49a2a47e1a489fb27c6  /home/mariogk/.local/bin/ts-host
1dd1c9555e5a7f49f02383003d9bdf6be3cb2428ad7d1216ff981e8b756d52c4  /home/mariogk/.local/bin/docker
```

The urgent throwaway-account follow-up uses the SQL scopes
`sql:read,sql:write`, preserving database scopes. All three hosts match SHA-256
`d74d7489ebbd923fd52f281152b1e9eb364eccd515e0081fa93e4d9a7cf399c2`;
`ts-throwaway-account.before-sql-scopes` preserves the old helper.
A live create and delete of `tooling-sql-20261104-1944` both returned zero;
its local state and key files were removed.
