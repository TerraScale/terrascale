#!/usr/bin/env python3
"""Run on each serving host: bounded local reads, then two inbound xregion lanes.

Uses RT6 only for tenant lifecycle and native client framing. HTTP reads use a
persistent authenticated client, rather than timing curl process launches.
"""
import concurrent.futures
import http.client
import importlib.util
import json
import math
import os
import pathlib
import signal
import statistics
import subprocess
import sys
import threading
import time

work, region, sha, output, probe, rt6 = sys.argv[1:]
spec = importlib.util.spec_from_file_location("rt6", rt6)
matrix = importlib.util.module_from_spec(spec)
spec.loader.exec_module(matrix)
signal.signal(signal.SIGTERM, lambda *_: sys.exit("terminated; cleaning synthetic tenant"))


class Live(matrix.Matrix):
    def __init__(self, stream):
        self.work, self.sha, self.output = work, sha, stream
        self.lock = threading.Lock()
        self.node = {parts[1]: (parts[0], parts[2]) for line in pathlib.Path(work, "peers").read_text().splitlines()
                     if len(parts := line.split()) == 3}
        if set(self.node) != set(matrix.REGIONS):
            raise RuntimeError("unexpected serving regions")
        matrix.NATIVE_RELAYS.update({region: dict(postgres=5432, wire=7464)})


with open(output, "x") as stream:
    live = Live(stream)
    try:
        revisions = {}
        for target in live.node:
            status, body = live.http(target, "GET", "/readyz")
            if status != 200:
                raise RuntimeError("regional readiness failed")
            revisions[target] = body.get("revision")
        live.record(kind="metadata", host=os.uname().nodename, region=region, revisions=revisions,
                    load=os.getloadavg(), pressure=pathlib.Path('/proc/pressure/cpu').read_text())
        live.provision()
        tables = {}
        for size, count in (("small", 256), ("medium", 4096)):
            table, identifier = "perf_" + size, matrix.ulid()
            live.sql("sjc", "sql", [f"CREATE TABLE {table} (id TEXT PRIMARY KEY, value TEXT)"], live.name)
            live.table_everywhere(table, live.name)
            value = "x" * (count - len('{"value":""}'))
            live.write(region, "documents", table, identifier, value, "insert", live.name)
            tables[size] = (table, identifier, value)
        local = threading.local()

        def read(endpoint, table, identifier, value):
            if endpoint in ("postgres", "wire"):
                result = live.read(region, endpoint, table, identifier, live.name)
                if result != value:
                    raise RuntimeError("native read mismatch")
                return
            if not hasattr(local, "http"):
                local.http = http.client.HTTPConnection("127.0.0.1", 8080, timeout=20)
            if endpoint == "documents":
                method, path, body = "GET", live.document(live.name, table, identifier), None
            else:
                sql = f"SELECT value FROM {table} WHERE id = '{identifier}'"
                method = "POST"
                path = live.data(live.name) + ("/sql/execute" if endpoint == "sql" else "/libsql/v2/pipeline")
                body = json.dumps({"statements": [{"sql": sql}]} if endpoint == "sql" else
                                  {"requests": [{"type": "execute", "stmt": {"sql": sql, "want_rows": True}}, {"type": "close"}]})
            local.http.request(method, path, body, {"Authorization": "Bearer " + live.key, "Content-Type": "application/json"})
            response = local.http.getresponse()
            data = json.loads(response.read())
            if response.status != 200 or value not in json.dumps(data):
                raise RuntimeError("HTTP read failed or value mismatch")

        with concurrent.futures.ThreadPoolExecutor(max_workers=16) as pool:
            for repeat in range(1, 4):
                for size, (table, identifier, value) in tables.items():
                    for concurrency in (1,):
                        for endpoint in matrix.ENDPOINTS:
                            # Live is c=1 read-only after setup; 60 reads bounds production work.
                            barrier = threading.Barrier(concurrency)
                            def worker():
                                read(endpoint, table, identifier, value)
                                barrier.wait(timeout=30)
                                samples = []
                                for _ in range(60):
                                    began = time.perf_counter()
                                    read(endpoint, table, identifier, value)
                                    samples.append((time.perf_counter() - began) * 1000)
                                return samples
                            began = time.perf_counter()
                            samples = sorted(v for future in [pool.submit(worker) for _ in range(concurrency)] for v in future.result())
                            elapsed = time.perf_counter() - began
                            live.record(kind="latency", region=region, endpoint=endpoint, repeat=repeat, size=size,
                                        concurrency=concurrency, samples_ms=samples, p50_ms=statistics.median(samples),
                                        p99_ms=samples[math.ceil(len(samples) * .99) - 1],
                                        ops_per_s=len(samples) / elapsed, rate_window="includes warmup and worker startup")
        key = pathlib.Path(work, "key")
        key.write_text(live.key)
        for repeat in range(1, 4):
            for origin, (_, address) in live.node.items():
                if origin != region:
                    with open(f"{output}.{origin}-{region}-r{repeat}.jsonl", "x") as evidence:
                        subprocess.run([probe, "-origin", f"http://{address}:8080", "-local", "http://127.0.0.1:8080",
                                        "-project", live.project, "-database", live.name, "-collection", tables["small"][0],
                                        "-key-file", str(key), "-label", f"{origin}->{region}", "-n", "60"],
                                       stdout=evidence, check=True)
    finally:
        live.teardown()
