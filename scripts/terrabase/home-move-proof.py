#!/usr/bin/env python3
"""Issue #1106 public SDK reproduction with a disposable Free account and exact IDs."""
from __future__ import annotations

import concurrent.futures
import contextlib
import datetime
import json
import pathlib
import subprocess
import sys
import threading
import time

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[2] / "sdk/python/src"))
import httpx
from terrascale import TerraScale
from terrascale.errors import APIError
from terrascale.ids import new_id


def utc():
    return datetime.datetime.now(datetime.timezone.utc).isoformat()


def main():
    tag = "move1106-" + new_id().lower()
    events = []
    lock = threading.Lock()
    clients = contextlib.ExitStack()
    summaries = []
    last = threading.local()

    def record(event):
        with lock:
            events.append(event)
            print(json.dumps(event), flush=True)

    def response_hook(response):
        if response.request.url.host.endswith("api.terrascale.tech"):
            last.response = {"request_id": response.headers.get("x-request-id"),
                             "region": response.headers.get("x-terrabase-region"),
                             "route": response.headers.get("terrascale-write-route")}
            record({"kind": "http", "at": utc(), "method": response.request.method,
                    "path": response.request.url.path, "status": response.status_code,
                    "request_id": response.headers.get("x-request-id"),
                    "region": response.headers.get("x-terrabase-region"),
                    "route": response.headers.get("terrascale-write-route"),
                    "timing": response.headers.get("server-timing")})

    def sdk(region, pin=True):
        http = clients.enter_context(httpx.Client(http2=True, timeout=90,
                                     event_hooks={"response": [response_hook]}))
        return clients.enter_context(TerraScale(api_key=key, project=project,
                       base_url=f"https://{region}.api.terrascale.tech",
                       region=region if pin else None, http_client=http, max_attempts=1))

    try:
        created = subprocess.run(["ts-throwaway-account", tag], capture_output=True, text=True, check=True)
        key = pathlib.Path(created.stdout.strip()).read_text().strip()
        state = json.loads((pathlib.Path.home() / ".local/state/ts-throwaway-account" / (tag + ".json")).read_text())
        project = state["projectId"]
        gru, sjc, fra, unpinned = sdk("gru"), sdk("sjc"), sdk("fra"), sdk("sjc", False)
        for trial in range(1, 4):
            database = gru.create_database("move1106-" + new_id().lower(), home_region="gru", all_regions=True)
            home = gru
            try:
                db = gru.database(database.id)
                db.execute("CREATE TABLE acceptance_rows (id TEXT PRIMARY KEY, writer TEXT, sequence INTEGER, body TEXT)")
                expected = {}
                for start in range(0, 1923, 100):
                    batch = []
                    for sequence in range(start, min(start + 100, 1923)):
                        row = new_id()
                        values = [row, "baseline", sequence, "baseline"]
                        batch.append(("INSERT INTO acceptance_rows (id, writer, sequence, body) VALUES (?, ?, ?, ?)", values))
                        expected[row] = values
                    db.batch(batch)

                # Confirm SJC's actual replica is serving before beginning measured load.
                deadline = time.monotonic() + 90
                while True:
                    try:
                        local = sjc.database(database.id).query("SELECT id FROM acceptance_rows")
                        if {row[0] for row in local} == set(expected):
                            break
                    except APIError:
                        pass
                    if time.monotonic() >= deadline:
                        raise AssertionError("SJC did not hold the reconciled baseline")
                    threading.Event().wait(0.5)
                third = threading.Event()
                moved = threading.Event()
                accepted = []
                attempts = []
                record({"kind": "baseline", "trial": trial, "database": database.id,
                        "expected_ids": sorted(expected)})

                def writer():
                    for sequence in range(12):
                        row = new_id()
                        values = [row, "sjc", sequence, "move-load"]
                        started = utc()
                        try:
                            sjc.database(database.id).execute("INSERT INTO acceptance_rows (id, writer, sequence, body) VALUES (?, ?, ?, ?)", values)
                            completed = utc()
                            expected[row] = values
                            accepted.append({"id": row, "sequence": sequence, "started": started, "completed": completed, **last.response})
                            record({"kind": "accepted", "trial": trial, "database": database.id, **accepted[-1]})
                            if "commit=local" not in (last.response["route"] or ""):
                                raise AssertionError("SJC write did not receive a local commit acknowledgement")
                            if len(accepted) == 3:
                                third.set()
                        except APIError as error:
                            attempts.append({"id": row, "sequence": sequence, "status": error.status, "code": error.code})
                            record({"kind": "refused", "trial": trial, "database": database.id, **attempts[-1]})
                        if moved.is_set() and len(accepted) >= 6:
                            break
                        threading.Event().wait(0.5)
                    third.set()

                with concurrent.futures.ThreadPoolExecutor(max_workers=1) as pool:
                    future = pool.submit(writer)
                    future.add_done_callback(lambda _: third.set())
                    if not third.wait(30) or len(accepted) < 3:
                        if future.done():
                            future.result()
                        raise AssertionError("three local writes were not acknowledged before the move")
                    move_started = utc()
                    try:
                        result = gru.move_database_home(database.id, "fra")
                        if result.home_region != "fra":
                            raise AssertionError("move did not record FRA")
                        home = fra
                        record({"kind": "move", "trial": trial, "database": database.id,
                                "started": move_started, "completed": utc(), **last.response})
                    finally:
                        move_completed = utc()
                        moved.set()
                    future.result(timeout=90)

                during = [ack for ack in accepted if move_started <= ack["completed"] <= move_completed]
                if not during:
                    raise AssertionError("reproduction was inconclusive: no local acknowledgement during the move")
                checks = []
                for region, client in [("gru", gru), ("fra", fra), ("sjc", sjc), ("sjc-unpinned", unpinned)]:
                    deadline = time.monotonic() + 90
                    while True:
                        try:
                            rows = client.database(database.id).query("SELECT id, writer, sequence, body FROM acceptance_rows ORDER BY id")
                        except APIError as error:
                            if error.status not in (404, 421, 503) or time.monotonic() >= deadline:
                                raise
                            threading.Event().wait(0.5)
                            continue
                        if last.response["region"] != region.split("-")[0]:
                            raise AssertionError("placed regional read was served in another region")
                        actual = {row[0]: list(row) for row in rows}
                        missing = sorted(set(expected) - set(actual))
                        unexpected = sorted(set(actual) - set(expected))
                        duplicates = len(rows) - len(actual)
                        changed = sorted(row for row in set(expected) & set(actual) if expected[row] != actual[row])
                        if not missing and not unexpected and not duplicates and not changed:
                            checks.append({"region": region, "count": len(rows), "missing": [], "unexpected": [], "duplicates": 0, "changed": []})
                            break
                        if time.monotonic() >= deadline:
                            record({"kind": "reconciliation_failure", "region": region, "missing": missing,
                                    "unexpected": unexpected, "duplicates": duplicates, "changed": changed})
                            raise AssertionError("acknowledged ID reconciliation failed")
                        threading.Event().wait(0.5)
                summary = {"kind": "trial", "trial": trial, "database": database.id, "move_started": move_started,
                           "move_completed": move_completed, "accepted": accepted, "refused": attempts,
                           "accepted_during_move": during, "expected_ids": sorted(expected), "checks": checks}
                record(summary)
                summaries.append(summary)
            finally:
                try:
                    home.delete_database(database.id)
                except APIError as error:
                    if error.status != 421:
                        raise
                    (fra if home is gru else gru).delete_database(database.id)
        record({"kind": "result", "trials_passed": len(summaries), "trials_required": 3})
    finally:
        clients.close()
        cleanup = subprocess.run(["ts-throwaway-account", "--delete", tag], capture_output=True, text=True)
        record({"kind": "cleanup", "tag": tag, "deleted": cleanup.returncode == 0})
        if cleanup.returncode:
            raise RuntimeError("owned synthetic account cleanup needs completion")


if __name__ == "__main__":
    main()
