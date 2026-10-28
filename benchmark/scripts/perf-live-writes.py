#!/usr/bin/env python3
"""Run on one serving host: bounded c=1 document writes against the local node, HTTP and Wire.

Each write is one FULL commit answered after this region's commit (0013e). A synthetic tenant comes from the RT6
matrix (endpoint-replication-proof.py) and is deleted afterwards; writes are paced at most 10/s, 60 per cell.
"""
import http.client
import json
import math
import os
import pathlib
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
matrix.NATIVE_RELAYS.update({region: dict(postgres=5432, wire=7464)})

with open(output, "x") as stream:
    live = matrix.Matrix(work, sha, stream)
    try:
        live.record(kind="metadata", host=os.uname().nodename, region=region, load=os.getloadavg(),
                    pressure_io=pathlib.Path("/proc/pressure/io").read_text(), pressure_cpu=pathlib.Path("/proc/pressure/cpu").read_text())
        live.provision()
        tables = {}
        for size, count in (("small", 256), ("medium", 4096)):
            table = "perfw_" + size
            live.sql("sjc", "sql", [f"CREATE TABLE {table} (id TEXT PRIMARY KEY, value TEXT)"], live.name)
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

        for repeat in range(1, 4):
            for size, (table, identifiers, value) in tables.items():
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
        live.teardown()
