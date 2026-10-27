#!/usr/bin/env python3
"""Convert VsTurso JSONL to auditable JSON, tables and ranked Q-PARITY gaps."""
import collections
import json
import pathlib
import statistics
import sys


def report(directory):
    directory = pathlib.Path(directory)
    metadata = json.loads((directory / "metadata.json").read_text())
    raw = directory / "local.raw"
    cells = [json.loads(line) for line in (raw.read_text() if raw.exists() else "").splitlines()
             if line.startswith('{"Lane":')]
    groups = collections.defaultdict(list)
    for cell in cells:
        groups[cell["Size"], cell["Workload"], cell["Concurrency"], cell["Lane"]].append(cell)
    expected = {(size, operation, concurrency, lane) for size in ("small", "medium")
                for operation in ("read", "write") for concurrency in (1, 16)
                for lane in ("turso", "turso-cdc", "engine", "api", "api-query", "api-batch", "sql", "pg", "hrana", "wire", "tcp-echo")}
    invalid = set(groups) != expected or any(len(rows) != 3 or {r["Repeat"] for r in rows} != {1, 2, 3}
                               or any(r["Errors"] or r["OpsPerSecond"] <= 0 for r in rows)
                               for rows in groups.values())
    med = lambda rows, field: statistics.median(r[field] for r in rows)
    gaps = []
    for (size, workload, concurrency, lane), rows in groups.items():
        if workload != "read" or lane in ("turso", "turso-cdc", "tcp-echo", "engine", "floor", "api-batch"):
            continue
        raw, echo = (groups.get((size, workload, concurrency, baseline)) for baseline in ("turso", "tcp-echo"))
        if not raw or not echo:
            invalid = True
            continue
        fields = ("P50Micros", "P99Micros") if concurrency == 1 else ("OpsPerSecond",)
        for field in fields:
            observed = med(rows, field)
            target = (1.1 * (med(raw, field) + med(echo, field)) if concurrency == 1
                      else 0.9 * min(med(raw, field), med(echo, field)))
            if observed <= 0 or target <= 0:
                invalid = True
                continue
            ratio = observed / target if concurrency == 1 else target / observed
            gaps.append(dict(lane=lane, size=size, concurrency=concurrency, metric=field,
                             observed=observed, target=target, gap_ratio=ratio))
    gaps.sort(key=lambda row: row["gap_ratio"], reverse=True)
    data = dict(metadata=metadata, valid=not invalid, cells=cells, ranked_read_gaps=gaps)
    (directory / "results.json").write_text(json.dumps(data, indent=2) + "\n")
    lines = ["# TerraScale performance baseline", "", f"Source: `{metadata['sha']}`; host: `{metadata['host']}`.",
             f"Three interleaved repeats; status: {'INVALID / incomplete or failed cells' if invalid else 'complete, zero operation errors'}.",
             "", "Q-PARITY read targets: c=1 each percentile ≤ 1.1 × (raw Turso + TCP echo); "
             "c=16 ops/s ≥ 0.9 × min(raw Turso, TCP echo). Applying the Wire budget to other endpoints is an aspirational comparison.",
             "", "## Biggest gaps vs target", "", "| Rank | Lane | Size | c | Metric | Observed | Target | Gap ratio |",
             "| ---: | --- | --- | ---: | --- | ---: | ---: | ---: |"]
    for rank, gap in enumerate(gaps, 1):
        lines.append(f"| {rank} | {gap['lane']} | {gap['size']} | {gap['concurrency']} | {gap['metric']} | "
                     f"{gap['observed']:.1f} | {gap['target']:.1f} | {gap['gap_ratio']:.2f}× |")
    lines += ["", "## All local cells", "", "Percentiles are medians of per-repeat percentiles, not pooled percentiles. "
              "Batch throughput counts requests; each batch contains 16 documents. These are closed-loop measurements.",
              "", "| Size | Operation | c | Lane | ops/s median (min–max) | p50 µs | p99 µs | Errors |",
              "| --- | --- | ---: | --- | ---: | ---: | ---: | ---: |"]
    for (size, workload, concurrency, lane), rows in groups.items():
        rates = [r["OpsPerSecond"] for r in rows]
        lines.append(f"| {size} | {workload} | {concurrency} | {lane} | {statistics.median(rates):.0f} "
                     f"({min(rates):.0f}–{max(rates):.0f}) | {med(rows, 'P50Micros'):.1f} | "
                     f"{med(rows, 'P99Micros'):.1f} | {sum(r['Errors'] for r in rows)} |")
    (directory / "summary.md").write_text("\n".join(lines) + "\n")
    return int(invalid)


if __name__ == "__main__":
    sys.exit(report(sys.argv[1]))
