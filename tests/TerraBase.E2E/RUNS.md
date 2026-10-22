# E2E runs

Full summaries of the repeat runs `COVERAGE.md` lists, as `make terrabase-e2e` printed them. Newest last. CI's
`terrabase-check` validates every block (`scripts/terrabase/e2e-summary.sh check`).

## FR-25 process durability scenarios (working tree)

<!-- e2e-summary -->
E2E run (local), 2026-10-14T06:02Z on delta

- Commit: `993927ce7ecf8f9b889d24d4d1d21ec96eaf9416`; working tree: 2541e8771da12bfcd4c86c5dfa0eb8abdec26b4434cd538f11703bbb1347211c
- Stacked on: none (merge base with origin/main `993927ce7ecf8f9b889d24d4d1d21ec96eaf9416`)

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


<!-- e2e-summary -->
E2E run (local), 2026-10-14T06:46Z on delta

- Commit: `993927ce7ecf8f9b889d24d4d1d21ec96eaf9416`; working tree: ea3ec095bed736912489d31f2144b910779a1e7b351c4940d0f4190bc284f485
- Stacked on: none (merge base with origin/main `993927ce7ecf8f9b889d24d4d1d21ec96eaf9416`)

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


## FR-25 process durability after #286 rebase (committed tree)

<!-- e2e-summary -->
E2E run (local), 2026-10-14T08:22Z on delta

- Commit: `f280e7ae3bee9b2d955d1e924f83c6b135c2b08c`; working tree: clean
- Stacked on: none (merge base with origin/main `0eac37a89c7217d75275bfb543ef92d4680939b7`)

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


## FR-25 process durability after test isolation (committed tree)

<!-- e2e-summary -->
E2E run (local), 2026-10-14T08:29Z on delta

- Commit: `89f54c3e5f94f414b94fcf8e83b1c223888061c7`; working tree: clean
- Stacked on: none (merge base with origin/main `0eac37a89c7217d75275bfb543ef92d4680939b7`)

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


## FR-25 HTTP integration and bounded proof wait (remote committed tree)

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
