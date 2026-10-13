# Backup and restore drill

The required TerraBase Fast job runs `RestoreDrillTests` automatically. Its local drill writes 24 known documents,
ships an encrypted snapshot and a subsequent CDC segment, deletes the live database file through the real engine,
restores through the management PITR endpoint, and compares all 4096 anti-entropy bucket digests. It also reads every
one of the 25 recovered documents through the client listener. Each test owns its server, data directory and object store;
TUnit's parallel execution remains unchanged.

Run only the drill locally:

```sh
~/.local/bin/ts-heavy make terrabase-test-fast TEST_ARGS='--treenode-filter /*/*/RestoreDrillTests/*'
```

This is an integration test with an in-process S3 transport, not evidence of Tigris or Fly recovery. Timing output is
`RESTORE_DRILL documents=25 backup_ms=... restore_ms=... digest=...` in the test's output, retained by the HTML report.
Measured results belong in `benchmark/bench/`.

## Production acceptance

The runtime catalog restore gate is a separate change in [PR #213](https://github.com/TerraScale/terrascale/pull/213), held for a combined format-6 reset deployment. This PR adds only integration tests and documentation; it changes no serving behavior.

Live work waits for T2's deploy and COS `GO DRILL`, after the replication matrix passes. Retain timestamped Tigris listings, exact images and three machine identities. Write known data, ship it, delete one region's machine and volume, restore tenant data with a fresh catalog from healthy peers, compare all three digests and prove replication in both directions. Inspect daily Fly snapshot scheduling and restore one snapshot into a separate volume. Publish backup, replay, readiness and convergence timings in `benchmark/bench/`, with total recovery time separately.
