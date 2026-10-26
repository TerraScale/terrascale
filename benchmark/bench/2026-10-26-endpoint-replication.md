# RT6 endpoint replication — partial evidence

Production baseline supplied by FR1: batch 10, TerraBase `841dd0d99`, StorageFormat 18, no reset.
Preflight clients ran on delta; no production host ran a client for this thread.

| Public endpoint | Observed result |
| --- | --- |
| `https://api.terrascale.tech/readyz` | `200`, ready, gru |
| Postgres, gru / sjc / fra | TLS 1.3; system trust and hostname `pg.terrascale.tech` verified at every regional IP |
| Wire, all regions | Closed; FR1 confirms ts-orch has no TLS route for port 7464 |
| Local build, mariogk-dev, code `e302e70de` | Passed; zero warnings/errors |
| Local three-region matrix, same code/host | 5/5 passed, zero skips, 17.040 s |
| Wire protocol tests, same code/host | 9/9 passed, zero skips, 8.117 s |

Postgres used the SSLRequest handshake with SNI/hostname `pg.terrascale.tech`, targeting gru `163.176.124.12`,
sjc `163.192.54.17` and fra `130.162.238.198`. Initial sjc/fra timeouts preceded FR1's ingress correction.

The local matrix uses real three-region servers and all five endpoint readers. Required CI and merge are pending.
No production mutations have run, no test tenant has been provisioned, and lost writes, LWW violations and replication
p50/p99 are **not measured**. Remaining acceptance work: ALTER TABLE, exact concurrent LWW winners through every
endpoint, public Wire TLS, dedicated-tenant provisioning/cleanup and three live measurement repeats on the same dev host.
