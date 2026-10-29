# E4a focused integration repeat proof

All five repeats completed: 15 focused invocations, 20 focused cases passed, zero failed or skipped; all invocation and preparation exit codes were 0. The Make target also ran and passed 44 Turso driver cases per invocation (660 total). This is focused remote evidence; CI remains the merge gate.

| Environment | Actual value |
| --- | --- |
| Host | `delta` |
| TerraScale SHA, before and after | `8e359138a24b75b4bfcbece36eb45ddf90e83a17` |
| ts-dotnet SHA, before and after | `bcd20603f11f3aa54dc54cec6e410b4fe9e24151` |
| TsDotnetRoot | `/home/mariogk/Projects/TerraScale/ts-dotnet` |
| Managed checkout | `/home/mariogk/remote-builds/terrascale/refactor-e4a-delete-hrana-wire-webhooks-ab91ece8fd4b88491f92f89c686ba40c0fce451ab742e462094c9ba7d58387bf` |
| Configuration | Release |
| Runtime | NixOS 26.05, linux-x64, .NET 11.0.0-rc.1.26425.128, TUnit 1.73.19.0 |
| Start UTC, after managed clone | `2026-10-29T16:49:05Z` |
| End UTC | `2026-10-29T16:56:25Z` |
| Source working trees | Clean before and after for TerraScale and ts-dotnet; no source edits |
| Managed-job and SSH transport exit | 0 |
| Preserved logs on delta and local copy | `/tmp/e4a-repeat-proof-20261029T164527Z` |
| Full session log on delta and local | `/tmp/e4a-repeat-proof-20261029T164527Z/session.log` |
| Full local transport log | `/tmp/e4a-repeat-proof-20261029T164527Z-transport.log` |

Preparation ran first, in order, with the explicit TsDotnetRoot environment variable:

| Preparation | Wall duration (s) | Exit | Full log (delta and local) |
| --- | ---: | ---: | --- |
| `nice -n 19 bash scripts/vendor/download-turso-native.sh` | 2.041 | 0 | `/tmp/e4a-repeat-proof-20261029T164527Z/prep-0-turso-native.log` |
| `nice -n 19 make restore` | 9.318 | 0 | `/tmp/e4a-repeat-proof-20261029T164527Z/prep-0-restore.log` |

Each test row is a separate invocation of:

```sh
nice -n 19 make -o restore terrabase-test-fast CONFIGURATION=Release TEST_ARGS='--treenode-filter <filter> --minimum-expected-tests 1'
```

The entire preparation and repeat sequence ran in one automatically selected managed checkout through `ssh delta /home/mariogk/.local/bin/ts-heavy-any bash -c '<sequence>' --repo terrascale --branch refactor/e4a-delete-hrana-wire-webhooks`. Existing Make target parallelism was unchanged.

| Filter label | Exact filter |
| --- | --- |
| active-streams | `/*/*/BoundedShutdownTests/Active_changes_and_peer_streams_stop_before_storage_closes_within_budget*` |
| in-progress-write | `/*/*/BoundedShutdownTests/A_changes_write_in_progress_at_shutdown_completes_before_clean_eof` |
| tenant-isolation | `/*/*/TenantIsolationTests/A_tenant_reaches_nothing_of_another_tenant_through_any_client_route` |

Wall duration includes both builds, the driver suite, and the focused Fast invocation. Focused duration comes directly from the Fast runner summary. Counts are passed/failed/skipped.

| Repeat | Filter | Focused P/F/S | Focused duration | Driver P/F/S | Wall duration (s) | Exit | Full log (delta and local) |
| ---: | --- | --- | --- | --- | ---: | ---: | --- |
| 1 | active-streams | 2/0/0 | 8s 432ms | 44/0/0 | 59.613 | 0 | `/tmp/e4a-repeat-proof-20261029T164527Z/test-1-active-streams.log` |
| 1 | in-progress-write | 1/0/0 | 4s 026ms | 44/0/0 | 25.596 | 0 | `/tmp/e4a-repeat-proof-20261029T164527Z/test-1-in-progress-write.log` |
| 1 | tenant-isolation | 1/0/0 | 2s 952ms | 44/0/0 | 28.401 | 0 | `/tmp/e4a-repeat-proof-20261029T164527Z/test-1-tenant-isolation.log` |
| 2 | active-streams | 2/0/0 | 8s 304ms | 44/0/0 | 33.015 | 0 | `/tmp/e4a-repeat-proof-20261029T164527Z/test-2-active-streams.log` |
| 2 | in-progress-write | 1/0/0 | 1s 632ms | 44/0/0 | 19.810 | 0 | `/tmp/e4a-repeat-proof-20261029T164527Z/test-2-in-progress-write.log` |
| 2 | tenant-isolation | 1/0/0 | 4s 464ms | 44/0/0 | 23.324 | 0 | `/tmp/e4a-repeat-proof-20261029T164527Z/test-2-tenant-isolation.log` |
| 3 | active-streams | 2/0/0 | 9s 173ms | 44/0/0 | 31.178 | 0 | `/tmp/e4a-repeat-proof-20261029T164527Z/test-3-active-streams.log` |
| 3 | in-progress-write | 1/0/0 | 1s 681ms | 44/0/0 | 20.839 | 0 | `/tmp/e4a-repeat-proof-20261029T164527Z/test-3-in-progress-write.log` |
| 3 | tenant-isolation | 1/0/0 | 4s 040ms | 44/0/0 | 23.948 | 0 | `/tmp/e4a-repeat-proof-20261029T164527Z/test-3-tenant-isolation.log` |
| 4 | active-streams | 2/0/0 | 13s 541ms | 44/0/0 | 31.310 | 0 | `/tmp/e4a-repeat-proof-20261029T164527Z/test-4-active-streams.log` |
| 4 | in-progress-write | 1/0/0 | 1s 457ms | 44/0/0 | 23.898 | 0 | `/tmp/e4a-repeat-proof-20261029T164527Z/test-4-in-progress-write.log` |
| 4 | tenant-isolation | 1/0/0 | 3s 091ms | 44/0/0 | 31.486 | 0 | `/tmp/e4a-repeat-proof-20261029T164527Z/test-4-tenant-isolation.log` |
| 5 | active-streams | 2/0/0 | 8s 424ms | 44/0/0 | 30.474 | 0 | `/tmp/e4a-repeat-proof-20261029T164527Z/test-5-active-streams.log` |
| 5 | in-progress-write | 1/0/0 | 1s 563ms | 44/0/0 | 20.721 | 0 | `/tmp/e4a-repeat-proof-20261029T164527Z/test-5-in-progress-write.log` |
| 5 | tenant-isolation | 1/0/0 | 4s 236ms | 44/0/0 | 25.651 | 0 | `/tmp/e4a-repeat-proof-20261029T164527Z/test-5-tenant-isolation.log` |

All 30 builds reported success with zero warnings and zero errors. No focused invocation returned zero tests. Both active-stream argument variants ran each time. No test failure was observed; no retries, skips, test weakening, or source fixes were made.
