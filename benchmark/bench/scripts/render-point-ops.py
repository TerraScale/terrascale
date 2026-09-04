#!/usr/bin/env python3
"""Render the E-13 point-ops family section of bench/RESULTS.md.

Point-ops family = the `read` and `write` lane workloads only:
  - read:  point document read by identifier (seeded `seed-{index}`).
  - write: acknowledged create/replace by identifier (`measured-{index}`).

The `query` workload belongs to the indexed-query family and `mixed` is a
cross-family composite; neither is rendered here, so sibling families can
own their sections without file conflicts.

Reads the latest retained JSON per (target, workload) under bench/results/,
computes the median-across-samples ops/s and p50/p95/p99, and rewrites only
the text between the POINT-OPS markers in bench/RESULTS.md (creating the
file with its header when absent). Idempotent: rendering twice without new
results produces byte-identical section content apart from the render stamp.

Usage (repository root):
    python3 bench/scripts/render-point-ops.py [--results DIR] [--out FILE]

No third-party dependencies: stdlib only.
"""

import glob
import json
import os
import statistics
import sys
import time

BEGIN = "<!-- E-13 point-ops BEGIN -->"
END = "<!-- E-13 point-ops END -->"

# This family owns point operations only. Targets mirror the lane driver.
TARGETS = ("terrabase", "postgresql", "pg-fullstack", "ravendb")
WORKLOADS = ("read", "write")

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
RESULTS_DIR = os.path.join(REPO_ROOT, "results")
RESULTS_MD = os.path.join(REPO_ROOT, "RESULTS.md")


def latest_attempt(results_dir, target, workload):
    """Newest retained attempt for one (target, workload) lane, or None."""
    prefix = "%s-%s-" % (target, workload)
    candidates = []
    try:
        names = os.listdir(results_dir)
    except OSError:
        return None
    for name in names:
        if name.startswith(prefix) and name.endswith(".json"):
            candidates.append(os.path.join(results_dir, name))
    legacy = os.path.join(results_dir, "%s-%s.json" % (target, workload))
    if not candidates and os.path.isfile(legacy):
        candidates.append(legacy)
    if not candidates:
        return None

    def key(path):
        name = os.path.basename(path)
        stamp = name[len(prefix):].split("-")[0] if name.startswith(prefix) else ""
        try:
            return (1, int(stamp), name)
        except ValueError:
            try:
                return (0, int(os.path.getmtime(path)), name)
            except OSError:
                return (0, 0, name)

    candidates.sort(key=key)
    return candidates[-1]


def load_document(path):
    try:
        with open(path, encoding="utf-8") as handle:
            return json.load(handle)
    except (OSError, ValueError):
        return None


def median(values):
    return statistics.median(values) if values else float("nan")


DIAGNOSTIC_IDENTITY = "tbep-qualification-harness/network-mtls/v1"
DIAGNOSTIC_LIMIT = "source identity: terrabase-rust-diagnostic-qualification/v1"


def is_diagnostic(document):
    """Diagnostic-boundary runs carry no comparability contract (history.rs
    `is_diagnostic_result`); they are trajectory facts, never comparable."""
    if not isinstance(document, dict):
        return False
    env = document.get("environment") or {}
    if env.get("observed_identity") == DIAGNOSTIC_IDENTITY:
        return True
    return any(limit == DIAGNOSTIC_LIMIT
               for limit in (document.get("limits") or [])
               if isinstance(limit, str))


def summarize(document):
    """(status, ops/s, p50, p95, p99, wall_ms) medians across samples."""
    if not isinstance(document, dict):
        return ("invalid",) + ("-",) * 5
    if document.get("status") == "failed":
        return ("failed",) + ("-",) * 5
    samples = document.get("samples")
    if not isinstance(samples, list) or not samples:
        return ("missing",) + ("-",) * 5
    try:
        ops = median([float(s["throughput_ops_per_s"]) for s in samples])
        p50 = median([float(s["p50_us"]) for s in samples])
        p95 = median([float(s["p95_us"]) for s in samples])
        p99 = median([float(s["p99_us"]) for s in samples])
        wall = median([float(s["wall_seconds"]) for s in samples]) * 1000.0
    except (KeyError, TypeError, ValueError):
        return ("invalid",) + ("-",) * 5
    fmt = lambda v: ("%.0f" % v) if v == v else "-"
    status = "diagnostic" if is_diagnostic(document) else "complete"
    return (status, fmt(ops), fmt(p50), fmt(p95), fmt(p99), fmt(wall))


def render_section(results_dir):
    lines = []
    lines.append(BEGIN)
    lines.append("")
    lines.append("### Point ops (read / write)")
    lines.append("")
    lines.append("Point read by id and acknowledged create/replace by id, one")
    lines.append("connection per concurrency slot, warm-up excluded from every")
    lines.append("measured sample. `query` lives in the indexed-query family;")
    lines.append("`mixed` is a cross-family composite rendered by no family here.")
    lines.append("`diagnostic` = diagnostic-boundary trajectory (no comparability")
    lines.append("contract, never decision-grade); `failed` = newest retained")
    lines.append("attempt failed, older complete attempts are superseded.")
    lines.append("")
    lines.append("| workload | side | status | ops/s | p50 us | p95 us | p99 us | wall ms |")
    lines.append("| --- | --- | --- | --- | --- | --- | --- | --- |")
    for workload in WORKLOADS:
        for target in TARGETS:
            path = latest_attempt(results_dir, target, workload)
            if path is None:
                lines.append("| %s | %s | missing | - | - | - | - | - |"
                             % (workload, target))
                continue
            status, ops, p50, p95, p99, wall = summarize(load_document(path))
            lines.append("| %s | %s | %s | %s | %s | %s | %s | %s |"
                         % (workload, target, status, ops, p50, p95, p99, wall))
    lines.append("")
    lines.append(END)
    return "\n".join(lines) + "\n"


HEADER = """# Benchmarks (E-13)

One command per family appends here; this file is generated, never
hand-edited. Each family section is delimited by markers so sibling
families render independently. A number here is a fact about the host and
moment that produced it, per the limits each results document carries.

## Resource ceiling (all families, owner decision 2026-08-31)

Every MEASURED launch member — the TerraBase member, PostgreSQL /
pgbouncer / pgrest, RavenDB — runs capped at **2 OCPU / 12 GiB RAM**
(`bench/scripts/resource-envelope.sh`, spec `2ocpu/12GiB`: two-CPU
affinity inside a `systemd-run` scope holding the memory ceiling).
Load generators (the bench lane process) are NOT capped: they are the
instrument, not the subject. Disk is unfenced; write-family numbers must
be read next to the ambient `io-pressure` snapshot the runner records.
Runs without verified cgroup evidence are unavailable and never join a
comparison. There is no unfenced gating mode.

## Families

| family | workloads | status |
| --- | --- | --- |
| point ops | read, write | rendered below |
| indexed query | query | <!-- E-13 indexed-query STATUS -->pending<!-- E-13 indexed-query STATUS-END --> |
| ingest | bulk load | <!-- E-13 ingest STATUS -->pending<!-- E-13 ingest STATUS-END --> |
| auto-indexing | index build / shadow verify | <!-- E-13 auto-indexing STATUS -->pending<!-- E-13 auto-indexing STATUS-END --> |
| sync | view attach / delta log | <!-- E-13 sync STATUS -->pending<!-- E-13 sync STATUS-END --> |

"""


def splice(path, section):
    if os.path.isfile(path):
        with open(path, encoding="utf-8") as handle:
            text = handle.read()
    else:
        text = HEADER
    if BEGIN in text and END in text:
        before, rest = text.split(BEGIN, 1)
        _, after = rest.split(END, 1)
        return before + section + after
    if not text.endswith("\n"):
        text += "\n"
    return text + "\n" + section


def main(argv):
    results_dir = RESULTS_DIR
    out = RESULTS_MD
    args = iter(argv[1:])
    for arg in args:
        if arg == "--results":
            results_dir = next(args, results_dir)
        elif arg == "--out":
            out = next(args, out)
        else:
            print("unknown argument: %s" % arg, file=sys.stderr)
            print("usage: render-point-ops.py [--results DIR] [--out FILE]",
                  file=sys.stderr)
            return 2
    section = render_section(results_dir)
    stamp = ("_Point-ops section rendered %s UTC from `%s/`._\n"
             % (time.strftime("%Y-%m-%dT%H:%M:%S", time.gmtime()),
                os.path.relpath(results_dir, REPO_ROOT)))
    # The stamp lives outside the markers so section bytes stay stable
    # between renders with identical results.
    text = splice(out, section)
    with open(out, "w", encoding="utf-8") as handle:
        handle.write(text)
    print("rendered point-ops section from %s -> %s" % (results_dir, out))
    print(stamp.strip())
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
