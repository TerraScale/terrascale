# P7 tier-failure lane (in-process receipt)

Status: recorded 2026-08-29 at develop post-`538a9ef`. The lane is
`src/storage/lsm/tier.rs::failure_lane` — 3 rows, green under
`cargo test -p terrabase-v2 --lib tier` (13 tier rows total).

## What is proven (against the real LSM + local object store)

The storage-tiering contract §4 failure table:

- **Unavailable object store**: a mover pass under pressure against a
  store whose writes fail answers `MoverOutcome::Failed` with the typed
  `Unavailable` error and a receipt that NAMES THE STAGE — the
  attempted `Demote` decision, the candidate segment, the
  `failed_stage: object_put` reason, zero bytes transferred, and the
  manifest epochs untouched. The NVMe copy stands: nothing is demoted
  behind a failed transfer. (The public seam now carries this too:
  `TierAdminError::MoveFailed(TierMoveRunReceipt)` — previously the
  receipt was lost to the typed error — and the TBEP mapping names the
  segment, policy, manifest epochs, and decision in its detail.)
- **Corrupt object (checksum mismatch)**: after a verified demote, the
  stored bytes are corrupted behind the manifest's back; the read by
  the manifest's checksum address refuses typed `ChecksumMismatch` —
  the corrupt bytes are quarantined by their own address and never
  served. The manifest referenced only the read-after-write-verified
  object.
- **Torn upload**: a partial object written into the store under no
  key any manifest references is unreachable by every verified
  address — manifests are the only source of truth for fallback reads,
  so the orphan is never served and awaits reclamation.

## Honest limits

- The store here is the local content-addressed object store standing
  in for the remote cold tier; live S3 failure classes (404 vs slow vs
  throttled) are recorded unrun — no object-store endpoint runs on
  this host.
- The full §5 fallback order (active-Region QUIC sources, backup
  cluster, typed `ColdTierUnavailable` exhaustion with every source
  named) has its local legs proven; the QUIC/backup legs are the
  replication layer's, exercised in the P5/P7 cluster lanes.
