# gru-1 preflight and idle members, 2026-10-06

No load ran: the dedicated benchmark project's dashboard token is not provisioned
yet. The runner stopped at that check after its live preflight.

## Preflight (20:51Z)

| Check | Result |
| --- | --- |
| LB `gru-1-api` / `terrabase` active | 10.251.0.11, .12, .13, .21 (ctl-1..3, db-1); .22 (db-2) offline and drained. Not TS-578's final set: smoke only. |
| gru.api `/healthz`, gru.auth OIDC, dashboard `/health/ready` | 200, 200, 200 |
| Client | beta, AMD Ryzen 5 PRO 6650H, 12 CPUs, load 3.22 (delta: 16 CPUs, load 6.74) |
| beta → gru.api via Cloudflare (median of 10) | TCP connect 20.1 ms, TLS ready 44.8 ms, `/healthz` 73.1 ms |

## Idle members (20:53Z, 35 s, no benchmark load)

| Member | CPU % | Peak RSS MB | GC collections | GC pause ms | Allocated MB | Disk read MB | Disk write MB |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| gru-1-ctl-1 | 5.8 | 199.3 | 25 | 50.3 | 51.8 | 0.00 | 2.98 |
| gru-1-ctl-2 | 2.0 | 194.2 | 5 | 10.2 | 11.1 | 0.00 | 2.46 |
| gru-1-ctl-3 | 2.0 | 211.9 | 5 | 7.7 | 11.8 | 0.00 | 2.45 |
| gru-1-db-1 | 1.7 | 235.7 | 5 | 7.9 | 11.4 | 0.00 | 2.57 |
| gru-1-db-2 | 1.9 | 200.4 | 5 | 6.4 | 11.3 | 0.00 | 2.43 |

Idle write traffic is replication and probes: about 70 KB/s per member.
