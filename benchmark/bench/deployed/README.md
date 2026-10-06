# Deployed TerraBase benchmark (TS-606)

Benchmarks the production gru-1 TerraBase from a real user's position: the load
runs on a homelab client (`beta` or `delta`, whichever is less busy) over the
public internet, through Cloudflare, against `https://gru.api.terrascale.tech`.
No node is created in OCI. ts-orchestrator's NetBird SSH reads the server side.

```sh
TS_ORCH=/path/to/ts-orch TS_CLI=/path/to/ts \
TERRASCALE_DASHBOARD_TOKEN=… TERRASCALE_PROJECT_ID=<benchmark project> \
nice -n 19 make deployed-smoke DEPLOYED_ARGS="--client beta"
```

| Step | What happens |
| --- | --- |
| Preflight | Reads the LB `terrabase` backend set (`oci lb backend list`); `--full` is refused until it is TS-578's final set (ctl-1..3). Requires 200 from gru.api `/healthz`, gru.auth's OIDC document and the dashboard's `/health/ready`. |
| Client | Records host, CPU model and count, load average before and after, client CPU busy % during the run, and the median of 10 TCP connect / TLS ready / `/healthz` times to gru.api, so a client bottleneck is visible. |
| Database | `ts databases create bench-…` and a database-scoped `tb_` key (`ts keys create`) in the dedicated benchmark project; 32 seed documents. Both are deleted and revoked in `finally`. No other database is touched. |
| Load | The [https-document driver](../../https-document/src/main.rs) runs point read, insert, update, delete, filter+sort query, two-page list, two-put transaction and an 80/20 read/write mix at each concurrency, with small (0 B) then large (16 KiB) documents, each payload in its own collection whose 32 seeds carry the same padding. Every write is read back. |
| Bounds | Smoke: concurrency 1/4/16, 10 ops per worker, at most 50 req/s, 20 min. Full: 1–64, 100 ops per worker, 400 req/s, 90 min. One shared limiter spaces every request (setup writes, timed requests, both list pages, readbacks) at 1/rate; waiting for the second page's slot is excluded from its latency. |
| Abort | The driver stops after the first case with an error or a p99 over 3 s (smoke) / 2 s (full). A guard thread stops it when gru.api, gru.auth or the dashboard answers non-200 twice in a row (2 s apart). On the client, a watchdog enforces the duration bound itself and holds a lease on the runner's 2 s stdin heartbeat: if it stops for 10 s or the connection closes, the driver's process group gets SIGTERM, then SIGKILL. |
| Members | For every member, `ts-orch netbird ssh` samples every 5 s: process CPU time, working set, GC collections and pause, allocations (TerraBase's loopback `/metrics`) and `/proc/<pid>/io` disk bytes (SQLite and journal). The samplers exit on their own after the run's bound. |
| Cost | US$ per million ops = LB members × US$0.019/h (A1: 1 OCPU, 6 GB, list price) ÷ (ops/s × 3600). A rate-bounded smoke never saturates the cell, so it is an upper bound. Omitted when the LB set was not read (CI). |

The nightly and manual [Deployed smoke](../../.forgejo/workflows/deployed-smoke.yml)
CI job runs the smoke from the runner (`--client local`) without LB and member
metrics, and skips with a warning until `BENCH_DASHBOARD_TOKEN` and
`BENCH_PROJECT_ID` are set.

Cloudflare answers 403 to Python's default `Python-urllib` user agent, so every
request sends `ts-benchmark/1 (TS-606)`. No WAF or rate-limit rule was needed so far.

## Runs

| Date | Kind | Scoreboard |
| --- | --- | --- |
| 2026-10-06 | Preflight and idle members (load blocked on the benchmark project credential) | [2026-10-06](2026-10-06/README.md) |
