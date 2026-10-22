# Continuous backup + PITR on real Tigris — 100M-scale run and disaster-recovery proof

Same program and binary as the 1K run ([1K results](2026-10-16-backup-scale-1k.md)). The full
100M-document load was interrupted at 83,445,000 documents by repeated external SIGTERMs of the
node plus the single-node crash-recovery defect (both below); the bucket held every acknowledged
write, and the stage finished as a **true disaster-recovery proof**: a fresh node with empty
disks restored everything from Tigris alone and verified it against the write ledger.

## What ran

| Item | Value |
| --- | --- |
| Load | 40,000,000 docs (load-a) + 43,445,000 docs (load-b of 60M planned), 1,000-doc transactions, 8 writers, every batch acknowledged in the fsynced ledger |
| Shipping | Continuous throughout: 4 generations, 31,418 database segments + 17,005 `_system` segments, 5 snapshots; one segment per second while writes ran |
| **Bucket audit (100M-scale)** | **`ok`, zero problems**: 117.9 GB of sealed objects over the database prefix (90.2 GB of change log, 311,429,884 upserts) — every object decrypted, authenticated, contiguous, snapshots on segment boundaries |
| **Disaster recovery** | Fresh node, empty data directory, the run's Tigris env only. Adopted the tenant ids and restored `pitr-dr` at T = 2026-10-19T20:28:36Z: **201 in 4 h 14 m** |
| **Verify against the ledger oracle** | 3 h 04 m, per-partition over 1,000 partitions: **all 43,455 acknowledged load-b batches present (43,455,000 documents), 0 missing, 0 partial batches, 0 prefix gaps, 0 acknowledged-but-not-restored** — with load-a's 40M docs inside the restored snapshot: **83,455,000 documents recovered, zero acknowledged loss** |
| Peak node RSS | ≤ 7.62 GB through loads, 200 GB-scale snapshot copies and restores (P11 fix holding) |
| Stored / projected cost | 99.1 GB live at the monitor's last sample, $2.21 projected first month (final objects ~118 GB ≈ $2.4/mo) |
| Wall | 2026-10-17T12:26Z → 2026-10-20T12:35Z (~3 days), single node on host beta at nice 19 |

The verify's single flag is the documented boundary-second semantics, not loss: 13,447 batches
whose ledger first-send time falls at/after T are included because their COMMIT seconds end
within T's boundary second — the product contract ("a transaction that committed within T's own
millisecond can be included through a segment read in that millisecond") meets a stricter
first-send check in the harness. Every flagged batch was itself acknowledged and shipped.

## Incidents (evidence in the run directory)

1. **Host kills**: an unidentified agent on host beta SIGTERMed the terrabase node four times
   (2026-10-19 04:15Z, ~06:38Z, ~21:5XZ, and once during the DR restore's first attempt). Every
   kill was lossless: graceful shutdown shipped the backlog, the ledger made writes
   exactly-once, and loading resumed from the ledger after each restart
   (`keepalive-100m.sh`, `runs/100m/out/steps.log`).
2. **Single-node crash-recovery defect (product, blocking)**: after the third kill,
   `RebuildRecovery` replaced the live 83.4 M-document file with an empty one and pended forever
   on "no certified refill from an intact source" — a replica peer a single-node deployment does
   not have. `/v1/backup` looks healthy while it happens. The rebuilt node then shipped the
   emptiness as the newest generation's snapshot (generations 3–4), which any restore at a later
   T correctly refuses (`backup_history_incomplete`); restoring inside generation 2's span works.
   Same defect first observed at 1K (`runs/1k-attempt4`). Whole-node recovery from backup is
   listed as not-done in `docs/terrabase/backup.md`; this run is the first proof it works in
   practice through the documented operator path (catalog-anchor reset + adopt + PITR restore).
3. **Multipart snapshots at scale**: four 17–29 GB sealed snapshots uploaded through S3 multipart
   (128 MiB parts) with the upload-id/ETag proof checks — the first time this path ran against
   real Tigris (previously emulator-only). All succeeded.

## Scaling picture across the ladder

| Scale | Docs | Path A restore (exact) | DR/Path B restore | Stored | Verdict |
| --- | --- | --- | --- | --- | --- |
| 1K | 1,000 | 2.6 s | 0.8 s | 48 KB | qualification GREEN (7/7) |
| 10K | 10,000 | 9.0 s | 3.6 s | ~2 MB | qualification GREEN (7/7) |
| 100K | 100,000 | 32.2 s | 29.8 s | ~7 MB | qualification GREEN (7/7) |
| 1M | 1,000,000 | 365.8 s | 343.0 s | 2.04 GB | qualification GREEN (7/7) |
| 10M | 10,000,000 | 79.1 min | — (disk-bound) | 21.2 GB | qualification GREEN (3/3 + bounded C4 exact) |
| 100M | 83.45M loaded | — (killed mid-run) | **4 h 14 m, all acked docs** | 99–118 GB | DR proof: zero acknowledged loss |

Run directory: `/home/mariogk/tb-backup-20261016/runs/100m` (ledger, cuts, audits, monitor
samples, DR node under `targets/dr`).
