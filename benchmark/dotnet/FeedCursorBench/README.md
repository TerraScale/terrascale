# Feed cursor prefix benchmark

This standalone executable references the real TerraBase engine. It measures proof creation and cross-machine
resume validation at the consumed head of a million-event feed, without HTTP or document reads.

1. Build with the repository SDK and a sibling ts-dotnet checkout:

   ```sh
   nice -n 19 dotnet build benchmark/dotnet/FeedCursorBench -c Release -p:TsDotnetRoot=/absolute/path/to/ts-dotnet
   ```

2. Seed a fresh, disposable fixture directory outside the measured cgroup:

   ```sh
   nice -n 19 dotnet benchmark/dotnet/FeedCursorBench/bin/Release/net11.0/FeedCursorBench.dll seed /tmp/feed-prefix-fixture
   ```

3. Measure with enforced limits and an absolute DLL path (`systemd-run` starts in a different working directory):

   ```sh
   flock -o /tmp/ts-heavy.lock systemd-run --user --wait --pipe \
     -p MemoryMax=512M -p MemorySwapMax=0 -p CPUQuota=200% \
     taskset -c 0,1 nice -n 19 dotnet "$PWD/benchmark/dotnet/FeedCursorBench/bin/Release/net11.0/FeedCursorBench.dll" \
     run /tmp/feed-prefix-fixture
   ```

Use CPU IDs allowed by the host. Run detached on remote hosts to preserve the measurement across SSH disconnects.
The fixture stays on disk for inspection. Results and measurement limitations are in
[`2026-10-13-feed-cursor-prefix.md`](../../bench/2026-10-13-feed-cursor-prefix.md).
