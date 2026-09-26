# Strict 10M ingest launch diagnostic

- **Status:** launcher exited before the first acknowledged batch
- **Launch mode:** detached `nohup preflight-command-10m-ingest.sh`
- **Launcher PID:** 2756912
- **Benchmark PID:** 2756914
- **Configured cgroup:** `MemoryMax=4G`, `MemorySwapMax=0`, `OOMPolicy=stop`
- **Configured free-space floor:** 107,374,182,400 bytes
- **Prefix/output:** `tiered-rustfs-strict-10m-ingest-20260923T152728Z`
- **Receipt:** `tiered-rustfs-receipt.json`
- **Console log:** `tiered-rustfs-strict-10m-ingest-20260923T152728Z.console.log`

The launcher and benchmark process disappeared during initialization/seed before
any record was acknowledged. The preserved receipt remained `Status=running`,
`Phase=seed`, `CompletedRecords=0`, with no error recorded. The preserved progress
stream contains only initialization at 0 records. The resource samples show the
configured 4 GiB memory cap, zero swap cap, zero OOM counters, and no free-space
violation. No SIGINT or explicit stop was sent to this first launch. No source or
frozen binary was changed.

A persistent systemd scope rerun was then started with a fresh suffix and prefix:

`/home/mariogk/Projects/TerraScale/ts-benchmark/bench/receipts/tiered-rustfs-strict-10m-ingest-20260923T152728Z-rerun`

The rerun is the valid calibration attempt.
