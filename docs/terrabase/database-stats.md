# Database statistics

`Features/Stats/DatabaseStats.cs` records node-local observations per database: reads, committed writes, requests, errors, request latency and sampled storage bytes. Windows close every five seconds. These are observations of work served by that process, not proof of global convergence or a billing ledger.

A push stream that returns NATS error 10074 is not retried, because the stream wants more replicas than the cluster has.

The in-memory database map is bounded at 16,384 entries. Excess events use the overflow path and increment `terrabase.stats.dropped` with `kind=database`. Entries idle for three hours are removed. Restarting resets process-local statistics.

Tenant IDs and database IDs are not Prometheus labels. Request logs exclude SQL text, row values and credentials. The dashboard must retain unknown values when a sample or a region is missing; it must not infer zero or a complete cross-region total.

See [metrics export](metrics-export.md), [replica status](replica-status.md) and [monthly operation allowances](operation-allowance.md). Monthly write units, free SQL reads and SQL read fair use use the durable usage service rather than these process-local counters.
