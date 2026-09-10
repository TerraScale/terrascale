#!/usr/bin/env python3
"""Diff two bench/results directories: newest complete receipt per lane.

Usage: diff-results.py OLD_DIR NEW_DIR
Lanes are (target[-mode], workload); terrabase served lanes under run dirs
are flattened by scanning recursively. Median sample per receipt.
"""
import json
import os
import statistics
import sys


def lane_key(doc):
    # targets are already lane-distinct ("zonetree-sync", "terra-base", ...);
    # ingest receipts carry topology instead of workload
    workload = doc.get("workload") or doc.get("topology") or ""
    return doc.get("target", "?"), workload


def receipt_nanos(name):
    # lane receipts embed their start instant in the filename:
    # <target>-<workload>-<nanos>-<pid>-<seq>.json
    parts = name[:-len(".json")].split("-")
    for part in parts:
        if part.isdigit() and len(part) >= 16:
            return int(part)
    return 0


def newest_per_lane(root):
    lanes = {}
    for dirpath, _dirs, files in os.walk(root):
        for name in files:
            if not name.endswith(".json"):
                continue
            path = os.path.join(dirpath, name)
            try:
                doc = json.load(open(path))
            except (OSError, ValueError):
                continue
            samples = doc.get("samples")
            if not isinstance(samples, list) or not samples:
                continue
            # same admission rule as the RESULTS.md renderer: failed/raw
            # attempts never contribute measurements
            if doc.get("status") != "complete":
                continue
            key = lane_key(doc)
            stamp = receipt_nanos(name)
            if key not in lanes or stamp > lanes[key][0]:
                lanes[key] = (stamp, path, doc)
    return lanes


FIELDS = ("throughput_ops_per_s", "throughput_documents_per_s",
          "p50_us", "p50_batch_us", "p95_us", "p95_batch_us",
          "p99_us", "p99_batch_us")


def median_sample(doc):
    def med(field):
        return statistics.median(s[field] for s in doc["samples"] if field in s)

    return {f: med(f) for f in FIELDS if any(f in s for s in doc["samples"])}


def fmt(old, new):
    if old is None or new is None:
        return "-", "-", "-"
    if old == 0:
        return f"{new:.0f}", "new", ""
    pct = (new - old) / old * 100.0
    return f"{old:.0f} → {new:.0f}", f"{pct:+.1f}%", pct


def main():
    old_dir, new_dir = sys.argv[1], sys.argv[2]
    old_lanes = newest_per_lane(old_dir)
    new_lanes = newest_per_lane(new_dir)
    keys = sorted(set(old_lanes) | set(new_lanes))
    rows = []
    for key in keys:
        target, workload = key
        old_m = median_sample(old_lanes[key][2]) if key in old_lanes else None
        new_m = median_sample(new_lanes[key][2]) if key in new_lanes else None

        def pair(field):
            return fmt(old_m and old_m.get(field), new_m and new_m.get(field))

        tps_f = "throughput_documents_per_s" if (
            old_m and "throughput_documents_per_s" in old_m) or (
            new_m and "throughput_documents_per_s" in new_m) else "throughput_ops_per_s"
        p50_f = "p50_batch_us" if "p50_batch_us" in (old_m or new_m or {}) else "p50_us"
        tps, tps_d, _ = pair(tps_f)
        p50, p50_d, _ = pair(p50_f)
        p95, _, _ = pair("p95_batch_us" if p50_f == "p50_batch_us" else "p95_us")
        p99, _, _ = pair("p99_batch_us" if p50_f == "p50_batch_us" else "p99_us")
        rows.append((target, workload, tps, tps_d, p50, p50_d, p95, p99))
    unit = "docs/s" if tps_f.endswith("documents_per_s") else "ops/s"
    print(f"| target | workload | {unit} (old → new) | Δ | p50 us | p50 Δ | p95 us | p99 us |")
    print("| --- | --- | --- | --- | --- | --- | --- | --- |")
    for r in rows:
        print("| " + " | ".join(r) + " |")


if __name__ == "__main__":
    main()
