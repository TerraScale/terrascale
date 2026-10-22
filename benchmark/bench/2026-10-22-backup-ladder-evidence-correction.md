# Backup ladder: evidence corrections (2026-10-22)

This correction supersedes the fault-audit and 100M exactness claims in the October 16–20
reports and program summary. Historical measurements were inspected on host `beta` under
`/home/mariogk/tb-backup-20261016/runs/`; no cloud benchmark was rerun for this correction.

| Retained evidence | What it supports |
| --- | --- |
| `1k`, `10k`, `100k`, `1m`: `out/restores-summary.json` | Recorded qualification GREEN 7/7, 20 restores per scale; not independent proof of correct local fault audits |
| `10m/out/restores-summary.json` | Recorded qualification GREEN 3/3, 5 restores; reduced matrix, not full coverage |
| `1k/jobs/audit-fstray-db.json` | Historical fault audit incorrectly used `source.kind=s3`, auditing the original bucket instead of the mutated directory |
| `100m/out/restore-pdr1.json` | Restore returned HTTP 201 in 15,234.46 seconds at requested time `2026-10-19T20:28:36Z` |
| `100m/out/verify-pdr1-dr.json` | Index scan matched 83,455,000 documents, zero missing/extra/mismatched values; overall `passed:false` |

The 100M-stage verifier used `a7.t_ms=1792376788809` (`2026-10-19T00:26:28.809Z`), a stale
C4 oracle time almost 20 hours before the requested restore time. Its 13,447 batches present
after that oracle time cannot be explained as boundary-second inclusion. The retained scan
supports recovered-document evidence, but the failed verification does **not** prove exact
point-in-time correctness. No corrected full verification was run. The planned 100M load
also remained incomplete.

The harness now selects S3 only for the original source marker (`$BASE/s3/src-root`).
Downloaded and mutated copies use directory sources, including during Tigris runs. A focused
shell check covers all three routes. Historical local fault audits must be rerun before
claiming that the corrected fault-audit path passed; the old GREEN summaries do not establish it.

The bounded-copy product fix and its tests already landed on `main` through `e208fcb6`
(`46c17c94` implements P11). PR #337 carries the remaining harness and historical reports.
This correction does not establish current production recovery behavior or fix the reported
single-node crash-recovery failure.
