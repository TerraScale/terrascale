# Change-feed shutdown cancellation ordering

PR #524's CI run [40034922011](https://github.com/TerraScale/terrascale/actions/runs/40034922011/job/120694584771) failed only `Active_changes_and_peer_streams_stop_before_storage_closes_within_budget(False)` (2556 other Fast cases passed). The required `test` job reflected that failure.

Shared feed producers observe `ApplicationStopping` directly. Their cancellation callbacks can complete a page before a stream's earlier linked-token callback executes. The stream then receives `OperationCanceledException` while its linked token still appears live, propagates the exception after headers, and aborts the response. The handler now also checks the original host shutdown token. Unexpected cancellation while the host and stream remain live still propagates.

The new real-listener regression holds cancellation callback dispatch before the linked stream token, commits a document to wake the feed, and requires a clean response end and complete storage cleanup. Restoring the original catch filter reproduces `HttpIOException: The response ended prematurely` (1 failure, 0 passes). Restoring the fix passes.

Five repeats of all six `BoundedShutdownTests` cases ran on `mariogk-dev` at `nice -n 19`, using Release. The first repeat rebuilt; subsequent repeats used `--no-build`.

```sh
nice -n 19 dotnet run --project tests/TerraBase.Fast --configuration Release --no-restore -- \
  --treenode-filter '/*/*/BoundedShutdownTests/*' --minimum-expected-tests 6 --no-ansi
```

| Repeat | Passed | Failed | Skipped | Duration |
| --- | --- | --- | --- | --- |
| 1 | 6 | 0 | 0 | 10s 847ms |
| 2 | 6 | 0 | 0 | 10s 573ms |
| 3 | 6 | 0 | 0 | 10s 517ms |
| 4 | 6 | 0 | 0 | 10s 325ms |
| 5 | 6 | 0 | 0 | 10s 377ms |

No shutdown budget, grace interval, rollback, storage admission, or existing assertion changed. The regression uses its own test server, files, and callback gate. No live service, production data, or credential was accessed. Exact-head CI and the subsequent production recovery drill remain required.
