---
title: Restore drill evidence — 2026-10-23
description: Measured snapshot restore component timings, integrity results, and limits of the 2026-10-23 live restore drill.
---

# Restore drill evidence — 2026-10-23

At **2026-10-23T11:55:40.6790912Z**, a live restore drill selected the newest observed production database snapshot, dated **2026-10-23T11:33:29.764Z**, from a real Tigris object-storage bucket. The drill independently downloaded and decrypted a reference, then used the restore engine to reconstruct a separate disposable copy.

This demonstrates a **snapshot-only restore component** with matching captured logical state and successful integrity checks. It does not establish full application or cell service recovery time, measured write-loss recovery point, or a full disaster-recovery SLA.

## Measured component timings

| Measurement | Observed value | Scope |
| --- | --- | --- |
| Download and restore (`download_restore_ms`) | 238.1043 ms (0.2381043 seconds) | Includes download, reconstruction, and integrity checks for the disposable restore. |
| Independent reference download (`reference_download_ms`) | 1557.2863 ms | Separately measured reference download; not included in the restore component timing. |
| Observed snapshot age | 1330915.0912 ms (22 minutes, 10.9150912 seconds) | Age of the selected snapshot at the drill timestamp; **not measured write-loss RPO**. |
| Historical backup upload (`backup_ms`) | Unknown (`null`) | The original backup upload was not timed. |

These component timers do not cover the complete drill: checksum comparison, source delivery, and other work outside their stated boundaries are excluded. The 0.2381043-second measurement is **not full service RTO**.

## Verified results

| Check | Result |
| --- | --- |
| Same-cut logical-state digest match (`digest_match`) | Passed |
| Encrypted authentication | Passed |
| Database integrity | Passed |
| Cleanup | Passed; the disposable test target and credential handoff were verified deleted. |
| Source configuration | Unchanged |
| Object-store write rejection self-check | Passed; writes were rejected. |

The same-cut digest comparison retains the captured logical state and excludes uncaptured bookkeeping. It verifies agreement with the independently downloaded reference at that captured point; it does not prove recovery of writes after the snapshot. Digest values and source identifiers are omitted from this public evidence.

The public API, authentication, and dashboard were ready after cleanup. This post-drill readiness check was not a timed service-recovery measurement.

## Coverage and remaining limits

The target and reached positions were both **0**. There was no previous snapshot in the same generation, so **WAL replay was not exercised** (`wal_exercised=false`). Coverage is snapshot-only.

Latest-WAL point-in-time recovery and failover remain unproven by this drill. Full application or cell service RTO and write-loss RPO were not measured. These results support no current full disaster-recovery SLA claim.
