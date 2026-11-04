import json, sys

runs = [json.loads(line) for line in open(sys.argv[1]) if line.strip()]
sha, host = sys.argv[2], sys.argv[3]
print("# Shared-process tenancy (E6, E11)\n")
print(f"Commit `{sha}`, host `{host}`, {runs[0]['cpus']} CPUs, {len(runs)} measured repeats. "
      "Produced by `benchmark/scripts/shared-process.sh` (`benchmark/dotnet/SharedProcessBenchmark.cs`), "
      "one in-process TerraBase per scenario.\n")
print("## 1,000 databases in one process\n")
print("Each database is created, gets a table and a row, is written again and then read back (row count checked). "
      "RSS is the whole process after a full GC.\n")
print("| Run | Writer bound | Open writers | RSS before (MiB) | RSS after (MiB) | Create + first write (s) | Second write p50 (ms) | p99 (ms) |")
print("|---|---|---|---|---|---|---|---|")
for i, run in enumerate(runs, 1):
    for label in ("bounded", "unbounded"):
        r = run[label]
        print(f"| {i} {label} | {r['max_writers']} | {r['open_writers']} | {r['rss_before_mib']} | {r['rss_after_mib']} | "
              f"{r['create_seconds']} | {r['rewrite_p50_ms']} | {r['rewrite_p99_ms']} |")
print("\n## Runaway neighbour\n")
print("Tenant B (Hobby) runs point reads paced at 40 per second (under its 50/s plan rate); tenant A (Hobby) keeps 4 endless recursive queries in flight "
      "(its plan concurrency) on a 4-thread storage pool.\n")
print("| Run | B alone p50 (ms) | B alone p99 (ms) | B with A p50 (ms) | B with A p99 (ms) | A requests | A throttled (429) |")
print("|---|---|---|---|---|---|---|")
for i, run in enumerate(runs, 1):
    n = run["neighbour"]
    print(f"| {i} | {n['alone_p50_ms']} | {n['alone_p99_ms']} | {n['loaded_p50_ms']} | {n['loaded_p99_ms']} | "
          f"{n['runaway_requests']} | {n['runaway_throttled']} |")
