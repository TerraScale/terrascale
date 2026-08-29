# P0 contract: storage tiers, manifest, mover, and S3 failure behavior

Status: frozen by the P0 contract pass, 2026-08-28. Governs P1
implementation and AC-11 recovery evidence. Parents: ADR 0016 (LSM
organization, tier split), ADR 0017 (placement, mover, fallback).

## 1. Storage-tier manifest

`TierManifest` is the versioned, quorum-published authority for which
immutable segments live where.

```text
TierManifest {
  manifest_id:            UUIDv7          // unique per publication
  epoch:                  u64             // publication epoch, monotonic
  cluster_id, shard_id:   identity
  home_region:            RegionId        // the one verified S3 Region
  objects: [ {
    segment_id, key_range {start, end},
    object_key,            // checksum-addressed, content-derived
    sha256, byte_len,
    encryption_epoch,      // key epoch identifier only, never material
    uploaded_at_epoch_ms, verified: { read_after_write: bool, checksum: bool }
  } ],
  nvme_resident: [ { segment_id, key_range } ],   // reclaimed-after-reference view
  fallback_sources: [ { region | backup_cluster_id, verified: bool } ],
  previous_manifest_id:   Option<UUIDv7>
}
```

Rules:

- A manifest never references an object until checksum AND
  read-after-write verification pass on that object.
- Publication: the engine proposes `TierManifest e+1` only after every
  object it names is uploaded and verified; the manifest becomes active
  when `RegionalQuorum` durably accepts it (ledger-recorded). Until
  then the previous epoch stays active.
- Before the last active NVMe copy of a range is reclaimed, the active
  manifest names at least one verified active-Region or backup-cluster
  fallback source for it. One verified home S3 Region suffices for
  object placement (ADR 0017 D-1); the fallback need not be S3.
- Object keys are checksum-addressed and immutable: a replaced range is
  a new object; old objects are reclaimed only after every manifest,
  snapshot, backup, replication, CRDT-history, and shadow-retention pin
  releases them.
- Fencing: a stale Member may not publish epoch `e` after `e+1` is
  active; the quorum acceptance records the predecessor id and rejects
  non-chained proposals.

## 2. Tier Move Policy (versioned, explainable)

Policy inherits cluster -> tenant -> database -> collection; the most
specific wins. A policy may combine:

- tenant Tier-1 quota and reservation (bytes);
- creation age, last-access age;
- decayed read frequency, decayed write frequency (separate decays);
- scan locality (range touched by range-scans);
- refetch cost (estimated bytes + requests to make the range hot again);
- immutable range size; explicit hot/cold pins and exclusions;
- schedules (window allow/deny);
- minimum residence time (per move direction) and promotion threshold;
- adaptive NVMe headroom (node reserve target);
- budgets: bytes/sec, requests/sec, concurrency, CPU, memory, window.

Every policy is versioned (`policy_id`, `version`). For any document or
range, the engine can explain the effective policy and the concrete
reasons a move was chosen or rejected (typed explain operation; no
opaque model).

### Control loop (fairness contract)

- The mover forecasts, per tenant: working-set size, growth rate, and
  time to exhaust the tenant's Tier-1 budget; and per node: the reserve
  needed for WAL, foreground writes, manifests, and recovery. Demotion
  starts when the forecast crosses the tenant's horizon — not at a fixed
  node-wide fullness watermark.
- A tenant above its fair share supplies its own cold candidates first;
  it can never evict another tenant's reserved hot working set.
- Weighted fair queues govern compaction, upload, and cache capacity.
  Per-tenant cache partitions and backpressure engage before the system
  reserve is consumed.
- Hysteresis: promotion/demotion of the same range within the minimum
  residence time is refused; repeated ping-pong triggers a hold.
- Mixed-temperature segments: a bounded compaction may split by key
  range and temperature only when predicted NVMe savings exceed the
  rewrite cost (recorded in the receipt).

## 3. Tier Move Receipt (schema)

Every mover run (including dry runs) emits a receipt:

```text
TierMoveReceipt {
  receipt_id, started_at, finished_at,
  policy: { policy_id, version },          // exact effective policy
  decision: promote | demote | split | none | refused,
  reasons: [ ... ],                        // ordered, machine-readable
  source_tier, target_tier,
  segment_ids: [ ... ], bytes, requests,
  manifest_before, manifest_after,         // ids; publication outcome
  cache_effect: { entries_invalidated, entries_filled },
  budgets: { bytes_used, requests_used, duration_ms },
  failures: [ { stage, error, retry_state } ],
  dry_run: bool,
}
```

- Operator controls are typed: pause, resume, cancel, dry-run, explain,
  inspect. A policy change never strands an in-flight object or
  bypasses manifest fencing.
- Receipts are per-run artifacts under `evidence/` lanes and are the
  proof unit for AC-11 tiering gates.

## 4. S3 failure contract

| Condition | Required behavior |
| --- | --- |
| object missing / 404 | treat as unavailable; go to fallback order (§5); never return empty result |
| object corrupt (checksum mismatch) | quarantine the entry, alert, try fallback; never serve |
| partially uploaded (torn) | impossible to be referenced: manifests reference only verified objects; a torn upload is orphaned and reclaimed |
| stale manifest | epoch fencing rejects; re-converge from the active manifest |
| permission denied | typed configuration error surfaced to administration; reads follow fallback |
| home Region unavailable | transparent fallback (§5) |
| all sources exhausted | typed `ColdTierUnavailable` error after bounded retries; hot tier never corrupted |
| slow object store | bounded latency budgets; requests coalesce; per-tenant budgets enforced |

An object-store outage never turns into an empty read result and never
writes partial state back to the hot tier.

## 5. Cold-read fallback order (frozen)

1. Local NVMe cache.
2. Home S3 Region (bounded retries, coalesced requests).
3. Every eligible active Region, over authenticated QUIC, in a
   deterministic order derived from topology (manifest identity +
   checksum verified before cache publication).
4. `backup` cluster — only after 1-3 all fail.
5. Typed `ColdTierUnavailable` availability error; the receipt names
   every source tried and the failure class.

NATS may locate a source; it never transports the range.

## 6. SSD-usage accounting (100 GB program budget)

This contract is recorded here and in the workspace charter; the
program budget for TerraBaseV2 work on this host is 100 GB
(100,000,000,000 allocated bytes) covering the repository, Rust build
outputs, WAL/segment data, test and benchmark datasets, caches,
temporary files, receipts, and any local S3 emulator attributable to
TerraBaseV2.

- Every storage-intensive task records SSD usage before it starts,
  monitors while running, and records after cleanup (a three-line
  record in the task's evidence note).
- At 80 GB: warn; stop optional cache or dataset growth.
- At 90 GB: refuse to start another write-intensive task; stop the
  current task at its next safe checkpoint.
- Each task reserves its worst-case next batch against the remaining
  budget so a checkpoint cannot overshoot the ceiling. 100 GB is an
  absolute ceiling, not a cleanup trigger.
- Cleanup touches only TerraBaseV2-owned paths; retained evidence,
  user files, sibling repositories, and unrelated caches are never
  deleted.
