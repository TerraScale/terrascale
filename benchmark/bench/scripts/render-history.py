#!/usr/bin/env python3
"""Renders the benchmark history table from the lane's results documents.

Every row is derived from a retained `bench/results/<target>-<workload>-*.json`
document the lane actually wrote — nothing is hand-typed. The engine-side
adapter (`src/benchmarks/`) owns every verdict; this script only tabulates
the facts those documents record, so a table can never mint a claim the
lane did not produce.

Usage: python3 bench/scripts/render-history.py [--out PATH]
"""

from __future__ import annotations

import argparse
import json
import math
import pathlib
import statistics
import sys

WORKLOADS = ["read", "write", "query", "mixed"]
TARGETS = ["terrabase", "postgresql", "ravendb"]


def _attempt_key(path: pathlib.Path, target: str, workload: str) -> tuple[int, int, str]:
    """Match Rust's retained-attempt ordering.

    The reference driver writes a nanosecond epoch stamp as the first field
    after ``<target>-<workload>-``. Filename fields are authoritative; mtime
    is only a fallback for manually named attempts that lack that stamp.
    """
    prefix = f"{target}-{workload}-"
    suffix = path.name[len(prefix) : -len(".json")]
    stamp = suffix.split("-", 1)[0]
    if stamp.isdigit():
        return (1, int(stamp), path.name)
    try:
        modified = path.stat().st_mtime_ns
    except OSError:
        modified = -1
    return (0, modified, path.name)


def load(results: pathlib.Path, target: str, workload: str) -> dict | None:
    # The driver never overwrites an attempt. If any retained attempt exists,
    # the newest one is authoritative—even when it failed or is malformed.
    paths = list(results.glob(f"{target}-{workload}-*.json"))
    legacy = results / f"{target}-{workload}.json"
    if not paths and legacy.is_file():
        paths = [legacy]
    if not paths:
        return None
    path = max(paths, key=lambda candidate: _attempt_key(candidate, target, workload))
    try:
        with path.open() as handle:
            document = json.load(handle)
    except (OSError, json.JSONDecodeError) as error:
        return {"status": "invalid", "error": f"cannot read {path.name}: {error}"}
    if not isinstance(document, dict):
        return {"status": "invalid", "error": f"{path.name} is not a JSON object"}
    status = document.get("status", "complete")
    if not isinstance(status, str) or status not in {"complete", "failed"}:
        return {"status": "invalid", "error": f"{path.name} has invalid status"}
    if status == "complete":
        if document.get("target") != target or document.get("workload") != workload:
            return {"status": "invalid", "error": f"{path.name} identity does not match its lane"}
        samples = document.get("samples")
        required = {
            "operations",
            "wall_seconds",
            "throughput_ops_per_s",
            "p50_us",
            "p95_us",
            "p99_us",
        }
        if not isinstance(samples, list) or not samples:
            return {"status": "invalid", "error": f"{path.name} has no complete samples"}
        for sample in samples:
            if not isinstance(sample, dict) or not required <= sample.keys():
                return {"status": "invalid", "error": f"{path.name} has an invalid sample"}
            numbers = [sample[key] for key in required]
            if any(isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value) for value in numbers):
                return {"status": "invalid", "error": f"{path.name} has non-finite sample values"}
    return document


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
            status = document.get("status", "complete")
            samples = document.get("samples", [])
            if status == "complete" and samples:
                row = "| {workload} | {target} | {status} | {ops:.0f} | {p50:.0f}–{p50max:.0f} | {p95:.0f} | {p99:.0f} | {wall:.0f} |".format(
                        workload=workload,
                        target=target,
                        status=status,
                        ops=med([s["throughput_ops_per_s"] for s in samples]),
                        p50=med([s["p50_us"] for s in samples]),
                        p50max=max((s["p50_us"] for s in samples), default=0.0),
                        p95=med([s["p95_us"] for s in samples]),
                        p99=med([s["p99_us"] for s in samples]),
                        wall=med([s["wall_seconds"] for s in samples]) * 1000.0,
                    )
            else:
                row = f"| {workload} | {target} | {status} | — | — | — | — | — |"
            rows.append(row)

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
        "| workload | side | status | ops/s | p50 µs (median–max) | p95 µs | p99 µs | sample wall ms |",
        "| --- | --- | --- | --- | --- | --- | --- | --- |",
        *rows,
        "",
    ]
    arguments.out.parent.mkdir(parents=True, exist_ok=True)
    arguments.out.write_text("\n".join(lines))
    print(f"wrote {arguments.out} with {len(rows)} rows")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
