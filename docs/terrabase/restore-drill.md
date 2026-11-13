# Catalog restore gate

Every server starts closed before cleanup or seeding: an old `_system.db` can revive revoked keys, removed members and deleted databases. Catalog authorization, management, readiness and backup shipping wait for verification; probes and recovery receivers stay available.

With backup configured, verification reads the machine's anchor from the backup bucket (and writes it on a fresh machine), so a server's catalog stays closed at start until the backup store answers: a process that starts while the store is unreachable is not ready and answers catalog-backed requests with 503 until the store is back, without operator action. While the store fails, the gate checks again every second; it never opens over a catalog that is behind its anchor. `terrabase_catalog_closed{reason}` is 1 for the reason it is closed (`unverified`, `store_unavailable`, `bucket_missing`, `anchor_ahead`, `anchor_missing`, `anchoring_unavailable`) and 0 for the others, all 0 once it is open; each change of reason logs once, with the S3 error code, HTTP status, operation and object key for a store failure. `anchor_ahead` (the anchor names another incarnation or a later offset than the local catalog), `anchor_missing` (an existing catalog without its anchor), `bucket_missing` and `anchoring_unavailable` do not clear by themselves: they need a restore, the anchor or bucket back, or the backup configuration, and the gate checks them every 15 seconds.

Genesis verification reads all catalog buckets as one bounded indexed page per cursor, including retained deletions; it does not issue a query for every empty bucket. The five-second initial verification budget and fail-closed behavior are unchanged.

A fresh empty cluster initializes automatically. A fresh replacement pulls every listed trusted peer, ignoring destroyed historical members. Existing catalogs require trusted-peer pulls and/or an encrypted Tigris anchor at `catalog-anchor/<machine-id>` (format 1: incarnation, CDC head, UTC time). Snapshot and replay copies receive new incarnations. An unreadable anchor, older head, different incarnation or missing non-genesis anchor cannot establish trust; restored peers cannot export system replication state or verify each other.

System CDC groups capture the supported management catalog version with dependent rows in the same transaction, even when that version arrived from another origin. The unchanged captured proof ships first and stays identical when the same cursor is replayed after a newer peer version arrives. The proof preserves the received state and uses the dependent group's stamp, so forwarding a future version neither rewrites it nor lifts the shared clock or the sender's applied frontier. Capture reserves and accounts for the proof's slot and bytes; CDC paging counts it in its bounds. Unsupported versions and versionless catalogs containing application state remain refused.

An existing machine waits for all recorded/listed peers, or a trusted majority when its own anchor proves it was not restored. A catalog proven stale stays closed for reviewed recovery without a bypass. Recreate a lost region with a fresh catalog from healthy peers, then restore tenant databases. Cleanup skipped while closed runs at the next verified restart.

Revocations and destructive management routes anchor before acknowledging success, including commit-envelope replay. Failure returns 503 `catalog_anchor_unavailable`; the local commit may exist, so retry. Tenant writes and non-revoking management commits do not wait; background anchoring runs every 15 seconds.

Inspect `/v1/catalog-gate` through the authorized private administrative channel using HTTP/2 and the [peer credential](peer-authentication.md), supplied to curl through a mode-0600 header file that is deleted after use. A bare request on 8081 is currently recorded as unmatched; the following enforcement release will refuse it with 401. The response reports `open`, `trusted`, `pending` and SHA-256 of the catalog bucket digests. Unverified tenant recovery never opens the catalog.

Identical organization/project/database creates reuse live catalog entries without changing versions; missing local storage initializes after successful commit. Different content, ownership, placement and tombstones remain `id_taken`. Shutdown-caused cancelled RPCs become normal cancellation; real faults still surface.

Production acceptance needs reviewed machine/volume recovery targets, timestamped
Tigris listings, three-region digest comparison and bidirectional replication proof.
Publish backup/recovery/convergence timings in `benchmark/bench/`; local tests
establish no production recovery proof.

A push stream that returns NATS error 10074 is not retried, because the stream wants more replicas than the cluster has.
