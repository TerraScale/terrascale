#!/usr/bin/env python3
"""Aggregate the three regional read runs and all six xregion-probe directions."""
import collections
import json
import math
import pathlib
import statistics
import sys

directory = pathlib.Path(sys.argv[1])
reads, lag, metadata, cleanup = collections.defaultdict(list), collections.defaultdict(list), {}, {}
valid = True


def percentile(values, quantile):
    ordered = sorted(values)
    return ordered[max(0, math.ceil(len(ordered) * quantile) - 1)]


for path in sorted(directory.glob("*/live.jsonl")):
    rows = [json.loads(line) for line in path.read_text().splitlines()]
    first = next(row for row in rows if row["kind"] == "metadata")
    region = first["region"]
    if region in metadata:
        raise ValueError("duplicate regional evidence")
    metadata[region] = first
    cleanup[region] = [row for row in rows if row["kind"] == "teardown"]
    valid &= all(any(row.get("step") == "project gone" and row.get("target") == target and row["status"] == 404
                     for row in cleanup[region]) for target in ("gru", "sjc", "fra"))
    valid &= len([row for row in rows if row["kind"] == "revision_end" and row["status"] == 200
                  and row["revision"] == first["revisions"].get(row["region"])]) == 3
    for row in rows:
        if row["kind"] == "latency":
            reads[region, row["endpoint"], row["size"]].append(row)
    for probe in sorted(path.parent.glob("live.jsonl.*-r*.jsonl")):
        samples = [json.loads(line) for line in probe.read_text().splitlines()]
        summary = samples[-1]
        valid &= summary.get("summary", False) and summary["missing"] == 0 and len(samples) == 61
        lag[summary["label"]].append(dict(summary=summary, samples=samples[:-1]))

expected = {(region, endpoint, size) for region in ("gru", "sjc", "fra")
            for endpoint in ("documents", "sql", "postgres", "hrana", "wire") for size in ("small", "medium")}
directions = {f"{source}->{target}" for source in ("gru", "sjc", "fra") for target in ("gru", "sjc", "fra") if source != target}
valid &= set(reads) == expected and set(lag) == directions
valid &= all(len(runs) == 3 and {run["repeat"] for run in runs} == {1, 2, 3}
             and all(len(run["samples_ms"]) == 60 for run in runs) for runs in reads.values())
valid &= all(len(runs) == 3 for runs in lag.values())
lines = ["# Live regional baseline", "", f"Evidence {'complete' if valid else 'INVALID / incomplete'}; normal production load. "
         "Reads are c=1, paced at most 10/s; percentiles pool 180 samples. These are host-local endpoint latencies, not public WAN latencies.",
         "", "| Region | Endpoint | Size | N | p50 ms | p99 ms | p99 per-repeat min–max ms |",
         "| --- | --- | --- | ---: | ---: | ---: | --- |"]
for (region, endpoint, size), runs in sorted(reads.items()):
    values = [value for run in runs for value in run["samples_ms"]]
    tails = [run["p99_ms"] for run in runs]
    lines.append(f"| {region} | {endpoint} | {size} | {len(values)} | {percentile(values, .5):.3f} | "
                 f"{percentile(values, .99):.3f} | {min(tails):.3f}–{max(tails):.3f} |")
lines += ["", "| Direction | N | Floor ms | p50 ms | p99 ms | Missing | Target p99 <200 ms |",
          "| --- | ---: | ---: | ---: | ---: | ---: | --- |"]
gaps = []
for direction, runs in sorted(lag.items()):
    values = [sample["latency_ms"] for run in runs for sample in run["samples"]]
    floor = statistics.median(run["summary"]["floor_ms"] for run in runs)
    missing = sum(run["summary"]["missing"] for run in runs)
    tail = percentile(values, .99) if values else None
    gaps.append(dict(direction=direction, p99_ms=tail, gap_ratio=None if tail is None else tail / 200))
    lines.append(f"| {direction} | {len(values)} | {floor:.3f} | {percentile(values, .5):.3f} | "
                 f"{tail:.3f} | {missing} | {'met' if not missing and tail < 200 else 'NOT MET'} |")
result = dict(valid=bool(valid), metadata=metadata, cleanup=cleanup,
              reads=[dict(region=key[0], endpoint=key[1], size=key[2], runs=runs) for key, runs in sorted(reads.items())],
              lag=lag, ranked_lag_gaps=sorted(gaps, key=lambda row: row["gap_ratio"] or 0, reverse=True))
(directory / "live-results.json").write_text(json.dumps(result, separators=(',', ':')) + '\n')
(directory / "live-summary.md").write_text('\n'.join(lines) + '\n')
sys.exit(0 if valid else 1)
