# TerraBase replication end-to-end suite

> Live runs and the outage proof (`make terrabase-e2e-orch-outage`) target ts-orch; see [e2e-ts-orch.md](e2e-ts-orch.md).

`tests/TerraBase.E2E` checks leaderless multi-region writes in both directions between `gru` and `sjc`: a write in one
region is readable in the other, concurrent writes converge to one identical state, deletes converge, and a bulk load
matches in count and checksum. Each scenario waits for convergence against a deadline and logs its replication latency.

## Run locally

```sh
nice -n 19 make terrabase-e2e
```

Two in-process nodes, `gru` and `sjc`, joined by the harness's in-memory region bus (`FakeRegionNetwork`). Nothing else is needed.

Each test creates its own `e2e-<guid>` collection in the shared database and deletes it afterwards.

## Expected failures

`tests/TerraBase.E2E/Gaps.cs` lists the scenarios that wait on a slice not yet on main, each with the failure it shows
today: E11 (SQL writes, R9), E12 and the deleted-database scenario (catalog lattice, R10a #116/#121; then GC, R7 #120),
E13 (live move, R11) and E14 (GC, R7 #120: no tombstone of a deleted document is collected). A listed scenario that
passes fails the test with a message to remove its row; one that fails any other way fails it too. Every other scenario,
`Concurrent_writes_to_different_fields_both_survive` (per-field lattice) included, runs as a plain assertion.

E11's SQL statements go to `/sql`, which takes a ts-auth project token (`databases:read`), never an API key. A refused token (401/403) fails E11 as an unexpected failure:
only the R9 signature "only SELECT statements are supported" counts as its gap.

## Production evidence

Local results do not establish OCI cross-region behavior. A new production
acceptance run must verify each responding node and commit through the current
[OCI runtime](../oci.md); retired infrastructure receipts are not acceptance evidence.

## Local path: what depends on today's region code

`Cluster.StartNodeAsync` starts each node with the harness's `wan-regions` key and its fake region bus (`FakeRegionNetwork`). When the
region code is replaced, this is the only place that changes.
