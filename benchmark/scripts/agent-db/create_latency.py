import argparse
import base64
import json
import statistics
import sys
import time
import urllib.error
import urllib.request

AUTH = "https://auth.terrascale.tech/terrascale/oauth/token"
API = "https://api.terrascale.tech"


def call(method, url, token=None, body=None, headers=None):
    data = None if body is None else json.dumps(body).encode()
    request = urllib.request.Request(url, data=data, method=method, headers=headers or {})
    if token:
        request.add_header("Authorization", "Bearer " + token)
    if data is not None:
        request.add_header("Content-Type", "application/json")
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            text = response.read()
            return response.status, json.loads(text) if text else None
    except urllib.error.HTTPError as error:
        text = error.read()
        return error.code, json.loads(text) if text else None


def token(key, project):
    basic = base64.b64encode(f"{key[:26]}:{key}".encode()).decode()
    form = f"grant_type=client_credentials&project={project}".encode()
    request = urllib.request.Request(AUTH, data=form, method="POST", headers={
        "Authorization": "Basic " + basic, "Content-Type": "application/x-www-form-urlencoded"})
    with urllib.request.urlopen(request, timeout=30) as response:
        return json.loads(response.read())["access_token"]


def run(key, project, count, prefix, ttl):
    times = []
    access = token(key, project)
    issued = time.monotonic()
    for index in range(count):
        if time.monotonic() - issued > 40:
            access, issued = token(key, project), time.monotonic()
        name = f"{prefix}-{index}"
        started = time.perf_counter()
        status, body = call("POST", f"{API}/v1/projects/{project}/databases", access, dict(name=name, **({"ttlSeconds": 600} if ttl else {})))
        elapsed = (time.perf_counter() - started) * 1000
        if status != 201:
            raise SystemExit(f"create {name}: HTTP {status} {body}")
        times.append(elapsed)
        status, _ = call("DELETE", f"{API}/v1/projects/{project}/databases/{body['id']}", access)
        if status != 204:
            raise SystemExit(f"delete {name}: HTTP {status}")
    return times


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("key_file")
    parser.add_argument("project")
    parser.add_argument("--count", type=int, default=30)
    parser.add_argument("--repeats", type=int, default=3)
    parser.add_argument("--prefix", default="bench")
    parser.add_argument("--no-ttl", action="store_true")
    args = parser.parse_args()
    key = open(args.key_file).read().strip()
    rows = []
    for repeat in range(args.repeats + 1):
        times = run(key, args.project, args.count, f"{args.prefix}-r{repeat}", not args.no_ttl)
        if repeat == 0:
            continue
        ordered = sorted(times)
        rows.append((repeat, len(times), statistics.median(ordered), ordered[int(len(ordered) * 0.95) - 1], ordered[0], ordered[-1]))
    print("| repeat | creates | p50 ms | p95 ms | min ms | max ms |")
    print("|---:|---:|---:|---:|---:|---:|")
    for row in rows:
        print(f"| {row[0]} | {row[1]} | {row[2]:.1f} | {row[3]:.1f} | {row[4]:.1f} | {row[5]:.1f} |")


if __name__ == "__main__":
    sys.exit(main())
