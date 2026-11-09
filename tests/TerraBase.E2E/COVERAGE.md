# Replication scenario coverage: old suite → design C

## Last runs

<!-- e2e-summary -->
E2E run (local), 2026-10-14T13:19Z on mariogk-dev

- Commit: `3d1491bcf05caa6688f398333c953b8c2bb13379`; working tree: clean
- Stacked on: none (merge base with origin/main `778fba30cbfcc9c4e879578ad869e158480b51c2`)

| Category | Tests |
| --- | ---: |
| actual pass | 2 |
| harness check | 0 |
| expected failure | 0 |
| not supported | 0 |
| skip | 0 |
| fail | 0 |
| total | 2 |

| Test | Outcome | Nodes | Faults | Waits on / reason |
| --- | --- | --- | --- | --- |
| `An_unacknowledged_origin_write_is_never_read_from_a_sealed_chunk_and_dies_with_its_origin` | actual pass | separate OS processes | ProcessKill |  |
| `The_remote_durable_frontier_survives_the_origins_SIGKILL` | actual pass | separate OS processes | ProcessKill |  |
<!-- /e2e-summary -->

