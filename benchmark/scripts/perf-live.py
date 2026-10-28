#!/usr/bin/env python3
"""Run on each serving host: bounded local reads, then two inbound xregion lanes.

Uses RT6 only for tenant lifecycle and native client framing. HTTP reads use a
persistent authenticated client, rather than timing curl process launches.
"""
import concurrent.futures
import hashlib
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

work, region, sha, output, probe, rt6, *operation = sys.argv[1:]
spec = importlib.util.spec_from_file_location("rt6", rt6)
matrix = importlib.util.module_from_spec(spec)
spec.loader.exec_module(matrix)
def ignore_stop_signals():
    for stop in (signal.SIGTERM, signal.SIGINT, signal.SIGHUP):
        signal.signal(stop, signal.SIG_IGN)


def terminate(*_):
    ignore_stop_signals()
    sys.exit("terminated; cleaning synthetic tenant")


for stop in (signal.SIGTERM, signal.SIGINT, signal.SIGHUP):
    signal.signal(stop, terminate)
if not operation:
    subprocess.run([probe, "-h"], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=True)


class Live(matrix.Matrix):
    def __init__(self, stream):
        self.work, self.sha, self.output = work, sha, stream
        self.lock = threading.Lock()
        peers = [line.split() for line in pathlib.Path(work, "peers").read_text().splitlines() if line.strip()]
        if any(len(parts) != 3 for parts in peers) or len({parts[1] for parts in peers}) != len(peers):
            raise RuntimeError("peers must contain one well-formed serving node per region")
        self.node = {parts[1]: (parts[0], parts[2]) for parts in peers}
        if region not in self.node:
            raise RuntimeError("local serving region absent from peers file")
        matrix.REGIONS = tuple(sorted(self.node))
        matrix.NATIVE_RELAYS.update({region: dict(postgres=5432, wire=7464)})

    def record(self, **result):
        result.update(harness_source_hint=self.sha, at=time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()))
        with self.lock:
            self.output.write(json.dumps(result) + "\n")
            self.output.flush()

    def verify_cleanup(self):
        receipt = pathlib.Path(output)
        rows = [json.loads(line) for line in receipt.read_text().splitlines()] if receipt.exists() else []
        identity = {key: row[key] for row in rows for key in ("organization", "project", "database") if key in row}
        identity.update({key: getattr(self, key) for key in ("organization", "project", "database") if hasattr(self, key)})
        if "organization" not in identity:
            for target in self.node:
                _, body = self.http(target, "GET", "/v1/management/organizations", user=True, expect={200})
                if body["items"]:
                    raise RuntimeError("unidentified synthetic tenant remains; run cleanup")
                self.record(kind="teardown", step="acting user empty", target=target, status=200)
            return
        path = "/v1/management/organizations/" + identity["organization"]
        paths = [("organization gone", path)]
        if "project" in identity and "database" in identity:
            paths.append(("database gone", path + "/projects/" + identity["project"] + "/databases/" + identity["database"]))
        for target in self.node:
            for step, path in paths:
                deadline = time.monotonic() + 120
                while self.http(target, "GET", path, user=True)[0] != 404:
                    if time.monotonic() >= deadline:
                        raise RuntimeError(f"{step}: cleanup not observed in {target}")
                    time.sleep(1)
                self.record(kind="teardown", step=step, target=target, status=404)

    def teardown(self):
        ignore_stop_signals()
        if not getattr(self, "organization", None):
            self.recover()
            return
        super().teardown()
        self.verify_cleanup()

    def recover(self):
        ignore_stop_signals()
        # The persisted acting-user ULID belongs only to this run. Recover even if
        # it died between the organization creation response and the tenant log.
        _, body = self.http("sjc", "GET", "/v1/management/organizations", user=True, expect={200})
        if len(body["items"]) > 1:
            raise RuntimeError("unexpected recovery tenant count; review required")
        for organization in body["items"]:
            if organization["name"] != "rt6 endpoint matrix":
                raise RuntimeError("recovery organization is not this benchmark's tenant")
            self.organization = organization["id"]
            path = f"/v1/management/organizations/{self.organization}/projects"
            _, body = self.http("sjc", "GET", path, user=True, expect={200})
            if len(body["items"]) > 1:
                raise RuntimeError("unexpected recovery project count; review required")
            for project in body["items"]:
                if project["name"] != "rt6":
                    raise RuntimeError("recovery project is not this benchmark's tenant")
                self.project, self.management = project["id"], path + "/" + project["id"]
                _, body = self.http("sjc", "GET", self.management + "/databases", user=True, expect={200})
                if len(body["items"]) > 1:
                    raise RuntimeError("unexpected recovery database count; review required")
                if body["items"]:
                    if not body["items"][0]["name"].startswith("rt6-"):
                        raise RuntimeError("recovery database is not this benchmark's tenant")
                    self.database = body["items"][0]["id"]
                receipt = pathlib.Path(output)
                if receipt.exists():
                    for line in receipt.read_text().splitlines():
                        row = json.loads(line)
                        if row.get("kind") == "database":
                            self.key_id = row.get("key_id")
            self.teardown()
        self.verify_cleanup()


if operation:
    if operation != ["cleanup"]:
        raise ValueError("unknown live operation")
    with open(output + ".cleanup.jsonl", "a") as stream:
        Live(stream).recover()
    sys.exit(0)


with open(output, "x") as stream:
    live = Live(stream)
    try:
        revisions = {}
        for target in live.node:
            status, body = live.http(target, "GET", "/readyz")
            if status != 200:
                raise RuntimeError("regional readiness failed")
            revisions[target] = body.get("revision")
        if len(set(revisions.values())) != 1 or any(not isinstance(value, str) or not value for value in revisions.values()):
            raise RuntimeError("serving regions have different or missing revisions")
        live.record(kind="metadata", host=os.uname().nodename, region=region, revisions=revisions,
                    native_ports=matrix.NATIVE_RELAYS[region], documents_per_payload=16,
                    harness_files_sha256={path.name: hashlib.sha256(path.read_bytes()).hexdigest()
                                          for path in (pathlib.Path(__file__), pathlib.Path(__file__).with_suffix('.sh'), pathlib.Path(rt6), pathlib.Path(probe))},
                    load=os.getloadavg(), pressure=pathlib.Path('/proc/pressure/cpu').read_text())
        live.provision()
        tables = {}
        for size, count in (("small", 256), ("medium", 4096)):
            table, identifiers = "perf_" + size, [matrix.ulid() for _ in range(16)]
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
            value = "x" * (count - len('{"value":""}'))
            for identifier in identifiers:
                live.write(region, "documents", table, identifier, value, "insert", live.name)
            tables[size] = (table, identifiers, value)
        local = threading.local()

        def read(endpoint, table, identifiers, value):
            identifier, requested = identifiers[0], []
            if endpoint in ("postgres", "wire"):
                result = live.read(region, endpoint, table, identifier, live.name)
                if result != value:
                    raise RuntimeError("native read mismatch")
                return
            if not hasattr(local, "http"):
                local.http = http.client.HTTPConnection("127.0.0.1", 8080, timeout=20)
            if endpoint == "documents":
                method, path, body = "GET", live.document(live.name, table, identifier), None
            elif endpoint in ("query", "batch"):
                requested = identifiers[:1] if endpoint == "query" else identifiers
                method, path = "POST", live.data(live.name) + f"/collections/{table}/query"
                body = json.dumps({"filter": {"eq": ["$id", identifier]} if endpoint == "query" else
                                   {"in": ["$id", requested]}, "limit": len(requested)})
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
                raise RuntimeError(f"{endpoint}: HTTP {response.status}, value_match={value in json.dumps(data)}")
            if requested and (len(data["items"]) != len(requested) or {item["id"] for item in data["items"]} != set(requested)
                              or any(item["document"]["value"] != value for item in data["items"])):
                raise RuntimeError("document query/batch result mismatch")

        with concurrent.futures.ThreadPoolExecutor(max_workers=16) as pool:
            for repeat in range(1, 4):
                for size, (table, identifier, value) in tables.items():
                    for concurrency in (1,):
                        for endpoint in (*matrix.ENDPOINTS, "query", "batch"):
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
                                    time.sleep(.1)
                                return samples
                            began = time.perf_counter()
                            samples = sorted(v for future in [pool.submit(worker) for _ in range(concurrency)] for v in future.result())
                            elapsed = time.perf_counter() - began
                            live.record(kind="latency", region=region, endpoint=endpoint, repeat=repeat, size=size,
                                        concurrency=concurrency, samples_ms=samples, p50_ms=statistics.median(samples),
                                        p99_ms=samples[math.ceil(len(samples) * .99) - 1],
                                        ops_per_s=len(samples) / elapsed, rate_window="paced at most 10/s; includes warmup")
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
        for target, expected in revisions.items():
            status, body = live.http(target, "GET", "/readyz")
            live.record(kind="revision_end", region=target, revision=body.get("revision"), status=status)
            if status != 200 or body.get("revision") != expected:
                raise RuntimeError("serving revision changed during measurement")
    finally:
        live.teardown()
