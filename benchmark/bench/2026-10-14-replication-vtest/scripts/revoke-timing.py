"""V4 row 11: cross-region API-key revocation latency on ts-vtest-tb. Usage: python3 -I revoke-timing.py <trials> <out.jsonl> (0 trials: cleanup only)

Creates a test database (gru home, sjc + fra replicas) as the synthetic owner in state.env, then per trial: mints a
documents:read key through gru's operator API (flyctl proxy to gru's internal listener), waits until every region accepts
it (404 for a missing document), revokes it through gru, and polls sjc and fra (machine-pinned, one request every 50 ms
per region over 8 keep-alive connections) until each answers 401. Times are from the revoke's 204.
The key secret stays in this process's memory; only key ids are written. The database is deleted at the end.
"""
import http.client, json, os, statistics, subprocess, sys, threading, time

here = os.path.dirname(os.path.abspath(__file__))
state = dict(kv.split("=", 1) for line in open(os.path.join(here, "..", "state.env")) for kv in line.split())
APP, HOST, OP = "ts-vtest-tb", "ts-vtest-tb.fly.dev", "http://127.0.0.1:18185"
MACHINES = {"sjc": state["S"], "fra": state["F"]}
INTERVAL, WORKERS = 0.05, 8


def op(method, path, body=None):
    args = ["curl", "-sS", "-m", "30", "--fail-with-body", "--http2-prior-knowledge", "-X", method, "-H", "X-TerraBase-User-Id: " + state["owner"],
            "-H", "content-type: application/json", "-w", "\n%{http_code}", OP + path]
    if body is not None:
        args[1:1] = ["-d", json.dumps(body)]
    for _ in range(60):
        done = subprocess.run(args, capture_output=True, text=True)
        if "tenant_busy" not in done.stdout:
            break
        time.sleep(1)  # 429 before execution: the organization's request budget, safe to send again
    if done.returncode != 0:
        raise RuntimeError(f"{method} {path}: curl exit {done.returncode}: {done.stderr.strip()} {done.stdout.strip()[:300]}")
    out = done.stdout.rsplit("\n", 1)
    return int(out[1]), (json.loads(out[0]) if out[0].strip() else None)


def get(conn, path, key, machine):
    conn.request("GET", path, headers={"Authorization": "Bearer " + key, "fly-force-instance-id": machine})
    response = conn.getresponse()
    response.read()
    return response.status


def trial(n, db_path, project, org):
    status, issued = op("POST", "/admin/api-keys", {"name": f"v4-revoke-{n}", "scopes": ["documents:read"], "organizationId": org,
                                                    "projectId": project, "expiresAt": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime(time.time() + 3600))})
    key, key_id = issued["secret"], issued["id"]
    del issued
    path = f"{db_path}/collections/probe/documents/by-key?id=none"
    ready = {}
    for region, machine in MACHINES.items():
        conn, start = http.client.HTTPSConnection(HOST, timeout=10), time.monotonic()
        while (code := get(conn, path, key, machine)) == 401 and time.monotonic() - start < 60:
            time.sleep(0.2)
        ready[region] = code
    results, done = {r: [] for r in MACHINES}, threading.Event()
    revoked = {}

    t0 = time.monotonic()

    # Worker i of a region sends at t0 + 50 ms * (i + k * WORKERS): one request per region every 50 ms (the organization's
    # request budget answers 429 above that), with enough connections to cover a cross-region round trip.
    def poll(region, machine, i):
        conn, slot = http.client.HTTPSConnection(HOST, timeout=10), t0 + INTERVAL * i
        while not done.is_set():
            time.sleep(max(0.0, slot - time.monotonic()))
            slot += INTERVAL * WORKERS
            sent = time.monotonic()
            try:
                code = get(conn, path, key, machine)
            except (OSError, http.client.HTTPException):
                conn, code = http.client.HTTPSConnection(HOST, timeout=10), 0
            results[region].append((sent, time.monotonic(), code))

    threads = [threading.Thread(target=poll, args=(r, m, i), daemon=True) for r, m in MACHINES.items() for i in range(WORKERS)]
    for t in threads:
        t.start()
    time.sleep(1.0)
    revoked["sent"] = time.monotonic()
    code, _ = op("DELETE", f"/v1/management/organizations/{org}/projects/{project}/api-keys/{key_id}")
    revoked["ack"], revoked["code"] = time.monotonic(), code
    deadline = revoked["ack"] + 60
    while time.monotonic() < deadline and not all(any(c == 401 and s >= revoked["sent"] for s, _, c in results[r]) for r in MACHINES):
        time.sleep(0.05)
    time.sleep(0.3)
    done.set()
    for t in threads:
        t.join()
    del key
    row = {"trial": n, "key_id": key_id, "ready_codes": ready, "revoke_status": revoked["code"],
           "revoke_rtt_ms": round((revoked["ack"] - revoked["sent"]) * 1000, 1)}
    for region, samples in results.items():
        after = sorted(s for s in samples if s[0] >= revoked["sent"])
        first = next((s for s in after if s[2] == 401), None)
        accepted_after_ack = [s for s in after if s[2] != 401 and s[0] >= revoked["ack"]]
        row[region] = {
            "first_401_sent_ms": None if first is None else round((first[0] - revoked["ack"]) * 1000, 1),
            "first_401_received_ms": None if first is None else round((first[1] - revoked["ack"]) * 1000, 1),
            "last_accept_sent_after_ack_ms": round((accepted_after_ack[-1][0] - revoked["ack"]) * 1000, 1) if accepted_after_ack else None,
            "requests_after_revoke": len(after), "throttled_429": sum(1 for s in after if s[2] == 429),
            "median_interval_ms": round(statistics.median(b[0] - a[0] for a, b in zip(after, after[1:])) * 1000, 1) if len(after) > 2 else None,
        }
    return row


def cleanup(org, project):
    """Revokes every v4-revoke-* key and deletes every v4-revoke-* database (replicas removed first) of the run's project."""
    base = f"/v1/management/organizations/{org}/projects/{project}"
    for key in op("GET", base + "/api-keys")[1]["items"]:
        if key["name"].startswith("v4-revoke-"):
            print(f"revoked leftover key {key['id']}: HTTP {op('DELETE', base + '/api-keys/' + key['id'])[0]}", file=sys.stderr)
    for db in op("GET", base + "/databases")[1]["items"]:
        if db["name"].startswith("v4-revoke-"):
            if db.get("replicaRegions"):
                op("PATCH", base + "/databases/" + db["id"], {"replicaRegions": []})
            print(f"deleted database {db['name']} {db['id']}: HTTP {op('DELETE', base + '/databases/' + db['id'])[0]}", file=sys.stderr)


def main():
    trials, out = int(sys.argv[1]), sys.argv[2]
    proxy = subprocess.Popen(["flyctl", "proxy", "18185:8081", f"{state['G']}.vm.{APP}.internal", "-a", APP],
                             stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    try:
        for _ in range(30):
            if subprocess.run(["curl", "-s", "-m", "2", "-o", "/dev/null", "--http2-prior-knowledge", OP + "/healthz"]).returncode == 0:
                break
            time.sleep(1)
        org, project = state["org"], state["project"]
        if trials == 0:
            cleanup(org, project)
            return
        name = "v4-revoke-" + time.strftime("%Y%m%d%H%M%S", time.gmtime())
        _, db = op("POST", f"/v1/management/organizations/{org}/projects/{project}/databases", {"name": name, "homeRegion": "gru", "replicaRegions": ["sjc", "fra"]})
        print(f"database {name} id {db['id']}", file=sys.stderr)
        try:
            with open(out, "a") as f:
                for n in range(1, trials + 1):
                    row = trial(n, f"/v1/projects/{project}/databases/{name}", project, org)
                    row["at"] = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())
                    f.write(json.dumps(row) + "\n")
                    f.flush()
                    print(json.dumps(row), file=sys.stderr)
        finally:
            cleanup(org, project)
    finally:
        proxy.terminate()


main()
