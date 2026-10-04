#!/usr/bin/env python3
"""Run COMMAND with stdout to OUTPUT and fail if any process in it peaked above LIMIT_KIB of RSS.

usage: peak-rss.py LIMIT_KIB OUTPUT COMMAND...
"""
import resource
import subprocess
import sys

if len(sys.argv) < 4:
    sys.exit(__doc__)
limit_kib, output, command = int(sys.argv[1]), sys.argv[2], sys.argv[3:]
with open(output, "wb") as out:
    subprocess.run(command, stdout=out, check=True)
# The largest resident set of any waited-for descendant (KiB on Linux).
peak = resource.getrusage(resource.RUSAGE_CHILDREN).ru_maxrss
print(f"peak RSS {peak} KiB (limit {limit_kib} KiB)")
sys.exit(0 if peak <= limit_kib else 1)
