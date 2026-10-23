# Replication baseline, local three regions, old engine (2026-10-12)

Before design C. These runs measure the engine as it was before R2a-1 (#48) deleted the old replication: a whole-document
register per document and a pull-based WAN puller. They are the "before" for the design-C runs. The design-C local runs
can only start after peer streams (R4a) bring replication back.

- Code: tag `bench/old-engine-baseline-2026-10-12b` (commit `af126ee8`): main at `8f56a996` plus this benchmark.
- Host: mariogk-dev, 46 vCPU Intel Xeon E5-2660 v4 @ 2.0 GHz, 31 GB RAM, otherwise idle. One process hosts gru, sjc and
  fra (tests/TerraBase.E2E/BenchmarkClusterHost.cs, Release build). The regions share CPU and memory and have no WAN
  between them.
- Command: `nice -n 19 make benchmark-repl-local REPL_ARGS='…'` (sizes 256 B 70 %, 1 KiB 25 %, 8 KiB 5 %; 16 writers per
  region; 1 % deleted by their region; 200 conflict documents × 5 rounds per region; one lag probe in flight per origin).

| Run | Docs | Request | Throughput (all regions) | Request p50 / p99 | Lag p50 / p99 (worst pair) | Every region exact after the last ack | CPU cores avg | RSS avg / max | Disk per node |
| --- | ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| [1M](2026-10-12-repl-local-1m-batch16.md) | 1,000,000 | transaction of 16 PUTs | 1,773 docs/s | 367 / 547 ms | 18.5 / 43.4 s | 2.3 s (end markers); listings matched, 108 s per region | 3.94 | 1.9 / 10.5 GB | 4.48 GB |
| [100k](2026-10-12-repl-local-100k-put.md) | 100,000 | one PUT | 831 docs/s | 55 / 117 ms | 10.2 / 19.3 s | 2.9 s | 4.78 | 0.5 / 2.1 GB | 0.45 GB |

## Correctness (both runs)

- Every region holds exactly the expected documents (990,183 and 99,027 after deletes): no document missing, extra or
  mismatched. Digests are equal in all three regions, and no write had an unknown outcome.
- Conflicts: 200 of 200 documents are identical everywhere. The shared field `s` was always sjc's last write. Each region's
  own field survived only for sjc: whole-document last-writer-wins drops the other regions' fields. Design C's per-field
  lattice is meant to keep all three; this is the before value for that.

## Findings

1. Replication lag under load is 10 to 43 s, but end markers written after the load are visible everywhere 2 to 3 s
   later. The old puller falls behind while writes arrive and catches up when they stop.
2. The process is not CPU-bound: 3.9 of 46 cores at 1,773 docs/s. Commit latency and replication rounds bound throughput.
3. RSS peaks at 10.5 GB while the three regions are listed in parallel, but that is mostly the databases' memory map,
   not the heap. SQLite's `mmap_size` is the memory budget (`SqliteSession.Pragmas`), so a full scan maps the file into
   RSS as page cache the kernel can reclaim. A follow-up run on main (beta, one region, 297k documents listed in 9 s)
   showed working set going from 0.56 to 1.84 GB while the GC's committed memory stayed at or below 0.49 GB.
4. Disk is 4.5 GB per node for 990k documents averaging about 0.9 KB: about 5× amplification from the history and
   change-log tables. Design C removes those tables.
5. The first version of the lag probe (one poller per probe) loaded the cluster it measured: it halved throughput and
   inflated lag. The runner now keeps one probe in flight per origin and reports the poll read latency (p50 3.4 ms here).

## Not tested

- WAN latency, per-node CPU and memory (the nodes share one process), reads under load, counters, sets, SQL writes, the
  changes feed and faults during the load (tests/TerraBase.E2E covers faults). No cloud execution is established by these local measurements.
