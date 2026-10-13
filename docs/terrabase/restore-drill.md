# Catalog restore gate

Every server starts closed before cleanup or seeding: an old `_system.db` can revive revoked keys, removed members and deleted databases. Catalog authorization, management, readiness and backup shipping wait for verification; probes and recovery receivers stay available.

A fresh empty cluster initializes automatically. A fresh replacement pulls every listed trusted peer, ignoring destroyed historical members. Existing catalogs require trusted-peer pulls and/or an encrypted Tigris anchor at `catalog-anchor/<machine-id>` (format 1: incarnation, CDC head, UTC time). Snapshot and replay copies receive new incarnations. An unreadable anchor, older head, different incarnation or missing non-genesis anchor cannot establish trust; restored peers cannot export system replication state or verify each other.

An existing machine waits for all recorded/listed peers, or a trusted majority when its own anchor proves it was not restored. A catalog proven stale stays closed for reviewed recovery without a bypass. Recreate a lost region with a fresh catalog from healthy peers, then restore tenant databases. Cleanup skipped while closed runs at the next verified restart.

Revocations and destructive management routes anchor before acknowledging success, including commit-envelope replay. Failure returns 503 `catalog_anchor_unavailable`; the local commit may exist, so retry. Tenant writes and non-revoking management commits do not wait; background anchoring runs every 15 seconds.

Inspect over `fly ssh`: `curl --http2-prior-knowledge http://<machine-private-address>:8081/v1/catalog-gate`. The response reports `open`, `trusted`, `pending` and SHA-256 of the catalog bucket digests. Unverified tenant recovery never opens the catalog.

Identical organization/project/database creates reuse live catalog entries without changing versions; missing local storage initializes after successful commit. Different content, ownership, placement and tombstones remain `id_taken`. Shutdown-caused cancelled RPCs become normal cancellation; real faults still surface. Parallel Fast integration controls cover these behaviors and stale/absent/tampered anchors.

Production acceptance waits for T2 and COS `GO DRILL`: retain timestamped Tigris listings, compare three-region digests after machine/volume recreation, prove bidirectional replication, inspect daily Fly snapshots and restore one. Publish backup/recovery/convergence timings in `benchmark/bench/`; local tests establish no production proof.
