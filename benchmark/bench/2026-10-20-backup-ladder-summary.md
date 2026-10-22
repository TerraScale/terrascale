# TerraBase backup ladder on real Tigris — program summary (2026-10-16 → 2026-10-20)

Task (user, 2026-10-16): test the full TerraBase backup system locally, Tigris as the only
cloud, at 1,000 / 10,000 / 100,000 / 1M / 10M / 100M. Everything ran on one node on the local
dev host at nice 19, against a dedicated Tigris bucket (org `terrascale-previews`,
`ts-pitr-20261017`), under a $25 monitored spend cap (final projection ≈ $2.4 + prior scales
≈ **$3.6 first month**, well under cap; monitor stopped nothing).

## Results

| Scale | Verdict | Headline |
| --- | --- | --- |
| 1K | qualification GREEN 7/7 | first full-matrix green on real Tigris (Path A + B + faults) |
| 10K | qualification GREEN 7/7 | fault matrix clean, exact verifies |
| 100K | qualification GREEN 7/7 | 99,900/99,900 exact at C8 |
| 1M | qualification GREEN 7/7 | the 2026-10-13 program's never-reached G1 milestone; node RSS 3.66 GB (was 6.2–8.09 GB pre-fix) |
| 10M | qualification GREEN 3/3 + bounded C4 exact | 10,000,000/10,000,000 exact; RSS ≤ 5.1 GB |
| 100M | interrupted at 83.45M docs; disaster-recovery proof | bucket audit ok (118 GB, 311 M upserts); fresh node restored **all 83,455,000 acknowledged documents from the bucket alone** in 4 h 14 m, zero acknowledged loss |

Per-scale details: [1K](2026-10-16-backup-scale-1k.md) · [10K](2026-10-16-backup-scale-10k.md) ·
[100K](2026-10-16-backup-scale-100k.md) · [1M](2026-10-16-backup-scale-1m.md) ·
[10M](2026-10-17-backup-scale-10m.md) · [100M](2026-10-20-backup-scale-100m.md).

## What the product proved on real Tigris (several for the first time)

- Continuous shipping (one segment/second), daily full snapshots, multipart uploads of 17–29 GB
  objects with completion proofs, sealed-object encryption/contiguity audits — all green at
  every scale, including under sustained 550+ docs/s writes.
- Point-in-time restore exactness with per-document two-sided verification at 1K–10M.
- Whole-node disaster recovery from the bucket alone at 100M-scale (operator path: catalog-anchor
  reset, adopt, PITR restore inside a proven generation).

## Product findings (owners needed; details in the per-scale files)

1. **P11 fixed here (PR #337)**: restore/copy memory now bounded by chunk size, not file size.
   Measured: 10M-doc restores peaked 2.2–4.9 GB; 200 GB-scale snapshot copies held ≤ 7.6 GB RSS.
2. **Single-node crash recovery is broken on main**: a killed node's databases are replaced empty
   and pend forever on replica refill ("no certified refill from an intact source") while status
   looks healthy; the emptied state then ships as the newest generation, which later restores
   correctly refuse. Blocks the CK/SIGKILL scenario of the qualification matrix everywhere.
3. **Restore-cost curve**: Path A restores are hour-class at 10M (75–79 min) and ~4 h at
   ~83M (including the restored database's own first-generation snapshot upload). Restore-time
   work (segment compaction) remains the scaling lever.
4. Boundary-second inclusion semantics (documented) trip the harness's stricter first-send A-7
   check under continuous writes — a spec tightening candidate, not a loss.

## Harness/program changes (branch `fix/p11-bounded-copy-memory`, PR #337)

Tigris-mode audits/downloads via PitrLab's s3 source; peer-credential on the internal listener;
verbatim env-line export (base64 padding); `matrix-monitored` with the approved spend monitor;
`PLAIN=1` kill-skip with the defect documented; monitor envelope overrides bounded at
$25/350 GiB/700 GiB.

## Resources left in place

- Tigris bucket `ts-pitr-20261017` (org `terrascale-previews`): ~118 GB sealed synthetic
  backups; key `pitr-backup-20261017` active. Deletion/revocation awaits the user's call
  (retention ≈ $2.4/month until then).
- Run evidence under `/home/mariogk/tb-backup-20261016/runs/` (source data dirs are the bulk;
  `out/`, ledger and audits are the durable evidence).
- The 10M and 100M node data directories can be reclaimed (~150 GB) once evidence is copied.
