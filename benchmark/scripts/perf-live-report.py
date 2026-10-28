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
issues = []


def percentile(values, quantile):
    ordered = sorted(values)
    return ordered[max(0, math.ceil(len(ordered) * quantile) - 1)] if ordered else None


def number(value):
    return "unavailable" if value is None else f"{value:.3f}"


for path in sorted(directory.glob("*/live.jsonl")):
    try:
        rows = [json.loads(line) for line in path.read_text().splitlines()]
        extra = path.with_name(path.name + ".cleanup.jsonl")
        rows += [json.loads(line) for line in extra.read_text().splitlines()] if extra.exists() else []
    except (OSError, ValueError):
        valid = False
        issues.append(f"Unreadable or partial regional evidence: {path.parent.name}")
        continue
    for row in rows:
        for legacy in ("target_sha", "harness_sha"):
            if legacy in row:
                row["legacy_operator_source_hint"] = row.pop(legacy)
    first = next((row for row in rows if row.get("kind") == "metadata"), None)
    if first is None:
        valid = False
        issues.append(f"Missing regional metadata: {path.parent.name}")
        continue
    region = first["region"]
    if region in metadata:
        raise ValueError("duplicate regional evidence")
    metadata[region] = first
    cleanup[region] = [row for row in rows if row["kind"] == "teardown"]
    valid &= all(any(row.get("step") == step and row.get("target") == target and row["status"] == 404
                     for row in cleanup[region]) for target in first["revisions"]
                 for step in ("project gone", "organization gone", "database gone"))
    endings = [row for row in rows if row["kind"] == "revision_end"]
    valid &= len(endings) == len(first["revisions"]) and {row["region"] for row in endings} == set(first["revisions"])
    valid &= all(isinstance(value, str) and bool(value) for value in first["revisions"].values())
    valid &= all(row["status"] == 200 and row["revision"] == first["revisions"].get(row["region"]) for row in endings)
    for row in rows:
        if row["kind"] == "latency":
            reads[region, row["endpoint"], row["size"]].append(row)
    for probe in sorted(path.parent.glob("live.jsonl.*-r*.jsonl")):
        samples = [json.loads(line) for line in probe.read_text().splitlines()]
        if not samples:
            valid = False
            continue
        summary = samples[-1]
        valid &= summary.get("summary", False) and summary["missing"] == 0 and len(samples) == 61
        lag[summary["label"]].append(dict(repeat=int(probe.name.rsplit('-r', 1)[1].split('.')[0]), summary=summary, samples=samples[:-1]))

regions = set(next(iter(metadata.values()))["revisions"]) if metadata else set()
valid &= bool(regions) and set(metadata) == regions and all(set(row["revisions"]) == regions for row in metadata.values())
valid &= len({value for row in metadata.values() for value in row["revisions"].values()}) == 1
expected = {(region, endpoint, size) for region in regions
            for endpoint in ("documents", "query", "batch", "sql", "postgres", "hrana", "wire") for size in ("small", "medium")}
directions = {f"{source}->{target}" for source in regions for target in regions if source != target}
valid &= set(reads) == expected and set(lag) == directions
valid &= all(len(runs) == 3 and {run["repeat"] for run in runs} == {1, 2, 3}
             and all(len(run["samples_ms"]) == 60 for run in runs) for runs in reads.values())
valid &= all(len(runs) == 3 and {run["repeat"] for run in runs} == {1, 2, 3} for runs in lag.values())
lines = ["# Live regional baseline", "", *issues, "Serving revision(s): " + ", ".join(sorted({str(v) for m in metadata.values() for v in m['revisions'].values()})) + ".",
         "Harness provenance is separate: older target_sha fields are retained as legacy_operator_source_hint, not serving revisions.",
         f"Evidence {'complete' if valid else 'INVALID / incomplete'}; observed production load is recorded in metadata. "
         "Reads are c=1, paced at most 10/s; percentiles pool 180 samples. These are host-local endpoint latencies, not public WAN latencies.",
         "Nearest-rank pooled p99 is the second-largest of 180 samples; each 60-sample repeat's p99 equals its maximum. These are descriptive tails, not confidence bounds.",
         "", "| Region | Endpoint | Size | N | p50 ms | Pooled p99 ms | Repeat maxima min–max ms |",
         "| --- | --- | --- | ---: | ---: | ---: | --- |"]
for (region, endpoint, size), runs in sorted(reads.items()):
    values = [value for run in runs for value in run["samples_ms"]]
    tails = [run["p99_ms"] for run in runs]
    lines.append(f"| {region} | {endpoint} | {size} | {len(values)} | {number(percentile(values, .5))} | "
                 f"{number(percentile(values, .99))} | {min(tails):.3f}–{max(tails):.3f} |")
lines += ["", "| Direction | N | Floor ms | p50 ms | p99 ms | Missing | Target p99 <200 ms |",
          "| --- | ---: | ---: | ---: | ---: | ---: | --- |"]
gaps = []
for direction, runs in sorted(lag.items()):
    values = [sample["latency_ms"] for run in runs for sample in run["samples"]]
    floor = statistics.median(run["summary"]["floor_ms"] for run in runs)
    missing = sum(run["summary"]["missing"] for run in runs)
    tail = percentile(values, .99) if values else None
    gaps.append(dict(direction=direction, p99_ms=tail, gap_ratio=None if tail is None else tail / 200))
    lines.append(f"| {direction} | {len(values)} | {floor:.3f} | {number(percentile(values, .5))} | "
                 f"{number(tail)} | {missing} | {'met' if not missing and tail is not None and tail < 200 else 'NOT MET'} |")
result = dict(valid=bool(valid), issues=issues, metadata=metadata, cleanup=cleanup,
              reads=[dict(region=key[0], endpoint=key[1], size=key[2], runs=runs) for key, runs in sorted(reads.items())],
              lag=lag, ranked_lag_gaps=sorted(gaps, key=lambda row: row["gap_ratio"] or 0, reverse=True))
(directory / "live-results.json").write_text(json.dumps(result, separators=(',', ':')) + '\n')
(directory / "live-summary.md").write_text('\n'.join(lines) + '\n')
sys.exit(0 if valid else 1)
