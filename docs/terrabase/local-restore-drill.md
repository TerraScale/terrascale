# Backup and restore drill

The required TerraBase Fast job runs `RestoreDrillTests` automatically. Its local drill writes 24 known documents,
ships an encrypted snapshot and a subsequent CDC segment, deletes the live database file through the real engine,
restores through the management PITR endpoint, and compares a SHA-256 digest of every document's id, version and JSON.
It also reads every one of the 25 recovered documents through the client listener. Each test owns its server, data directory and object store;
TUnit's parallel execution remains unchanged.

Run only the drill locally:

```sh
nice -n 19 make terrabase-test-fast TEST_ARGS='--treenode-filter /*/*/RestoreDrillTests/*'
```

This is an integration test with an in-process S3 transport, not evidence of production Tigris or OCI recovery. Timing output is
`RESTORE_DRILL documents=25 backup_ms=... restore_ms=... digest=...` in the test's output, retained by the HTML report.
Measured results belong in `benchmark/bench/`.

## Production acceptance

The runtime catalog restore gate is a separate change in
[PR #213](https://github.com/TerraScale/terrascale/pull/213). Local drill coverage
changes no serving behavior and does not establish production recovery.

Production recovery needs a reviewed drill with an explicit destructive target. Retain timestamped Tigris listings,
the exact image and the node identity. Write known data, ship it, recreate the approved target's machine and volume,
restore tenant data and compare the digest. Each database has one serving region, so there is no peer to restore
from or replicate to. Publish backup, replay and readiness timings in `benchmark/bench/`, with total recovery time
separately. This is an acceptance requirement, not an instruction to run the drill.

## Backup qualification semantics

`BackupQualification.Failures` separates supported restore cuts from fault cuts.
A frozen plan must include a nonempty supported set. Every supported cut must
restore the acknowledged count, canonical digest, document versions, source identity
and placement. A refusal on such a cut is a failure. Fault cuts may instead return
the exact typed refusal and HTTP status prescribed by the plan; arbitrary errors
and silent partial results fail. A run in which every cut refuses is never a
working-restore proof. Local S3-transport coverage is not live Tigris evidence.
