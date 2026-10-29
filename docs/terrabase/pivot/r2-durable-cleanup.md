# F6 durable shape cleanup

This implements the durable cleanup part of
[r2-cdn-production.md](r2-cdn-production.md) (Retention, purge and
deletion, items 1–4). It replaces the volatile purge queue described in
[r2-shape-runtime.md](r2-shape-runtime.md). R2 holds shape ciphertext;
Tigris keeps backups and PITR. No live resource changes are part of it.

## Inventory

Every mirrored object gets a row in `_ts_shape_cleanup` in the node's SYSTEM
database before its PUT, and before a cached or listed object is adopted. A
row holds the object key, organization, project, database, database
incarnation, floor offset, epoch and key id, and the seven-day deadline
(`sealed_at` + 7 days). It also holds the stage, reason, stable due time,
attempt, next attempt, parked status, upload lease and consumer lease. Rows
live outside the owning database, so they survive its deletion and R2
lifecycle expiry. Writes are captured like other SYSTEM rows: rewrites
replay them and SYSTEM backups contain them. Peer streams never ship them.

Stages: `live` → `fenced` → `deleted` → `complete`. A manifest lists an
object, and a key release counts it, only while its row is `live`, published
and before its deadline. Activation fences a row at once: a floor raise or
retired handle (`DropHandleAsync`), a deletion fence or an object found
invalid after upload. Overdue `live` rows are fenced when leased. A completed
row stays as a fence until its deadline, then is collected.

Admission is O(1): `_ts_shape_cleanup_usage` and `_ts_shape_cleanup_handles`
count objects, bytes and distinct handles in the same transaction as each
row. Bounds are 65,536 objects, 64 MiB of rows and 4,096 handles. A full
inventory refuses the reservation, so nothing is uploaded. The manifest route
answers `503 unavailable` and `terrabase_shape_cleanup_saturated` is 1. Both
clear once cleanup frees capacity, without a restart. Origin shape reads keep
serving.

Database deletion: every node's catalog convergence activates the inventory
of each `database_purges` record. The replicated purge record also refuses
new reservations for that database on every node. A raw engine deletion
without a purge record only reaches its rows at their deadline.

## Worker

`ShapeCdnPurge` drains on a 15 s timer and on activation:

1. On the first drain after start it clears stale leases and unparks parked
   rows: repaired credentials arrive with a restart.
2. It reserves a 1 MiB memory window and leases at most 4,096 due rows
   (256 bytes each) under a two-minute consumer lease. Rows whose upload
   lease (two minutes, released on publication) is still held wait.
3. It DELETEs fenced objects with up to eight requests at a time. 404 is
   success, so already-expired objects cost nothing. It records `deleted`
   before any purge.
4. It coalesces deleted rows by handle into batches of at most 100 prefixes
   `shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/<handle>/`. It
   takes the shared budget, then sends `POST /zones/4cc466e19478c116fae7dc445a6d0911/purge_cache`.
   Only `success: true` completes a row.

Every captured transaction touches at most 256 rows, so SYSTEM rewrites keep
their bounded final delta.

Retries use jittered exponential backoff of up to one minute and honor
`Retry-After` (delta or date). There are five attempts per hourly cycle; on
exhaustion work waits for the next cycle with up to one minute of jitter.
Any non-429 4xx, including R2 credential or policy refusals, parks the row
durably; nothing is dropped.

Metrics: `terrabase_shape_cleanup_{objects,pending,parked,saturated}`,
`terrabase_shape_cleanup_oldest_age_seconds` (from the stable due time, so
retries never reset it), `terrabase_shape_cleanup_attempts_total{stage,outcome}`
and `terrabase_shape_cdn_purges_total{outcome}`. All labels are bounded.

## Shared purge budget

`ShapePurgeBudget` keeps the next permitted purge time in key
`purge.6cd3f3639e928077593d9b470765efeb` of the NATS KV bucket
`terrabase_cdn_budget`. It updates that key by revision compare-and-set over
ts-orch's role identity (`TERRABASE_NATS_*`: TLS-first mTLS, creds file).
Grants are 12.5 s × the number of serving regions in the peers file apart.
That keeps the five-per-minute Free limit across regions while each region
has its own cell bucket. A 429's `Retry-After` moves the shared key for
every node. A missing identity or bucket fails closed: deletions still run,
purges stay pending and `outcome="unavailable"` counts each refusal.

## Integration boundaries (not claimed)

- ts-orch must grant and create `terrabase_cdn_budget` (file, history 1, no
  TTL) and load the three alerts below. A ready patch exists for the ts-orch
  F6 lane; live hosts run without JetStream today (W0-9), so production
  purges stay pending until N1.
- CI and bench purge callers must join the same key before the account-wide
  five-per-minute claim holds. This repository has none today.
- The inventory is node-local SYSTEM state, captured in that node's backups.
  It is not replicated across nodes. E2's replicated SQL catalog owns that;
  node-loss or failover durability is not claimed.
- Clock skew between nodes eats into the 0.5 s spacing margin; D21's skew
  guard is not part of this.
- F11 key destruction, live R2 lifecycle and CDN variant qualification, and
  F10 benchmarks remain separate.

Alerts (ts-orch `operational-rules.yml`): `TerraBaseShapeCleanupParked`
(parked > 0), `TerraBaseShapeCleanupOverdue` (oldest age > 1 h) and
`TerraBaseShapeCleanupSaturated` (5 min).

## Proof

Fast `ShapeMirrorTests` cover these on real Kestrel servers, Turso files, the
fake R2/Cloudflare transports and a real `nats-server` (operator JWT,
TLS-first mTLS, the role's scoped grants):

- restart after lifecycle expiry, without a bucket listing
- 429 with `Retry-After`
- inventory saturation and recovery
- revoked-credential parking and repair
- owning database deletion with a readable sibling
- three nodes sharing one budget

The CI test image installs nats-server 2.15.2 pinned by SHA-256.

Needs format bump: the SYSTEM schema gains the three `_ts_shape_cleanup*`
tables. No visual change.
