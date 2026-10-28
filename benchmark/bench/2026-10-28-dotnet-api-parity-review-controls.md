# .NET parity review corrections, 2026-10-28

Code `137267446`, layered on runner `9b62c7a26`, keeps an unexpected worker failure rejected even when every observed header is successful. Cancellation after the final cell cannot yield an accepted outcome; the outcome distinguishes signals from the run limit. Credentials are bounded to 1–4096 bytes with exactly two required members and no duplicates; empty and zero-length special files are refused before opening. A path replacement between validation and open remains outside this guard.

| Delta qualification | Result |
|---|---|
| SDK-preparer fixtures | 14 passed |
| Locked restore and Release build | Passed; 0 warnings/errors |
| Three-project format checks | Passed |
| Existing and new parallel controls | 194 passed, 0 failed/skipped |
| SDK tracked diff and dependency lock diff | Empty |
| Production requests, credentials or tenants | None |

Command: `TS_HEAVY_HOST=delta ts-heavy-any nice -n 19 bash benchmark/scripts/dotnet-parity-review-controls-20261028.sh b --repo terrascale --branch fix/api-dotnet-parity-review-fixes`. The script runs existing `make check`. SDK pin: `36d114877a69d18fba33b27ad362bd3e68e9e7ff`.

Artifacts on delta: `~/remote-builds/terrascale/fix-api-dotnet-parity-review-fixes-0630c501036ba9848ae83f21364dc389f33097015bc6b3f8ff27ae66d9c6d822/benchmark/bench/dotnet-parity-review-controls-b/`. `check.log` SHA-256: `4394ab3cfe19195ca979c3310d6f48985f8b7486e6a11db53418585da9f39eee`.

Attempt a (`c68d22761`) built and formatted successfully, then failed 3/194 tests: whole-run controls sometimes exhausted their 2 ms wall-clock windows before workers started. Their fake clock now advances on observation, independent of scheduling; they still check every cell and run in parallel. Separate manual-clock controls retain the full timing assertions. Live timing is unchanged. Both failed and passing artifacts are retained.

Late-cancellation controls fire the run timer or cancel during cleanup after all 108 cells; they do not deliver real OS signals. Fake handlers do not verify TLS/ALPN. This establishes offline rejection and lifecycle behavior, not live parity, remote erasure or any speedup. Required CI remains the merge gate.
