#!/usr/bin/env python3
"""Helper of local-idle.sh.

  local-idle.py provision <targets> <placed> <single>   creates <placed> databases in every region (home gru) and
                                                        <single> databases in gru and sjc only, through the project token
  local-idle.py rates <out> <window>                    per-node rates between metrics-a-* and metrics-b-*, as Markdown
"""
import json
import os
import re
import sys
import time
import urllib.error
import urllib.request

REGIONS = ("gru", "sjc", "fra")


def provision(path, placed, single):
    with open(path, encoding="utf-8") as stream:
        targets = json.load(stream)
    gru = targets["regions"]["gru"]
    for index in range(placed + single):
        body = {"name": f"idle-{index}", "homeRegion": "gru", "replicaRegions": ["sjc", "fra"] if index < placed else ["sjc"]}
        request = urllib.request.Request(gru["url"].rstrip("/") + f"/v1/projects/{targets['project']}/databases",
                                         data=json.dumps(body).encode(), method="POST",
                                         headers={"Authorization": "Bearer " + gru["token"], "Content-Type": "application/json"})
        for attempt in range(30):
            try:
                with urllib.request.urlopen(request, timeout=30) as response:
                    print(index, response.status)
                    break
            except urllib.error.HTTPError as error:
                if error.code not in (429, 503) or attempt == 29:
                    raise RuntimeError(f"database {index}: {error.code} {error.read()[:300]!r}") from error
                time.sleep(1)


METRIC = re.compile(r'^(terrabase_[a-z_]+)\{(.*)\} ([0-9.e+-]+)$')
WATCHED = (
    ("send passes", "terrabase_replication_step_duration_seconds_count", 'step="send.pass"'),
    ("send reads", "terrabase_replication_step_duration_seconds_count", 'step="send.read"'),
    ("I/O calls, other", "terrabase_storage_queue_wait_seconds_count", 'lane="other"'),
    ("point reads", "terrabase_storage_reads_total", 'path="point"'),
    ("query reads", "terrabase_storage_reads_total", 'path="query"'),
    ("placement misses", "terrabase_catalog_cache_lookups_total", 'cache="placements",result="miss"'),
    ("writer holds", "terrabase_storage_writer_held_seconds_count", ""),
)


def load(path):
    values = {}
    with open(path, encoding="utf-8") as stream:
        for line in stream:
            match = METRIC.match(line.strip())
            if match:
                values[(match[1], re.sub(r'otel_scope_name="[^"]*",?', "", match[2]))] = float(match[3])
    return values


def rates(out, window):
    print("| Per second | " + " | ".join(REGIONS) + " |")
    print("| --- | " + " | ".join("---:" for _ in REGIONS) + " |")
    before = {region: load(f"{out}/metrics-a-{region}.txt") for region in REGIONS}
    after = {region: load(f"{out}/metrics-b-{region}.txt") for region in REGIONS}
    for label, name, labels in WATCHED:
        cells = []
        for region in REGIONS:
            total = sum(value - before[region].get(key, 0) for key, value in after[region].items()
                        if key[0] == name and labels in key[1])
            cells.append(f"{total / window:.1f}")
        print(f"| {label} | " + " | ".join(cells) + " |")
    ticks = [int(open(f"{out}/cpu-{point}.txt", encoding="utf-8").read()) for point in "ab"]
    print(f"\nHost process CPU (three nodes): {(ticks[1] - ticks[0]) / os.sysconf('SC_CLK_TCK') / window * 100:.1f} % of one core")


if __name__ == "__main__":
    if sys.argv[1] == "provision":
        provision(sys.argv[2], int(sys.argv[3]), int(sys.argv[4]))
    elif sys.argv[1] == "rates":
        rates(sys.argv[2], float(sys.argv[3]))
    else:
        raise SystemExit(__doc__)
