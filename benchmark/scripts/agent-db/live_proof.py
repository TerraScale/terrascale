import argparse
import base64
import json
import sys
import time
from datetime import UTC, datetime

from create_latency import API, call, token

MINT = "https://auth.terrascale.tech/v1/terrascale/api-keys/self/scoped-keys"


def sql(access, project, database, *statements):
    return call("POST", f"{API}/v1/projects/{project}/databases/{database}/sql/execute", access, {"statements": [{"sql": s} for s in statements]})


def mint(key, body):
    import urllib.error
    import urllib.request
    basic = base64.b64encode(f"{key.removeprefix('sk_')[:26]}:{key}".encode()).decode()
    request = urllib.request.Request(MINT, data=json.dumps(body).encode(), method="POST",
                                     headers={"Authorization": "Basic " + basic, "Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            return response.status, json.loads(response.read())
    except urllib.error.HTTPError as error:
        return error.code, json.loads(error.read() or b"{}")


def check(label, condition, detail=""):
    print(f"{'PASS' if condition else 'FAIL'} {label} {detail}".rstrip())
    if not condition:
        raise SystemExit(1)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("key_file")
    parser.add_argument("project")
    args = parser.parse_args()
    with open(args.key_file) as key_file:
        key = key_file.read().strip()
    project = args.project
    access = token(key, project)
    stamp = datetime.now(UTC).strftime("%H%M%S")

    status, source = call("POST", f"{API}/v1/projects/{project}/databases", access, {"name": f"src-{stamp}"})
    check("create source", status == 201, str(status))
    status, _ = sql(access, project, source["id"], "CREATE TABLE runs (id INTEGER PRIMARY KEY, name TEXT)",
                    "INSERT INTO runs (name) VALUES ('alpha'), ('beta'), ('gamma')")
    check("seed source", status == 200, str(status))

    started = time.perf_counter()
    status, fork = call("POST", f"{API}/v1/projects/{project}/databases/{source['id']}/branches", access, {"name": f"fork-{stamp}", "ttlSeconds": 3600})
    fork_ms = (time.perf_counter() - started) * 1000
    check("fork now", status == 201 and fork.get("expiresAt") and fork.get("branchedFrom") == source["id"], f"{status} {fork_ms:.0f} ms expiresAt={fork.get('expiresAt')}")
    status, rows = sql(access, project, fork["id"], "SELECT name FROM runs ORDER BY id")
    names = [row[0] for row in rows["results"][0]["rows"]] if status == 200 else []
    check("fork equals source", names == ["alpha", "beta", "gamma"], str(names))

    for _ in range(120):
        status, window = call("GET", f"{API}/v1/projects/{project}/databases/{source['id']}/point-in-time", access)
        if status == 200:
            break
        time.sleep(5)
    check("restore window", status == 200, json.dumps(window))
    time.sleep(1.5)
    at = datetime.now(UTC).isoformat().replace("+00:00", "Z")
    time.sleep(1.5)
    sql(access, project, source["id"], "INSERT INTO runs (name) VALUES ('later')")
    pitr = None
    for _ in range(120):
        access = token(key, project)
        started = time.perf_counter()
        status, pitr = call("POST", f"{API}/v1/projects/{project}/databases/{source['id']}/point-in-time/restore", access,
                            {"name": f"pitr-{stamp}", "pointInTime": at, "ttlSeconds": 600})
        pitr_ms = (time.perf_counter() - started) * 1000
        if status not in (404, 409) or pitr.get("error", {}).get("code") not in ("backup_history_incomplete", "not_found"):
            break
        time.sleep(5)
    check("fork at a point in time", status == 201 and pitr.get("expiresAt"), f"{status} {pitr_ms:.0f} ms {pitr.get('error', {}).get('code', '') if status != 201 else ''}")
    status, rows = sql(access, project, pitr["id"], "SELECT name FROM runs ORDER BY id")
    names = [row[0] for row in rows["results"][0]["rows"]] if status == 200 else []
    check("point-in-time fork holds only earlier rows", names == ["alpha", "beta", "gamma"], str(names))
    status, _ = call("DELETE", f"{API}/v1/projects/{project}/databases/{pitr['id']}", access)
    check("delete point-in-time fork", status == 204)

    status, ttl = call("POST", f"{API}/v1/projects/{project}/databases", access, {"name": f"ttl-{stamp}", "ttlSeconds": 60})
    check("create with ttl", status == 201 and ttl.get("expiresAt"), f"{status} expiresAt={ttl.get('expiresAt')}")
    expires = datetime.fromisoformat(ttl["expiresAt"])

    access = token(key, project)
    status, minted = mint(key, {"project": project, "database": fork["id"], "access": "read", "ttl_seconds": 600})
    check("mint scoped read key", status == 201 and minted.get("database") == fork["id"], f"{status} {minted.get('expires_at')}")
    scoped = token(minted["key"], project)
    status, _ = sql(scoped, project, fork["id"], "SELECT count(*) FROM runs")
    check("scoped key reads its database", status == 200, str(status))
    status, body = sql(scoped, project, source["id"], "SELECT count(*) FROM runs")
    check("scoped key refused on another database", status == 404, f"{status} {body.get('error', {}).get('code') if body else ''}")
    status, body = sql(scoped, project, fork["id"], "INSERT INTO runs (name) VALUES ('delta')")
    check("read-only scoped key refused a write", status != 200, f"{status} {body.get('error', {}).get('code') if body else ''}")
    status, body = mint(minted["key"], {"project": project, "database": fork["id"], "access": "read"})
    check("scoped key mints nothing", status == 403, str(status))

    status, cleared = call("DELETE", f"{API}/v1/projects/{project}/databases/{fork['id']}/ttl", access)
    check("clear ttl", status == 200 and cleared.get("expiresAt") is None, str(status))

    while True:
        if (datetime.now(UTC) - expires).total_seconds() > 120:
            check("ttl database deleted after expiresAt", False, "still present 120 s after expiresAt")
        access = token(key, project)
        status, _ = call("GET", f"{API}/v1/projects/{project}/databases/{ttl['id']}", access)
        if status == 404:
            late = (datetime.now(UTC) - expires).total_seconds()
            check("ttl database deleted after expiresAt", late >= 0, f"{late:.1f} s after expiresAt")
            break
        time.sleep(1)

    for database in (fork["id"], source["id"]):
        status, _ = call("DELETE", f"{API}/v1/projects/{project}/databases/{database}", access)
        check("cleanup delete", status == 204, database)


if __name__ == "__main__":
    sys.exit(main())
