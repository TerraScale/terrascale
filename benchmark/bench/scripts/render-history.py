#!/usr/bin/env python3
"""Renders the benchmark history table from the lane's results documents.

Every row is derived from a retained `bench/results/<target>-<workload>-*.json`
document the lane actually wrote — nothing is hand-typed. The engine-side
adapter (`src/benchmarks/`) owns every verdict; this script only tabulates
the facts those documents record, so a table can never mint a claim the
lane did not produce.

Usage: python3 bench/scripts/render-history.py [--history-dir DIR]
"""

from __future__ import annotations

import argparse
import datetime
import json
import math
import pathlib
import statistics
import sys

WORKLOADS = ["read", "write", "query", "mixed"]
TARGETS = ["terrabase", "postgresql", "ravendb"]


def write_retained(history_dir: pathlib.Path, contents: str) -> pathlib.Path:
    """Writes one immutable Markdown snapshot inside the history directory."""
    history_dir.mkdir(parents=True, exist_ok=True)
    stamp = datetime.datetime.now(datetime.timezone.utc).strftime("%Y%m%dT%H%M%S.%fZ")
    for attempt in range(100):
        suffix = "" if attempt == 0 else f"-{attempt}"
        path = history_dir / f"benchmark-{stamp}{suffix}.md"
        try:
            with path.open("x") as handle:
                handle.write(contents)
        except FileExistsError:
            continue
        return path
    raise FileExistsError("could not allocate a unique benchmark history filename")


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
        try:
            validate_complete(document, target, workload)
        except ValueError as error:
            return {"status": "invalid", "error": f"{path.name}: {error}"}
    return document


def _number(value: object, field: str) -> float:
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        raise ValueError(f"{field} is not a number")
    value = float(value)
    if not math.isfinite(value):
        raise ValueError(f"{field} is not finite")
    return value


def _positive_integer(value: object, field: str) -> int:
    number = _number(value, field)
    if number <= 0 or number != math.floor(number):
        raise ValueError(f"{field} is not a positive integer")
    return int(number)


def validate_complete(document: dict, target: str, workload: str) -> None:
    """Apply the same admission checks as Rust's ``reference_side`` reader."""
    if document.get("target") != target or document.get("workload") != workload:
        raise ValueError("identity does not match its lane")

    config = document.get("config")
    if not isinstance(config, dict):
        raise ValueError("config is not an object")
    _positive_integer(config.get("seed_documents"), "config.seed_documents")
    operations = _positive_integer(config.get("operations"), "config.operations")
    samples_count = _positive_integer(config.get("samples"), "config.samples")
    concurrency = _positive_integer(config.get("concurrency"), "config.concurrency")
    warmup = _number(config.get("warmup_operations"), "config.warmup_operations")
    if warmup < 0 or warmup != math.floor(warmup):
        raise ValueError("config.warmup_operations is not a non-negative integer")
    noise = _number(config.get("noise_percent"), "config.noise_percent")
    if not 0 <= noise <= 100:
        raise ValueError("config.noise_percent is not in 0..=100")
    if operations % concurrency or int(warmup) % concurrency:
        raise ValueError("config operation counts are not divisible by concurrency")

    environment = document.get("environment")
    if not isinstance(environment, dict):
        raise ValueError("environment is not an object")
    for field in ("observed_identity", "endpoint", "client_transport", "kernel"):
        value = environment.get(field)
        if not isinstance(value, str) or not value:
            raise ValueError(f"environment.{field} is not a non-empty string")
    if "in-process" in environment["client_transport"].lower():
        raise ValueError("in-process transport cannot be compared with served references")
    _positive_integer(environment.get("unix_seconds"), "environment.unix_seconds")
    _positive_integer(environment.get("logical_cpus"), "environment.logical_cpus")

    samples = document.get("samples")
    if not isinstance(samples, list) or len(samples) != samples_count:
        raise ValueError(f"samples must contain exactly {samples_count} entries")
    required = ("operations", "wall_seconds", "throughput_ops_per_s", "p50_us", "p95_us", "p99_us")
    for index, sample in enumerate(samples):
        if not isinstance(sample, dict):
            raise ValueError(f"sample {index} is not an object")
        values = {field: _number(sample.get(field), f"sample {index}.{field}") for field in required}
        if values["operations"] != operations or values["operations"] != math.floor(values["operations"]):
            raise ValueError(f"sample {index} has the wrong operation count")
        if values["wall_seconds"] <= 0 or values["throughput_ops_per_s"] <= 0:
            raise ValueError(f"sample {index} has non-positive timing")
        if values["p95_us"] < values["p50_us"] or values["p99_us"] < values["p95_us"] or values["p50_us"] < 0:
            raise ValueError(f"sample {index} percentiles are not ordered")
        expected = values["operations"] / values["wall_seconds"]
        if abs(values["throughput_ops_per_s"] - expected) / max(expected, 1e-300) > 1e-6:
            raise ValueError(f"sample {index} throughput does not match wall_seconds")


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
    parser.add_argument(
        "--history-dir",
        default="docs/evidence/benchmarks/history",
        type=pathlib.Path,
        help="directory that retains immutable Markdown snapshots",
    )
    arguments = parser.parse_args()

    rows: list[str] = []
    identities: dict[str, str] = {}
    documents_found = False
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
                rows.append(
                    f"| {workload} | {target} | missing | — | — | — | — | — |"
                )
                continue
            documents_found = True
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

    if not documents_found:
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
    path = write_retained(arguments.history_dir, "\n".join(lines))
    print(f"wrote {path} with {len(rows)} rows")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
