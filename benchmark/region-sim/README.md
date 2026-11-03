# region-sim

`benchmark/cmd/region-sim` runs a two-region TerraBase cluster on one host and drives it with the real `ts` CLI. It prints a Markdown table (`scenario | pass/fail | lag | notes`) and exits non-zero if any scenario fails.

- **gru-1** (region `gru`) is the database's home and takes writes.
- **sjc-1** (region `sjc`) is a read-only replica of the home.

## Run

```sh
make region-sim                      # builds TerraBase, ts and region-sim, writes benchmark/bench/region-sim/<UTC>.md
make region-sim REGION_SIM_ARGS='-logs /tmp/region-sim-logs'
```

Run it on a dev host through `ts-heavy-any` (never on mariogk-desktop). For a fresh checkout, run `scripts/vendor/download-turso-native.sh` first. `nats-server` must be on `PATH`. The node addresses 127.0.0.2–127.0.0.5 must be usable; Linux routes all of 127.0.0.0/8 to loopback by default.

```sh
ts-heavy-any bash -c 'mkdir -p $HOME/region-sim && exec > $HOME/region-sim/job.log 2>&1 </dev/null; scripts/vendor/download-turso-native.sh && make region-sim REGION_SIM_ARGS="-logs $HOME/region-sim/logs"' --repo terrascale --branch <branch>
```

Flags: `-terrabase <binary or .dll>`, `-ts <ts binary>`, `-out <file>`, `-logs <dir>`. Without `-logs`, node logs are deleted with the run directory. On a failure, the end of each node's log is printed to stderr.

## What it sets up

| Piece | How |
| --- | --- |
| TerraBase nodes | `DOTNET_ENVIRONMENT=Development` plus `TS_REGION`, `TS_NODE`, `TS_PEERS_FILE`, `TS_NATS_URL`, `TERRABASE_NATS_TOKEN_FILE`, `TERRABASE_PEER_SECRET_FILE` and `TERRABASE_MASTER_KEY_FILE` (0600 files holding one per-run value shared by both nodes). Each node also gets `TERRABASE_{PORT,INTERNAL_PORT,METRICS_PORT,POSTGRES_PORT,DATA_DIRECTORY,AUTH_ISSUER,AUTH_AUDIENCE}`. Ports are ephemeral. Both nodes use the same internal port, because a peers line has no port. |
| Peer path | gru-1 binds 127.0.0.2 and sjc-1 binds 127.0.0.3. Each node gets its own peers file. Its own line holds its bind IP. The other node is listed at a forwarder address: sjc-1 sees gru-1 at 127.0.0.4, and gru-1 sees sjc-1 at 127.0.0.5. Pausing the forwarders black-holes traffic like a partition. |
| NATS | `nats-server -c <0600 conf>` with token auth on an ephemeral loopback port. The network-cut scenario stops and restarts it. |
| ts CLI origins | Each region has an in-process `HTTPS_PROXY`. It terminates CONNECT with a per-run CA (`SSL_CERT_FILE`) and forwards `api.terrascale.tech` to that region's plain HTTP/1 client listener. `auth.terrascale.tech` goes to the ts-auth stand-in. Every CLI call gets an isolated `HOME`/`XDG_*`. |
| ts-auth | A **test-only stand-in** inside the harness. It serves OIDC discovery and JWKS over HTTPS to TerraBase at `https://localhost:<port>/terrascale`, the device grant (auto-approved), refresh and token exchange, and the organization/project calls of `ts projects create`. Tokens are ES256 `at+jwt` with `project_id`, `org_id` and `scope`, as in `tests/TerraBase.TestHarness/TestIssuer.cs`. Real ts-auth cannot be used: approving a device code needs a browser session and an emailed passcode, and ts-auth stores its own state in TerraBase. |

## Scenarios

Functional steps use `ts`: `auth login`, `projects create`, `db create simdb -replica-regions sjc` and `exec`. The lag loop, bulk load and visibility polls call `POST /v1/projects/{project}/databases/{db}/sql/execute` directly on the node, because starting a CLI process per statement would dominate the measurement. Visibility is polled with a deadline, never a fixed sleep.

| Scenario | Pass condition | Lag |
| --- | --- | --- |
| cli-login | `ts auth login` signs in; `ts whoami` works | — |
| create-project-db-replica-sjc | project created; db home `gru` with replica `sjc` | — |
| dml-in-A-visible-in-B | DDL, insert, update and delete on gru, all visible on sjc; CLI read on sjc matches | last write ack → visible on sjc |
| write-on-B-refused-421 | HTTP write on sjc returns 421 `wrong_region`; `ts exec` write on sjc fails; nothing written | — |
| checkpoint-A-then-read-B | 1,536 rows of 3 KiB (past the 1,024-frame WAL checkpoint policy), then an update, all visible on sjc | update ack → visible |
| restart-B-catches-up | 51 writes while sjc-1 is down; after restart sjc-1 serves all of them | sjc-1 ready → caught up |
| restart-A | gru-1 keeps its data across a restart; a new write reaches sjc | write ack → visible |
| network-cut-10s-and-NATS-down | 10 s of writes on gru with the peer path cut both ways and NATS stopped; sjc converges after heal | heal → converged |
| bulk-10k-count-checksum-B | 10,000 rows in batches of ≤256 statements; count and full-row SHA-256 equal on gru, sjc and the generator | load ack → count visible |
| lag-500-writes-p99<200ms | 500 sequential single-row writes, sjc polled every 1 ms; p99 < 200 ms | p50 / p99 |

`cli-login` and `create-project-db-replica-sjc` are prerequisites. If either fails, later scenarios are reported as failed and not run.
