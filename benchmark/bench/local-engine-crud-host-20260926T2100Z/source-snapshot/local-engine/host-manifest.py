#!/usr/bin/env python3
"""Capture the host process environment for one local SQL benchmark lane."""

import argparse
import json
import os
import platform
import subprocess
from datetime import datetime, timezone
from pathlib import Path


def proc_value(path: str, key: str) -> str:
    for line in Path(path).read_text().splitlines():
        name, separator, value = line.partition(":")
        if separator and name.strip() == key:
            return value.strip()
    raise RuntimeError(f"{key} is missing from {path}")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--database-parent", required=True, type=Path)
    args = parser.parse_args()
    if not args.database_parent.is_dir():
        parser.error("database parent must already exist")
    if args.output.exists():
        parser.error("output must be fresh")

    affinity = proc_value("/proc/self/status", "Cpus_allowed_list")
    cpu_model = proc_value("/proc/cpuinfo", "model name")
    filesystem = subprocess.check_output(
        ["findmnt", "--noheadings", "--output", "FSTYPE", "--target", str(args.database_parent)],
        text=True,
    ).strip()
    mem_total_kib = int(proc_value("/proc/meminfo", "MemTotal").split()[0])
    swap_total_kib = int(proc_value("/proc/meminfo", "SwapTotal").split()[0])
    manifest = {
        "kind": "host-process",
        "captured_utc": datetime.now(timezone.utc).isoformat(),
        "hostname": platform.node(),
        "kernel_release": platform.release(),
        "architecture": platform.machine(),
        "cpu_model": cpu_model,
        "cpu_count_online": os.cpu_count(),
        "cpu_affinity": affinity,
        "process_nice": os.getpriority(os.PRIO_PROCESS, 0),
        "mem_total_kib": mem_total_kib,
        "swap_total_kib": swap_total_kib,
        "filesystem": filesystem,
        "cache_policy": "fresh database path; OS page cache left as-is; no cache eviction",
        "database_parent": str(args.database_parent.resolve()),
        "loadavg_1m_5m_15m": os.getloadavg(),
    }
    with args.output.open("x") as output:
        json.dump(manifest, output, indent=2, sort_keys=True)
        output.write("\n")


if __name__ == "__main__":
    main()
