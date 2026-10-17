# Continuous backup + PITR on real Tigris — 1K scale (first green run)

Program: the 1K→100M backup ladder on host delta (user task 2026-10-16). This is the first
qualification-green run of the complete `pitr-bench` matrix against **real Tigris object storage**
(all prior evidence in `docs/pitr-100m.md` used the local S3 emulator).

| Item | Value |
| --- | --- |
| Binary | `fix/p11-bounded-copy-memory` @ `2f601f1c`-based worktree + P11 chunked-commit fix; sha256 `0967d044…` (run pins: `runs/1k/out/pins.json`) |
| Harness | `matrix-monitored` (this branch): full cut matrix, Path A + Path B, fault matrix, spend monitor |
| Target | Tigris bucket `ts-pitr-20261017`, org `terrascale-previews`, region `auto`, endpoint `https://t3.storage.dev` |
| Workload | 1,000 documents (~410 B each), load-a 400 / load-b 600, u1/d1/r1 mutations, 8 writers, seed 20261016 |
| Scenario | PLAIN=1 (no SIGKILL; see findings) — Path A on the source node, Path B on an isolated target from a byte-exact copy |

## Verdict

**Qualification GREEN**: required successes **7/7** (C0 C2 C5 C6 C7 C8 S*), 0 failures in 20
restores, 3 expected typed refusals, 12 fault cases (typed refusals or exact restores),
source verified exact at C8 (999/999 documents + global digest), bucket audit `ok` for the
database and `_system` (decrypt + authenticate + contiguity of every object).

## Measurements

| Measure | Value |
| --- | --- |
| Path A restore (source node, reads Tigris directly), C2 / C5 / C8 | 2.19 s / 2.69 s / 2.64 s (201) |
| Path A bounded restores C1 / C4 | 1.95 s / 2.41 s (201, per-stream prefix verified) |
| Path B restore (isolated node, byte-exact copy), C0 / C1 / C2 | 0.65 s / 0.57 s / 0.79 s (201) |
| Stored after the run | 16+ objects, ~48 KB live (database log 1,168,966 B over 21 segments, 1 snapshot, 1 generation; `_system` 26 segments) |
| Ship cadence | one segment per pass (~1 s) while writes ran; cuts' `shipped_ms` advanced every pass |
| Peak node RSS | 297 MiB (all phases ≤ 303 MiB) |
| Monitor projected cost | $0.00014 first month |

## Findings (product, on current `origin/main` + P11 fix)

1. **Single-node crash recovery cannot complete** (blocks the CK/SIGKILL scenario): after SIGKILL,
   `RebuildRecovery` replaces the local database file and waits forever for "a certified refill
   from an intact source" — a replica peer that a single-node deployment does not have. The
   database stays empty (`unknown_collection`) and `/v1/backup` looks healthy while it happens.
   Evidence: `runs/1k-attempt4` (node log: `Replica rebuild remains pending: no certified refill
   from an intact source` every minute; live file replaced by an empty 159,744 B file; the data
   sits in a `.rebuild` scratch). Backup-based node recovery does not exist yet
   (`docs/terrabase/backup.md` "Status of the work"). The ladder runs PLAIN=1 and reports this
   as a finding; a two-node cell or the product's DR path is needed to test the kill scenario.
2. Graceful restart (G1) reconciles exactly (400/400 after restart) — only the kill path breaks.
3. Restore memory at this scale is flat (~300 MiB node RSS) with the P11 chunked-commit fix.

## Harness changes this run needed (this branch)

Internal listener now requires `TERRABASE_PEER_SECRET`/`X-TerraBase-Peer-Credential`; Tigris-mode
audit/download jobs read the bucket through PitrLab's s3 source with the run's env file;
`matrix-monitored` runs the monitor beside the writers; env lines export verbatim (a base64 key's
`=` padding was being eaten). Full list in the branch log.

Run directory: `/home/mariogk/tb-backup-20261016/runs/1k` (evidence, ledger, cuts, audits).
