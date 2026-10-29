#!/usr/bin/env python3
"""Convert VsTurso JSONL to auditable JSON, tables and ranked Q-PARITY gaps."""
import collections
import json
import pathlib
import statistics
import sys


def report(directory, baseline=None):
    directory = pathlib.Path(directory)
    metadata = json.loads((directory / "metadata.json").read_text())
    load = directory / "load.jsonl"
    metadata["load_samples"] = [json.loads(line) for line in load.read_text().splitlines()] if load.exists() else []
    telemetry_errors = directory / "telemetry-errors"
    metadata["telemetry_errors"] = telemetry_errors.read_text() if telemetry_errors.exists() else ""
    raw = directory / "local.raw"
    cells = [json.loads(line) for line in (raw.read_text() if raw.exists() else "").splitlines()
             if line.startswith('{"Lane":')]
    groups = collections.defaultdict(list)
    for cell in cells:
        groups[cell["Size"], cell["Workload"], cell["Concurrency"], cell["Lane"]].append(cell)
    lanes = {"turso", "turso-cdc", "engine", "api", "api-query", "api-batch", "sql", "pg"}
    lanes.update(cell["Lane"] for cell in cells if cell["Lane"] in ("hrana", "wire", "tcp-echo"))
    expected = {(size, operation, concurrency, lane) for size in ("small", "medium")
                for operation in ("read", "write") for concurrency in (1, 16)
                for lane in lanes}
    reservation = metadata.get("host_reservation", {})
    reserved = reservation.get("host") == metadata["host"] and len(set(reservation.get("scheduler_locks", []))) == 4
    invalid = not reserved or not metadata['load_samples'] or bool(metadata['telemetry_errors'] or metadata.get("tracked_changes") or metadata.get("untracked_source")) or set(groups) != expected or any(len(rows) != 3 or {r["Repeat"] for r in rows} != {1, 2, 3}
                               or any(r["Errors"] or r["OpsPerSecond"] <= 0 for r in rows)
                               for rows in groups.values())
    med = lambda rows, field: statistics.median(r[field] for r in rows)
    gaps = []
    for (size, workload, concurrency, lane), rows in groups.items():
        if workload != "read" or lane in ("turso", "turso-cdc", "tcp-echo", "engine", "floor", "api-batch"):
            continue
        raw, echo = (groups.get((size, workload, concurrency, baseline)) for baseline in ("turso", "tcp-echo"))
        if not raw:
            invalid = True
            continue
        if not echo:
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
    comparison = []
    try:
        if baseline is not None:
            before = json.loads(pathlib.Path(baseline).read_text())
            if not before["valid"] or not before["metadata"].get("host_reservation") or not data["valid"] or before["metadata"]["host"] != metadata["host"]:
                raise ValueError("comparison requires complete zero-error runs on the same host")
            if any(before["metadata"].get(key) != metadata.get(key) for key in ("cpus", "dotnet", "cpu_model")):
                raise ValueError("comparison host CPU or .NET SDK identity differs")
            old = collections.defaultdict(list)
            for cell in before["cells"]:
                old[cell["Size"], cell["Workload"], cell["Concurrency"], cell["Lane"]].append(cell)
            if set(old) != set(groups) or any({r.get("Seconds") for r in old[key]} != {r.get("Seconds") for r in rows}
                                              for key, rows in groups.items()):
                raise ValueError("comparison cell matrix or measurement durations differ")
            for key, rows in groups.items():
                changes = {field: 100 * (med(rows, field) / med(old[key], field) - 1)
                           for field in ("OpsPerSecond", "P50Micros", "P99Micros")}
                comparison.append(dict(size=key[0], workload=key[1], concurrency=key[2], lane=key[3], delta_percent=changes))
            data["comparison"] = dict(baseline_sha=before["metadata"]["sha"], cells=comparison)
    except (OSError, ValueError, KeyError, ZeroDivisionError) as error:
        invalid, data["valid"], data["comparison_error"] = True, False, str(error)
        comparison.clear()
    (directory / "results.json").write_text(json.dumps(data, separators=(',', ':')) + "\n")
    lines = ["# TerraScale performance baseline", "", f"Source: `{metadata['sha']}`; host: `{metadata['host']}`.",
             f"Main source: `{metadata['main_sha']}`; CPUs: {metadata['cpus']}; starting load: {metadata['load_before']}.",
             f"Starting CPU pressure: `{metadata['cpu_pressure_before'].splitlines()[0]}`. "
             f"Recorded {len(metadata['load_samples'])} load/pressure observations, including build and preload.", data.get("comparison_error", ""),
             "Quietness is checked at admission; scheduler slots stay reserved. Recorded load/pressure and control repeat spreads must be inspected for unscheduled interference during measurement.",
             f"Three interleaved repeats; host reservation: {reserved}; status: {'INVALID / missing reservation, dirty source, incomplete or failed cells' if invalid else 'complete, zero operation errors'}.",
             "", "Q-PARITY read targets: c=1 each percentile ≤ 1.1 × (raw Turso + TCP echo); "
             "c=16 ops/s ≥ 0.9 × min(raw Turso, TCP echo). These historical targets are evaluated only when recorded TCP echo controls are present; current runs report endpoint measurements without target gaps.",
             "", "## Biggest gaps vs target", "", "| Rank | Lane | Size | c | Metric | Observed | Target | Gap ratio |",
             "| ---: | --- | --- | ---: | --- | ---: | ---: | ---: |"]
    for rank, gap in enumerate(gaps, 1):
        lines.append(f"| {rank} | {gap['lane']} | {gap['size']} | {gap['concurrency']} | {gap['metric']} | "
                     f"{gap['observed']:.1f} | {gap['target']:.1f} | {gap['gap_ratio']:.2f}× |")
    lines += ["", "## All local cells", "", "Percentiles are medians of per-repeat percentiles, not pooled percentiles. "
              "Batch throughput counts requests; each batch contains 16 documents. These are closed-loop measurements.",
              "", "| Size | Operation | c | Lane | ops/s median (min–max) | p50 µs | p99 µs | Samples | Errors |",
              "| --- | --- | ---: | --- | ---: | ---: | ---: | ---: | ---: |"]
    for (size, workload, concurrency, lane), rows in groups.items():
        rates = [r["OpsPerSecond"] for r in rows]
        lines.append(f"| {size} | {workload} | {concurrency} | {lane} | {statistics.median(rates):.0f} "
                     f"({min(rates):.0f}–{max(rates):.0f}) | {med(rows, 'P50Micros'):.1f} | "
                     f"{med(rows, 'P99Micros'):.1f} | {sum(r.get('Samples', 0) for r in rows)} | {sum(r['Errors'] for r in rows)} |")
    if comparison:
        lines += ["", "## Change vs baseline", "", "Deltas compare three-repeat medians; positive throughput and negative latency are improvements. "
                  "These deltas alone do not establish statistical significance. Inspect repeat spreads and host load.",
                  "", "| Size | Operation | c | Lane | ops/s Δ % | p50 Δ % | p99 Δ % |",
                  "| --- | --- | ---: | --- | ---: | ---: | ---: |"]
        for row in comparison:
            change = row["delta_percent"]
            lines.append(f"| {row['size']} | {row['workload']} | {row['concurrency']} | {row['lane']} | "
                         f"{change['OpsPerSecond']:+.2f} | {change['P50Micros']:+.2f} | {change['P99Micros']:+.2f} |")
    (directory / "summary.md").write_text("\n".join(lines) + "\n")
    return int(invalid)


if __name__ == "__main__":
    sys.exit(report(*sys.argv[1:]))
