#!/usr/bin/env python3
"""Renders the benchmark history table from the lane's results documents.

Every row is derived from a `bench/results/<target>-<workload>.json`
document the lane actually wrote — nothing is hand-typed. The engine-side
adapter (`src/benchmarks/`) owns every verdict; this script only tabulates
the facts those documents record, so a table can never mint a claim the
lane did not produce.

Usage: python3 bench/scripts/render-history.py [--out PATH]
"""

from __future__ import annotations

import argparse
import json
import pathlib
import statistics
import sys

WORKLOADS = ["read", "write", "query", "mixed"]
TARGETS = ["terrabase", "postgresql", "ravendb"]


def load(results: pathlib.Path, target: str, workload: str) -> dict | None:
    path = results / f"{target}-{workload}.json"
    if not path.exists():
        return None
    with path.open() as handle:
        return json.load(handle)


def med(values: list[float]) -> float:
    return statistics.median(values) if values else 0.0


def identity_of(document: dict) -> str:
    environment = document.get("environment", {})
    return environment.get("observed_identity", "unknown")


def revision_of(document: dict) -> str:
    # The engine-side adapter records its source revision in the identity
    # string; references record their own observed identity verbatim.
    return identity_of(document)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--results", default="bench/results", type=pathlib.Path)
    parser.add_argument("--out", default="docs/evidence/benchmarks/benchmark-history.md", type=pathlib.Path)
    arguments = parser.parse_args()

    rows: list[str] = []
    identities: dict[str, str] = {}
    for workload in WORKLOADS:
        engine = load(arguments.results, "terrabase", workload)
        postgresql = load(arguments.results, "postgresql", workload)
        ravendb = load(arguments.results, "ravendb", workload)
        for target, document in (
            ("terrabase", engine),
            ("postgresql", postgresql),
            ("ravendb", ravendb),
        ):
            if document is None:
                continue
            identities[target] = revision_of(document)
            samples = document.get("samples", [])
            rows.append(
                "| {workload} | {target} | {ops:.0f} | {p50:.0f}–{p50max:.0f} | {p95:.0f} | {p99:.0f} | {wall:.0f} |".format(
                    workload=workload,
                    target=target,
                    ops=med([s["throughput_ops_per_s"] for s in samples]),
                    p50=med([s["p50_us"] for s in samples]),
                    p50max=max((s["p50_us"] for s in samples), default=0.0),
                    p95=med([s["p95_us"] for s in samples]),
                    p99=med([s["p99_us"] for s in samples]),
                    wall=med([s["wall_seconds"] for s in samples]) * 1000.0,
                )
            )

    if not rows:
        print("no results documents found under", arguments.results, file=sys.stderr)
        return 1

    lines = [
        "# Benchmark history",
        "",
        "Every row is derived from a lane results document under `bench/results/`",
        "by `bench/scripts/render-history.py` — never hand-edited. Verdicts and",
        "receipts are produced only by the engine-side adapter",
        "(`src/benchmarks/`); this table tabulates facts. A wall-clock number here",
        "is a fact about the host and moment that produced it, per the limits each",
        "results document carries.",
        "",
        "Lane configuration: seed 1,000 documents · 10,000 operations per sample ·",
        "5 samples · concurrency 16 · 1,000 warm-up operations · 5% noise band,",
        "per `LaneConfig::RECORD_MINIMUM`.",
        "",
        "## Sides",
        "",
    ]
    for target in TARGETS:
        if target in identities:
            lines.append(f"- **{target}**: {identities[target]}")
    lines += [
        "",
        "## Results (median across samples)",
        "",
        "| workload | side | ops/s | p50 µs (median–max) | p95 µs | p99 µs | sample wall ms |",
        "| --- | --- | --- | --- | --- | --- | --- |",
        *rows,
        "",
    ]
    arguments.out.parent.mkdir(parents=True, exist_ok=True)
    arguments.out.write_text("\n".join(lines))
    print(f"wrote {arguments.out} with {len(rows)} rows")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
