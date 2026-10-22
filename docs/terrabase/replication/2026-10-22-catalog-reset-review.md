# Catalog work after the main reset — 2026-10-22

This reviews the catalog entries in PR #396's reland inventory against the implementation after PRs #340/#341 and #349. The restore gate uses automatic discovery and authenticated backup anchors; the old operator-file bootstrap and catalog-open override must not return.

| Removed work | Current disposition |
| --- | --- |
| Explicit gate check in API-key creation | Already enforced by `EngineApiKeyStore` → `SystemCatalog.WriteAsync` → `EnsureCatalogAsync`. A second endpoint check would duplicate it. |
| Platform seed waits for the gate | Already enforced by guarded catalog writes. Concurrent fresh-node coverage checks the platform records and their fixed IDs. |
| Catalog bootstrap/open helper | Replaced by automatic fresh-catalog bootstrap and recovery. Harnesses may wait for the gate; they must not authorize opening it. |
| Apply replicated database deletion/name decisions to local files | Adapt the former convergence worker to the current lifecycle reservation and deletion checks, without changing persisted keys. |
| Catalog across separate processes | Restore process coverage for creation/name races, deletion during an outage/name reuse, and replicated schemas/collection drops. The older anchored power-loss variant needs a harness the current tree does not have; existing restore-gate tests remain the available anchor coverage. |

## Usage records and the format-reset batch

Usage is best-effort dashboard telemetry, not billing. Current persisted lifetime counts use the shared `ops` document, bucket counts use shared time keys, and retry settlement uses shared `flush`. Because the system catalog replicates, concurrent regional read/add/write flushes can overwrite counts; a local successful flush does not establish a cluster-wide sum.

The removed implementation used machine-owned `ops@<machine>`, `<bucket>@<machine>` and `flush@<machine>` documents and summed the components on reads. Restoring that design changes persisted key interpretation and must be included in the FR1 storage-format reset batch, with concurrent-flush coverage and matching analytics fixtures. It is not safe to claim that behavior from the current shared records. No migration or compatibility fallback is proposed.

The removed machine-owned cleanup-intent prefix likewise belongs in that batch: changing `pending_database_cleanup` to `machines/<machine>/pending_database_cleanup` changes which persisted records a node consumes. Until that work lands, cleanup intents retain the current shared format. The convergence worker retries from a durable database tombstone under the engine's deletion reservation, without opening the stale file; it preserves missing/unverified records. A peer can still clear a shared intent after another node has removed its main file, leaving that node's sidecars or in-memory reservation without discoverable retry work. Machine-owned cleanup records must close that gap in FR1.

No storage format number, table or persisted key is changed by this review document. Historical COVERAGE pass claims and expected-failure rewrites are not relanded without new results.
