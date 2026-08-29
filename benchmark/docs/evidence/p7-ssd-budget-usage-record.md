# P7 SSD-usage accounting record (standing)

Status: recorded 2026-08-29 by the P7 pass at develop post-`1c2c197`,
under the storage-tiering contract §6 program budget.

## Three-line record

- Before this session's P7 lanes (2026-08-29 start): not separately
  measured; the charter's review baseline was 13.10 GB allocated at
  commit `911197e` (2026-08-28), all TerraBaseV2-owned paths.
- During/after the P7 qualification, tier-failure, and disposition
  lanes (2026-08-29): `du -sx --block-size=1 TerraBaseV2` reports
  **25.25 GB** allocated (24.46 GB of it Rust `target/` build outputs
  from the P1-P7 landing batteries; the remainder repository, WAL and
  segment test artifacts under tempdirs are cleaned per-test).
- After cleanup: no action taken — 25.25 GB sits below the 80 GB warn
  line with 74.75 GB of headroom; retained evidence and receipts are
  never deleted, and cleanup touched only TerraBaseV2-owned tempdirs.

## Reading

The budget gates remain green: no warning (80 GB), no admission stop
(90 GB), no ceiling pressure (100 GB absolute). The growth over the
charter baseline is Rust build artifacts from this session's test
batteries; `cargo clean` remains the recovery lever if a future
storage-intensive task approaches the warn line — each such task
records its own before/during/after lines here.
