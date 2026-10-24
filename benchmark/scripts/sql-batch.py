#!/usr/bin/env python3
"""Times SQL batch writes over POST .../sql/execute and prints a Markdown table.

Each round sends one request of --rows parameterized INSERTs (100-byte text), then one UPDATE of --rows rows, into a
table this run creates and drops. The access token is read from TERRASCALE_TOKEN and never printed.

    TERRASCALE_TOKEN=... nice -n 19 python3 benchmark/scripts/sql-batch.py --project <id> --database <name>
"""

import argparse
import json
import os
import statistics
import sys
import time
import urllib.error
import urllib.request


def execute(url, token, statements):
    request = urllib.request.Request(
        url,
        data=json.dumps({"statements": statements}).encode(),
        headers={"Authorization": "Bearer " + token, "Content-Type": "application/json"},
        method="POST",
    )
    started = time.perf_counter()
    try:
        with urllib.request.urlopen(request, timeout=120) as response:
            body = json.load(response)
    except urllib.error.HTTPError as error:
        sys.exit(f"HTTP {error.code}: {error.read().decode(errors='replace')[:300]}")
    return time.perf_counter() - started, body


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--base", default="https://api.terrascale.tech")
    parser.add_argument("--project", required=True)
    parser.add_argument("--database", required=True)
    parser.add_argument("--rows", type=int, default=250)
    parser.add_argument("--rounds", type=int, default=5)
    args = parser.parse_args()
    token = os.environ.get("TERRASCALE_TOKEN") or sys.exit("TERRASCALE_TOKEN is not set")
    url = f"{args.base}/v1/projects/{args.project}/databases/{args.database}/sql/execute"
    table = f"bench_{time.time_ns() % 10**9}"
    payload = "x" * 100

    execute(url, token, [{"sql": f"CREATE TABLE {table} (id INTEGER PRIMARY KEY, payload TEXT)"}])
    inserts, updates = [], []
    try:
        for round_ in range(args.rounds):
            first = round_ * args.rows
            rows = [{"sql": f"INSERT INTO {table} (id, payload) VALUES (?, ?)", "args": [first + i, payload]} for i in range(args.rows)]
            elapsed, _ = execute(url, token, rows)
            inserts.append(elapsed)
            elapsed, body = execute(url, token, [{
                "sql": f"UPDATE {table} SET payload = ? WHERE id >= ? AND id < ?", "args": [payload[::-1], first, first + args.rows]}])
            assert body["results"][0]["rows_affected"] == args.rows
            updates.append(elapsed)
    finally:
        execute(url, token, [{"sql": f"DROP TABLE {table}"}])

    print(f"| Request ({args.rows} rows) | Rounds | Median s | Min s | Max s | Median ms/row |")
    print("| --- | --- | --- | --- | --- | --- |")
    for name, times in ((f"{args.rows} INSERT statements", inserts), (f"1 UPDATE of {args.rows} rows", updates)):
        median = statistics.median(times)
        print(f"| {name} | {len(times)} | {median:.3f} | {min(times):.3f} | {max(times):.3f} | {1000 * median / args.rows:.2f} |")


if __name__ == "__main__":
    main()
