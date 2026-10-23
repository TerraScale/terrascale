# PITR proof gate G0b at 1M documents, integrated fixes: LOCAL S3 EMULATOR — NOT TIGRIS

Qualification run of the integrated backup and recovery fixes. The bucket is PitrLab's disk-backed S3 emulator
(`s3-serve`) on loopback, so nothing here says anything about Tigris uplink, latency or object semantics. Plan and
vocabulary: [pitr-100m.md](../docs/pitr-100m.md). Earlier 1M run on the pre-fix binary: [2026-10-12-pitr-g0b-1m.md](2026-10-12-pitr-g0b-1m.md).

## Pins

| Item | Value |
| --- | --- |
| TerraBase | `087a8e2b` (local branch `fix/backup-recovery-integration`; every candidate fix, excludes R10a), `/home/mariogk/tb-100m-pitr/bin-087a8e2b/terrabase`, sha256 `e540c0c49ef4ea43cfc6ca8b41be6699215fb5fc5ba99013e84e82accfe60767`, `Production` environment |
| Gates on that commit | fmt, analyzers, Fast 1473/1473, Turso 6/6, Slow backup classes all pass (Tigris class skipped: no credentials) |
| PitrLab | `o6-build5` (s3-serve keeps user metadata, answers S3 ETags; self-check 25/25) |
| Mode | `qualification`, required set `C0 C2 C5 C6 C7 C8 S*` |
| Workload | 1,000,000 documents, `load-a` [0, 400k), `load-b` [400k, 1M), 16 writers, 1,000-document transactions; `u1` 1,000 updates, `d1` 1,100 deletes, `r1` 100 re-creations; seed 20261012 |
| Run dir | `/home/mariogk/tb-100m-pitr/runs/g0b-int-087a8e2b-q1m` (`out/steps.log`, `out/restores-summary.json`, `out/fault-matrix.jsonl`) |
| Host | shared, 12 CPUs, 14 GB RAM, every process at `nice -n 19` |

## Verdict: GREEN

| Area | Outcome |
| --- | --- |
| Required restores (C0, C2, C5, C6, C7, C8, S1, S2, S3) | 9/9 restored (201) and verified exactly on a fresh node from a byte-exact copy; verified again after the target's restart with per-partition digests (`--compare`) |
| All restores | 24: 13 restored, 11 typed refusals `restore_point_unproven` (mid-load and mutation-boundary times with milliseconds inside a commit second), 0 failures, 0 unexpected refusals |
| A-7 (no acknowledged write missing at T) | every verify passed its two-sided check; 0 unresolved batches |
| Source | exact at C8: 999,000 documents, versions equal to the ledger |
| Graceful restart, SIGKILL during writes | reconciles exact (400,000; 678,000 at the kill); 0 retries, 0 ambiguous outcomes, 0 unresolved |
| Shipping after restarts | lag 0, pending 0 within seconds of each restart (the previous attempt stalled here: see below) |
| Bucket audit | `ok` for the database (3 generations, 481 segments, 3 snapshots, 1.03 GB) and `_system` |
| Fault cases (13) | every one typed or exact: fmid, flast, ffinal0, fdelsnap, fdup, fctl, ffinal → 409 `backup_history_incomplete`; fcorrupt, ftrunc, fcorsnap, wrong master key → 409 `backup_unreadable`; fstray → 201 exact; fdr (disaster-recovery point, the window's latest on a fresh node) → 201 exact |

## Throughput and resources

| Phase | docs/s | batch p50 / p99 | Peak RSS (source) |
| --- | --- | --- | --- |
| load-a (400k) | 682 | 23.6 s / 23.9 s | 422 MiB |
| load-b after the SIGKILL (322k) | 619 | 26.1 s / 28.0 s | 2,292 MiB |
| restores on the source (C2, C5, C8) | 84 s, 146 s, 147 s | — | up to 6,696 MiB (C5) |
| restores on a fresh target (C5–C8) | 135–172 s | — | fdr target 5,442 MiB |

Restore memory (P11) is still unbounded: a 1M restore peaks at 5.4–6.7 GB RSS.

## Earlier attempt on `4b750b42` (failed, kept as the defect demonstration)

`runs/g0b-int-4b750b42-q1m`: after the first restart every generation snapshot (222–557 MB, multipart) failed the
multipart completion proof because s3-serve kept no user metadata; the shipper retried for 70 minutes, shipped no segment
and left 20 complete snapshot objects. Fixed by accepting S3's multipart ETag as a second proof and deleting unproven
snapshots (`d8f22113`), and by s3-serve keeping metadata. Tigris must still be checked at G1 for metadata through
CompleteMultipartUpload and for the composite ETag.

## Not proven here

Tigris (G1), 100M documents, three regions and six directed replication paths, whole-node cloud DR, remote durability
of acknowledged writes (RPO is the shipping lag, about 1 s here), several commits within one second.
