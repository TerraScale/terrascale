"""Three regional-write repeats, run on the selected serving host.

Credentials come from ts-throwaway-account's private files. No credentials or
request bodies enter the result. A separate connection handles setup/validation.
"""

import argparse
import base64
import contextlib
import datetime
import http.client
import io
import json
import os
import pathlib
import runpy
import statistics
import subprocess
import sys
import time
import urllib.parse
import urllib.request


def ulid():
    alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"
    value = (int(time.time() * 1000) << 80) | int.from_bytes(os.urandom(10), "big")
    return "".join(alphabet[(value >> shift) & 31] for shift in range(125, -1, -5))


def percentile(values, q):
    return sorted(values)[min(len(values) - 1, round(q * (len(values) - 1)))]


def summary(values):
    return {"p50_ms": round(statistics.median(values), 2),
            "p99_ms": round(percentile(values, .99), 2), "samples_ms": [round(v, 2) for v in values]}


class Client:
    def __init__(self, authority, tls=True, fresh=False):
        self.authority, self.tls, self.fresh = authority, tls, fresh
        self.connection = None

    def close(self):
        if self.connection:
            self.connection.close()
        self.connection = None

    def call(self, path, method="GET", body=None, headers=None):
        data = None if body is None else json.dumps(body).encode()
        started = time.perf_counter()
        if self.connection is None:
            kind = http.client.HTTPSConnection if self.tls else http.client.HTTPConnection
            self.connection = kind(self.authority, timeout=40)
        try:
            self.connection.request(method, path, data, headers or {})
            response = self.connection.getresponse()
            payload = response.read()
            elapsed = 1000 * (time.perf_counter() - started)
            answered = {}
            for name, value in response.getheaders():
                name = name.lower()
                answered[name] = answered[name] + ", " + value if name in answered else value
            return response.status, json.loads(payload) if payload else None, answered, elapsed
        finally:
            if self.fresh:
                self.close()


def timing(header):
    result = {}
    for part in header.split(","):
        fields = [field.strip() for field in part.split(";")]
        parameters = dict(field.split("=", 1) for field in fields[1:] if "=" in field)
        if "dur" in parameters:
            label = fields[0] + (":" + parameters["desc"].strip('"') if "desc" in parameters else "")
            # Outer region's stages follow any forwarded home's stages.
            result[label] = float(parameters["dur"])
    return result


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--tag", required=True)
    parser.add_argument("--region", choices=("sjc", "fra"), default="sjc")
    parser.add_argument("--writes", type=int, default=40)
    parser.add_argument("--repeats", type=int, default=3)
    parser.add_argument("--start-repeat", type=int, default=1)
    parser.add_argument("--request-logs", action="store_true")
    parser.add_argument("--expect-local", action="store_true")
    parser.add_argument("--transports", nargs="+", choices=("fresh_https", "keepalive_https", "keepalive_node"),
                        default=["fresh_https", "keepalive_https", "keepalive_node"])
    parser.add_argument("--output", required=True)
    parser.add_argument("--snapshot-script", type=pathlib.Path)
    args = parser.parse_args()
    internal_get = None
    log_addresses = []
    if args.request_logs:
        if not args.snapshot_script:
            parser.error("--request-logs needs --snapshot-script on the serving host")
        previous_argv = sys.argv
        try:
            sys.argv = [str(args.snapshot_script)]
            with contextlib.redirect_stdout(io.StringIO()):
                runtime = runpy.run_path(str(args.snapshot_script))
                internal_get = runtime["get"]
                log_addresses = [address for _, region, address in runtime["peers"] if region == args.region]
        finally:
            sys.argv = previous_argv
    root = pathlib.Path(os.environ.get("WRITE_PROOF_ACCOUNT_ROOT", pathlib.Path.home() / ".local/state/ts-throwaway-account"))
    state = json.loads((root / (args.tag + ".json")).read_text())
    key = (root / (args.tag + ".key")).read_text().strip()
    project = state["projectId"]
    authority = args.region + ".api.terrascale.tech"
    control = Client("gru.api.terrascale.tech")
    token, issued = None, 0

    def headers(extra=None):
        nonlocal token, issued
        if time.monotonic() - issued >= 40:
            form = urllib.parse.urlencode({"grant_type": "client_credentials", "project": project})
            # Token endpoint takes a form, so keep it outside the timed client helper.
            request = urllib.request.Request("https://auth.terrascale.tech/terrascale/oauth/token", form.encode(), {
                "Authorization": "Basic " + base64.b64encode((key.removeprefix("sk_")[:26] + ":" + key).encode()).decode(),
                "Content-Type": "application/x-www-form-urlencoded"})
            with urllib.request.urlopen(request, timeout=20) as response:
                token = json.load(response)["access_token"]
            issued = time.monotonic()
        return {"Authorization": "Bearer " + token, "Content-Type": "application/json", "Host": authority, **(extra or {})}

    def require(status, body, expected, operation):
        if status != expected:
            raise RuntimeError((operation, status, (body or {}).get("error", {}).get("code"), (body or {}).get("error", {}).get("message")))

    report = {"captured_at": datetime.datetime.now(datetime.UTC).isoformat(), "client_host": os.uname().nodename, "client_region": args.region,
              "writes_per_mode": args.writes, "default_expects_local": args.expect_local, "repeats": []}
    for repeat in range(args.start_repeat, args.start_repeat + args.repeats):
        name = "latency" + args.region + str(repeat)
        databases = f"/v1/projects/{project}/databases"
        path = databases + "/" + name + "/sql/execute"

        def sql(client, text, extra=None, path=path):
            return client.call(path, "POST", {"statements": [{"sql": text}]}, headers(extra))

        status, body, _, _ = control.call(databases, "POST", {"name": name, "homeRegion": "gru", "replicaRegions": [args.region]}, headers({"Host": "gru.api.terrascale.tech"}))
        require(status, body, 201, "create database")
        database_id = body["id"]
        samples = {"repeat": repeat, "database_id": database_id, "transports": {}, "cleanup_complete": False}
        report["repeats"].append(samples)
        pathlib.Path(args.output).write_text(json.dumps(report, indent=2) + "\n")
        try:
            if args.snapshot_script:
                samples["before"] = json.loads(subprocess.check_output(["python3", str(args.snapshot_script), "--health-samples", "1"]))
            status, body, _, _ = sql(control, "CREATE TABLE events (id TEXT PRIMARY KEY, body TEXT)", {"Host": "gru.api.terrascale.tech"})
            require(status, body, 200, "create table")
            for transport, client in (("fresh_https", Client(authority, fresh=True)),
                                      ("keepalive_https", Client(authority)),
                                      ("keepalive_node", Client("127.0.0.1:8080", tls=False))):
                if transport not in args.transports:
                    continue
                try:
                    end = time.monotonic() + 120
                    while True:
                        status, body, answered, _ = sql(client, "SELECT count(*) FROM events")
                        if status == 200 and answered.get("x-terrabase-region") == args.region:
                            break
                        if time.monotonic() >= end:
                            raise RuntimeError("replica did not become ready")
                        time.sleep(.2)
                    health = []
                    for _ in range(args.writes):
                        status, body, _, elapsed = client.call("/healthz", headers={"Host": authority})
                        require(status, body, 200, "health")
                        health.append(elapsed)
                    modes = {"health": summary(health)}
                    for mode, extra in (("default", {}), ("durable_home", {"TerraScale-Durability": "home"}),
                                        ("forced_home", {"TerraScale-Write": "home"})):
                        latencies, stages, routes, request_ids, completed_at = [], {}, {}, [], []
                        for index in range(args.writes + 5):
                            row = ulid()
                            label = f"{transport}_{mode}" if index >= 5 else "warmup"
                            status, body, answered, elapsed = sql(client, f"INSERT INTO events (id, body) VALUES ('{row}', '{label}')", extra)
                            completed = datetime.datetime.now(datetime.UTC).isoformat()
                            require(status, body, 200, mode)
                            route = answered.get("terrascale-write-route", "")
                            fields = dict(field.strip().split("=", 1) for field in route.split(";") if "=" in field)
                            expected = "local" if mode == "durable_home" or (mode == "default" and args.expect_local) else "home"
                            if fields.get("commit") != expected:
                                raise RuntimeError((mode, "unexpected commit route", route))
                            if index >= 5:
                                request_ids.append(answered.get("x-request-id"))
                                completed_at.append(completed)
                                latencies.append(elapsed)
                                routes[route] = routes.get(route, 0) + 1
                                for stage, duration in timing(answered.get("server-timing", "")).items():
                                    stages.setdefault(stage, []).append(duration)
                        modes[mode] = {"client": summary(latencies), "server": {stage: summary(values) for stage, values in stages.items()}, "routes": routes,
                                       "completed_at": completed_at}
                        if args.request_logs:
                            if None in request_ids or len(set(request_ids)) != args.writes:
                                raise RuntimeError("response request IDs are missing or repeated")
                            end = time.monotonic() + 5
                            while True:
                                recorded = {}
                                for address in log_addresses:
                                    payload = internal_get(f"v1/usage/projects/{project}/databases/{database_id}/logs", address, missing_ok=True)
                                    if payload is None:
                                        continue
                                    body = json.loads(payload)
                                    recorded.update({row.get("requestId"): row["latencyMs"] for row in body["items"] if row["region"] == args.region})
                                if all(request_id in recorded for request_id in request_ids):
                                    break
                                if time.monotonic() >= end:
                                    raise RuntimeError(("missing correlated regional request logs", len(set(request_ids) - recorded.keys())))
                                time.sleep(.05)
                            modes[mode]["request_ids"] = request_ids
                            modes[mode]["server"]["request_log:" + args.region] = summary([recorded[request_id] for request_id in request_ids])
                        print(json.dumps({"repeat": repeat, "transport": transport, "mode": mode, "client_p50_ms": modes[mode]["client"]["p50_ms"], "client_p99_ms": modes[mode]["client"]["p99_ms"]}), flush=True)
                    samples["transports"][transport] = modes
                finally:
                    client.close()
            status, body, _, _ = sql(control, "SELECT body, count(*) FROM events GROUP BY body ORDER BY body", {"TerraScale-Write": "home", "Host": "gru.api.terrascale.tech"})
            require(status, body, 200, "home counts")
            counts = dict(body["results"][0]["rows"])
            if any(counts.get(f"{transport}_{mode}") != args.writes for transport in samples["transports"] for mode in ("default", "durable_home", "forced_home")):
                raise RuntimeError(("incorrect home counts", counts))
            samples["home_counts"] = counts
        finally:
            status, body, _, _ = control.call(databases + "/" + name, "DELETE", headers=headers({"Host": "gru.api.terrascale.tech"}))
            samples["cleanup_http_status"] = status
            samples["cleanup_complete"] = status == 204
            pathlib.Path(args.output).write_text(json.dumps(report, indent=2) + "\n")
            require(status, body, 204, "delete database")
        if args.snapshot_script:
            # Preserve measured samples if a concurrent rollout prevents the final snapshot.
            samples["source_stable"] = False
            samples["snapshot_complete"] = False
            pathlib.Path(args.output).write_text(json.dumps(report, indent=2) + "\n")
            samples["after"] = json.loads(subprocess.check_output(["python3", str(args.snapshot_script), "--health-samples", "1"]))
            revisions = lambda snapshot: {node: data["health"]["revision"] for node, data in snapshot["peer_internal_health_ms"].items()}
            samples["source_stable"] = revisions(samples["before"]) == revisions(samples["after"])
            samples["snapshot_complete"] = True
        pathlib.Path(args.output).write_text(json.dumps(report, indent=2) + "\n")
        print(json.dumps({"repeat": repeat, "result": "verified and deleted", "output": args.output}), flush=True)
    control.close()


if __name__ == "__main__":
    main()
