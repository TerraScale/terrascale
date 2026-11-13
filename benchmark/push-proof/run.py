"""Run with R7_PROOF_TAG naming an existing ts-throwaway-account account.

Creates and deletes its proof database; the caller owns account cleanup.
"""

import base64
import json
import os
import pathlib
import statistics
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

TAG = os.environ["R7_PROOF_TAG"]
WRITES = int(os.environ.get("R7_PROOF_WRITES", "20"))
ROOT = pathlib.Path.home() / ".local/state/ts-throwaway-account"
STATE = json.loads((ROOT / (TAG + ".json")).read_text())
KEY = (ROOT / (TAG + ".key")).read_text().strip()
PROJECT = STATE["projectId"]


def bearer():
    form = urllib.parse.urlencode({"grant_type": "client_credentials", "project": PROJECT}).encode()
    headers = {
        "Authorization": "Basic " + base64.b64encode((KEY.removeprefix("sk_")[:26] + ":" + KEY).encode()).decode(),
        "Content-Type": "application/x-www-form-urlencoded",
    }
    request = urllib.request.Request("https://auth.terrascale.tech/terrascale/oauth/token", form, headers)
    with urllib.request.urlopen(request, timeout=20) as response:
        return json.load(response)["access_token"]


TOKEN = bearer()
TOKEN_ISSUED = time.monotonic()


def call(region, path, method="GET", body=None, headers=None):
    global TOKEN, TOKEN_ISSUED
    if time.monotonic() - TOKEN_ISSUED >= 45:
        TOKEN = bearer()
        TOKEN_ISSUED = time.monotonic()
    data = None if body is None else json.dumps(body).encode()
    sent = {"Authorization": "Bearer " + TOKEN, "Content-Type": "application/json", **(headers or {})}
    request = urllib.request.Request("https://" + region + ".api.terrascale.tech" + path, data, sent, method=method)
    started = time.perf_counter()
    try:
        with urllib.request.urlopen(request, timeout=40) as response:
            payload = response.read()
            return response.status, json.loads(payload) if payload else None, {key.lower(): value for key, value in response.headers.items()}, 1000 * (time.perf_counter() - started)
    except urllib.error.HTTPError as error:
        payload = error.read()
        return error.code, json.loads(payload) if payload else None, {key.lower(): value for key, value in error.headers.items()}, 1000 * (time.perf_counter() - started)


def sql(region, statements, headers=None):
    return call(region, f"/v1/projects/{PROJECT}/databases/r7push/sql/execute", "POST",
                {"statements": [{"sql": text} for text in statements]}, headers)


def ulid():
    alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"
    value = (int(time.time() * 1000) << 80) | int.from_bytes(os.urandom(10), "big")
    return "".join(alphabet[(value >> shift) & 31] for shift in range(125, -1, -5))


def percentile(values, q):
    ordered = sorted(values)
    return ordered[min(len(ordered) - 1, max(0, round(q * (len(ordered) - 1))))]


def deadline(check, seconds, what):
    end = time.monotonic() + seconds
    while time.monotonic() < end:
        if check():
            return
        time.sleep(0.2)
    raise RuntimeError("timed out waiting for " + what)


def main():
    databases = f"/v1/projects/{PROJECT}/databases"
    status, body, _, _ = call("gru", databases, "POST", {"name": "r7push", "homeRegion": "gru", "replicaRegions": ["sjc"]})
    if status != 201:
        raise RuntimeError(("create database", status, (body or {}).get("error", {}).get("code")))
    try:
        status, body, _, _ = sql("gru", ["CREATE TABLE events (id TEXT PRIMARY KEY, body TEXT)"])
        if status != 200:
            raise RuntimeError(("create table", status, (body or {}).get("error", {}).get("code")))
        def replica_ready():
            status, _, headers, _ = sql("sjc", ["SELECT count(*) FROM events"])
            return status == 200 and headers.get("x-terrabase-region") == "sjc"

        deadline(replica_ready, 120, "the sjc replica to hold the table")

        results = {}
        for mode, headers in (("durable_home", {"TerraScale-Durability": "home"}), ("forwarded", {})):
            latencies, routes = [], {}
            for _ in range(WRITES):
                row = ulid()
                status, body, answered, elapsed = sql("sjc", [f"INSERT INTO events (id, body) VALUES ('{row}', '{mode}')"], headers)
                if status != 200:
                    raise RuntimeError((mode, status, (body or {}).get("error", {}).get("code")))
                route = answered.get("terrascale-write-route") or ""
                route_fields = dict(field.strip().split("=", 1) for field in route.split(";") if "=" in field)
                expected_commit = "local" if mode == "durable_home" else "home"
                if route_fields.get("commit") != expected_commit:
                    raise RuntimeError((mode, "unexpected commit path", route))
                expected_region = "sjc" if mode == "durable_home" else "gru"
                if answered.get("x-terrabase-region") != expected_region:
                    raise RuntimeError((mode, "wrong answering region", answered.get("x-terrabase-region")))
                routes[route] = routes.get(route, 0) + 1
                latencies.append(elapsed)
                if mode == "durable_home":
                    status, body, home_headers, _ = sql("gru", [f"SELECT body FROM events WHERE id = '{row}'"], {"TerraScale-Write": "home"})
                    if status != 200 or home_headers.get("x-terrabase-region") != "gru" or body["results"][0]["rows"] != [[mode]]:
                        raise RuntimeError(("not at the home when the durable write answered", status))
            results[mode] = {
                "writes": WRITES,
                "p50_ms": round(statistics.median(latencies), 1),
                "p99_ms": round(percentile(latencies, 0.99), 1),
                "max_ms": round(max(latencies), 1),
                "routes": routes,
            }

        status, body, _, _ = sql("gru", ["SELECT body, count(*) FROM events GROUP BY body ORDER BY body"])
        if status != 200:
            raise RuntimeError(("home row counts", status))
        counts = {row[0]: row[1] for row in body["results"][0]["rows"]}
        results["home_rows"] = counts
        results["exactly_once"] = counts == {"durable_home": WRITES, "forwarded": WRITES}
        if not results["exactly_once"]:
            raise RuntimeError(("home row count mismatch", counts))
        print(json.dumps(results), flush=True)
    finally:
        status, _, _, _ = call("gru", databases + "/r7push", "DELETE")
        if status not in (200, 204, 404):
            raise RuntimeError(("delete proof database", status))


if __name__ == "__main__":
    try:
        main()
    except RuntimeError as error:
        print(json.dumps({"failed": str(error)}), flush=True)
        sys.exit(1)
