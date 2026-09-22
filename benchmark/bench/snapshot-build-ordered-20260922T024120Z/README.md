# Ordered synchronous snapshot construction

Final run: 2026-09-22 02:42:21–02:43:08 UTC, 2,048 documents with 1 KiB
padding, one warmup and five alternating trials per lane. All source documents,
physical entries and query results were verified outside the measured section.

| Lane | Median ms | p95 ms | Median allocated bytes |
| --- | ---: | ---: | ---: |
| Ordered durable batches | 228.035 | 305.890 | 52,494,648 |
| Atomic materialization, per-append synchronization | 7,142.593 | 7,582.839 | 95,000,960 |

Both lanes use synchronous index WALs. The batched lane persists transaction
start, beforeimages, write stamps, data and commit in that order. It includes
snapshot spool/checksum/directory synchronization and durable cursor costs.
The atomic lane buffers the source and uses one transaction with individual
append synchronization. These are different construction strategies, not an
equivalent-operation engine comparison against SQLite.

Historical measurements with the same population/trial count:

| Subject | Durable-batch lane median ms | Atomic lane median ms | Durability interpretation |
| --- | ---: | ---: | --- |
| `../snapshot-build-20260921/` | 213.353 | 170.013 | Asynchronous WAL; later SIGKILL tests lost acknowledged checkpoints |
| `../snapshot-build-sync-20260921/` | 8,158.418 | 8,843.788 | Synchronous WAL with per-append flushing |
| This run | 228.035 | 7,142.593 | Ordered phase flushing for the batched lane |

These are observations on a shared development host. Another actor's
10-million-document `e2e-volume` benchmark (PID 1166495, nice 19) was confirmed
running before this measurement and was left untouched. Our API/core/process
test runs had finished. No general speedup, larger-than-memory behavior,
SQLite superiority, device power-loss guarantee or platform parity is claimed.
Five trials make the reported p95 the slowest observed trial, not a reliable
tail-latency estimate. RSS/managed peaks are sampled process-wide, and allocation
deltas include process background work. See `snapshot-build.md` for full costs.

Evidence retained here:

- `snapshot-build-raw.json`: all ten measured trials, batch bounds and excluded
  source snapshot capture time (83.057 ms).
- `snapshot-build-receipt.json`: SHA-256 of the measured TerraBase, ZoneTree and
  benchmark assemblies, plus raw/report hashes. Those hashes were checked against
  the actual files after the run; medians/p95 were recomputed from raw rows.
- `run-receipt.json`: command, host/runtime and subject base commit
  `83f30b5110764c1a9c8f9d090a8697dbf2f042a0`, marked dirty. Its digest is
  `77252881353e1297c4735e22db3331909d5d1d580bb78ab294b1328894d45ac9`.
- Base commit files, tracked production/benchmark patches, the untracked
  production-source archive and benchmark workload source preserve the local
  changes. `source-sha256.txt` covers those source artifacts. The production
  patch and archive were checked against the checkout again after measurement.
- `console.log`: original benchmark output, copied without editorial changes.

The benchmark was built separately with
`nice -n 19 dotnet build terrabase/TerraBase.Benchmarks -c Release --artifacts-path /tmp/terrabase-batch-qualified-artifacts`.
The run receipt records the exact DLL invocation. This separate build directory
avoided replacing binaries used by the other actor's running benchmark.

For another run, build the recorded source and use a **new** output directory:

```sh
nice -n 19 dotnet run --project terrabase/TerraBase.Benchmarks -c Release -- --workload=snapshot-build --documents=2048 --trials=5 --output=bench/snapshot-build-ordered-repeat
```

Do not reuse an existing results directory. The earlier artifact overwrite and
mislabeling are disclosed in `../snapshot-build-batch-20260922/README.md`; those
results have not been silently replaced by this run.
