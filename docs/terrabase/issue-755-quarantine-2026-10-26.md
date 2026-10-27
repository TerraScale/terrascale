# Starter allowance concurrency quarantine, 2026-10-26

Issue [#755](https://github.com/TerraScale/terrascale/issues/755) records the
[main Slow failure](https://github.com/TerraScale/terrascale/actions/runs/40416203211/job/121903543239)
on `9bd3521376d638adb8cc09deb803ccf046694bc3`.

The test passed the assertions requiring exactly five successful creates and one
conflict. It failed on the conflict message: the documented transient
`database_limit` refusal, rather than the exhausted-plan message. Escrow peer
calls have a two-second deadline and unavailable holders can produce that
transient response. The log does not distinguish a deadline from another
temporary holder failure.

The test, `EngineManagementService.Escrow.cs` and `EscrowPeers.cs` are unchanged
between that commit and its parent. The platform placement guards introduced by
#748 handle placement updates; this test only creates customer databases.

## Five exact-merge repeats

Execution used `ts-heavy-any` on `mariogk-dev`, `nice -n 19`, Release, .NET SDK
`11.0.100-rc.1.26425.128`, `DOTNET_PROCESSOR_COUNT=8`, ts-dotnet pinned to CI's
`03881aa74a52683112180a5035d7b5ced8d65032`, and the repository's verified native
release. Source was the exact failing merge commit, without the quarantine.

After `nice -n 19 make terrabase-build CONFIGURATION=Release` succeeded with zero
warnings and errors, each repetition ran:

```sh
nice -n 19 make -o terrabase-build terrabase-test-slow CONFIGURATION=Release \
  TEST_ARGS="-- --treenode-filter /*/*/*/Concurrent_creates_across_three_regions_never_exceed_the_starter_allowance --minimum-expected-tests 1 --no-ansi"
```

| Repeat | Passed | Failed | Skipped | Duration |
| --- | ---: | ---: | ---: | --- |
| 1 | 1 | 0 | 0 | 15.688 s |
| 2 | 1 | 0 | 0 | 15.560 s |
| 3 | 1 | 0 | 0 | 20.725 s |
| 4 | 1 | 0 | 0 | 15.518 s |
| 5 | 1 | 0 | 0 | 15.594 s |

Each test executes three rounds of six concurrent requests across three regions.
These isolated passes do not qualify the complete Slow suite under CI load.
Raw logs are retained on `mariogk-dev` at `/tmp/issue755-repeats/{1..5}.log` and
on delta at `~/.cache/platform-all-regions-proof/issue755/{1..5}.log`.

## Temporary quarantine

COS orch explicitly authorized a quarantine after five local repeats. Owner:
**MarioGK / all-regions WRK**. Deadline: **2026-10-27T23:30:00Z**, within 24 hours
of triage. The method's narrow skip automatically stops applying at the deadline.
The test's assertions remain intact, and other escrow tests continue running.

The owner must remove the quarantine and distinguish a transient holder refusal
from a quota breach while preserving the successful-create and converged-count
assertions. This quarantine changes no production behavior or storage format.
