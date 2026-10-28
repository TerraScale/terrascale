#!/usr/bin/env python3
"""Run on one serving host: bounded c=1 document writes (HTTP and Wire) and point reads (document, SQL, Hrana) locally.

Each write is one FULL commit answered after this region's commit (0013e). A synthetic organization, project, database
(this region and its pair, PAIR) and key are created through the local region's internal listener and deleted afterwards, their
absence checked in every region; operations are paced at most 10/s, 60 per cell.
"""
import http.client
import json
import math
import os
import pathlib
import secrets
import signal
import statistics
import sys
import time
import importlib.util

# perf-live.sh's arguments; the xregion probe is not used here.
work, region, sha, output, _probe, rt6 = sys.argv[1:7]
for stop in (signal.SIGTERM, signal.SIGINT, signal.SIGHUP):
    signal.signal(stop, lambda *_: sys.exit("terminated; cleaning synthetic tenant"))

spec = importlib.util.spec_from_file_location("rt6", rt6)
matrix = importlib.util.module_from_spec(spec)
spec.loader.exec_module(matrix)
# A database needs two regions; gru and fra pair with each other, sjc with gru.
PAIR = {"gru": "fra", "fra": "gru", "sjc": "gru"}
matrix.NATIVE_RELAYS.update({region: dict(postgres=5432, wire=7464)})

with open(output, "x") as stream:
    live = matrix.Matrix(work, sha, stream)
    try:
        live.record(kind="metadata", host=os.uname().nodename, region=region, load=os.getloadavg(),
                    pressure_io=pathlib.Path("/proc/pressure/io").read_text(), pressure_cpu=pathlib.Path("/proc/pressure/cpu").read_text())
        _, body = live.http(region, "POST", "/v1/management/organizations", {"name": "rt6 endpoint matrix"}, user=True, expect={201})
        live.organization = body["id"]
        _, body = live.http(region, "POST", f"/v1/management/organizations/{live.organization}/projects", {"name": "rt6"}, user=True, expect={201})
        live.project = body["id"]
        live.management = f"/v1/management/organizations/{live.organization}/projects/{live.project}"
        live.record(kind="tenant", organization=live.organization, project=live.project)
        live.name = "rt6-" + secrets.token_hex(4)
        _, body = live.http(region, "POST", live.management + "/databases", {"name": live.name, "homeRegion": region, "replicaRegions": [PAIR[region]]},
                            user=True, expect={201})
        live.database = body["id"]
        _, body = live.http(region, "POST", "/admin/api-keys", {"name": "rt6 matrix", "scopes": ["documents:read", "documents:write"],
                            "organizationId": live.organization, "projectId": live.project, "databaseId": live.database}, expect={201})
        live.key_id, live.key = body["id"], body["secret"]
        live.record(kind="database", database=live.database, name=live.name, key_id=live.key_id, regions=[region, PAIR[region]])
        tables = {}
        for size, count in (("small", 256), ("medium", 4096)):
            table = "perfw_" + size
            live.sql(region, "sql", [f"CREATE TABLE {table} (id TEXT PRIMARY KEY, value TEXT)"], live.name)
            deadline = time.monotonic() + 120
            while True:
                try:
                    live.sql(region, "sql", [f"SELECT value FROM {table}"], live.name)
                    break
                except matrix.HttpError:
                    if time.monotonic() >= deadline:
                        raise
                    time.sleep(.1)
            identifiers = [matrix.ulid() for _ in range(16)]
            value = "x" * (count - len('{"value":""}'))
            for identifier in identifiers:
                live.write(region, "documents", table, identifier, value, "insert", live.name)
            tables[size] = (table, identifiers, value)

        client = http.client.HTTPConnection("127.0.0.1", 8080, timeout=20)

        def put(endpoint, table, identifier, value):
            if endpoint == "wire":
                status = live.wire(region).put(table, identifier, {"value": value})
                if status != 200:
                    raise RuntimeError(f"wire put {status}")
                return
            client.request("PUT", live.document(live.name, table, identifier), json.dumps({"value": value}),
                           {"Authorization": "Bearer " + live.key, "Content-Type": "application/json"})
            response = client.getresponse()
            response.read()
            if response.status not in (200, 201):
                raise RuntimeError(f"http put {response.status}")

        def get(endpoint, table, identifier, value):
            # P1's live statements (perf-live.py), so its live baseline is the comparison.
            sql = f"SELECT value FROM {table} WHERE id = '{identifier}'"
            if endpoint == "documents":
                method, path, body = "GET", live.document(live.name, table, identifier), None
            elif endpoint == "sql":
                method, path, body = "POST", live.data(live.name) + "/sql/execute", json.dumps({"statements": [{"sql": sql}]})
            else:
                method, path = "POST", live.data(live.name) + "/libsql/v2/pipeline"
                body = json.dumps({"requests": [{"type": "execute", "stmt": {"sql": sql, "want_rows": True}}, {"type": "close"}]})
            client.request(method, path, body, {"Authorization": "Bearer " + live.key, "Content-Type": "application/json"})
            response = client.getresponse()
            data = response.read()
            if response.status != 200 or value.encode() not in data:
                raise RuntimeError(f"{endpoint} read {response.status}")

        for repeat in range(1, 4):
            for size, (table, identifiers, value) in tables.items():
                for endpoint in ("documents", "sql", "hrana"):
                    get(endpoint, table, identifiers[0], value)
                    samples = []
                    for _ in range(60):
                        began = time.perf_counter()
                        get(endpoint, table, identifiers[0], value)
                        samples.append((time.perf_counter() - began) * 1000)
                        time.sleep(.1)
                    samples.sort()
                    live.record(kind="read_latency", region=region, endpoint=endpoint, repeat=repeat, size=size, concurrency=1,
                                samples_ms=samples, p50_ms=statistics.median(samples), p99_ms=samples[math.ceil(len(samples) * .99) - 1],
                                rate_window="paced at most 10/s; one warmup read")
                for endpoint in ("documents", "wire"):
                    put(endpoint, table, identifiers[0], value)
                    samples = []
                    for i in range(60):
                        began = time.perf_counter()
                        put(endpoint, table, identifiers[i % len(identifiers)], value)
                        samples.append((time.perf_counter() - began) * 1000)
                        time.sleep(.1)
                    samples.sort()
                    live.record(kind="write_latency", region=region, endpoint=endpoint, repeat=repeat, size=size, concurrency=1,
                                samples_ms=samples, p50_ms=statistics.median(samples), p99_ms=samples[math.ceil(len(samples) * .99) - 1],
                                rate_window="paced at most 10/s; one warmup write")
    finally:
        for stop in (signal.SIGTERM, signal.SIGINT, signal.SIGHUP):
            signal.signal(stop, signal.SIG_IGN)
        # Through this region, as everything was created: another region's outage must not strand the tenant.
        if getattr(live, "key_id", None):
            live.record(kind="teardown", step="revoke key", status=live.http(region, "DELETE", "/admin/api-keys/" + live.key_id)[0])
        if getattr(live, "database", None):
            live.record(kind="teardown", step="delete database", status=live.http(region, "DELETE", live.management + "/databases/" + live.database, user=True)[0])
        if getattr(live, "project", None):
            live.record(kind="teardown", step="delete project", status=live.http(region, "DELETE", live.management, user=True)[0])
        if getattr(live, "organization", None):
            path = "/v1/management/organizations/" + live.organization
            live.record(kind="teardown", step="delete organization", status=live.http(region, "DELETE", path, user=True)[0])
            for target in live.node:
                deadline = time.monotonic() + 600
                while True:
                    try:
                        status = live.http(target, "GET", path, user=True)[0]
                    except ConnectionError:
                        status = None
                    if status == 404:
                        break
                    if time.monotonic() >= deadline:
                        raise RuntimeError(f"organization still visible in {target}: {status}")
                    time.sleep(5)
                live.record(kind="teardown", step="organization gone", target=target, status=status)
