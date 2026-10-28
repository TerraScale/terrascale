#!/usr/bin/env python3
"""Synthetic tenant for the six-direction lag probes (xregion-lag.sh), on a ts-orch host.

  xregion-lag.py provision <work>   creates a user's organization, project, an all-regions database and a probe
                                    collection; writes <work>/tenant.json
  xregion-lag.py teardown <work>    deletes the database, project and organization
  xregion-lag.py summary <dir>      Markdown table of the probe runs in <dir>/<direction>.<mode>.<run>.jsonl

<work> holds `peer` (the operator header), `user` and `peers` as written by xregion-lag.sh.
"""
import json
import os
import secrets
import subprocess
import sys
import time

REGIONS = ("gru", "sjc", "fra")


def http(work, node, method, path, body=None, user=False):
    command = ["curl", "-sS", "--http2-prior-knowledge", "-m", "30", "-w", "\n%{http_code}", "-X", method,
               "-H", "@" + os.path.join(work, "peer")]
    if user:
        command += ["-H", "@" + os.path.join(work, "user")]
    if body is not None:
        command += ["-H", "Content-Type: application/json", "--data-binary", "@-"]
    command.append(f"http://{node}:8081{path}")
    done = subprocess.run(command, input=None if body is None else json.dumps(body).encode(), capture_output=True, check=True)
    text, _, status = done.stdout.decode().rpartition("\n")
    return int(status), (json.loads(text) if text.strip() else None)


def expect(result, *statuses):
    status, body = result
    if status not in statuses:
        raise RuntimeError(f"unexpected status {status}: {json.dumps(body)[:300]}")
    return body


def percentile(values, q):
    ordered = sorted(values)
    return ordered[min(len(ordered) - 1, max(0, round(q * len(ordered)) - 1))]


def summary(directory):
    runs = {}
    for name in sorted(os.listdir(directory)):
        if not name.endswith(".jsonl"):
            continue
        direction, mode, _ = name[:-len(".jsonl")].split(".")
        samples, floor, rate, missing = [], 0.0, 0.0, 0
        with open(os.path.join(directory, name), encoding="utf-8") as stream:
            for line in stream:
                row = json.loads(line)
                if row.get("summary"):
                    floor, rate, missing = row["floor_ms"], row["load_writes_per_s"], row["missing"]
                else:
                    samples.append(row["latency_ms"])
        runs.setdefault((direction, mode), []).append((samples, floor, rate, missing))
    print("| Direction | Mode | Floor | p50 | p99 pooled | p99 per run median (min-max) | max | load writes/s | lost |")
    print("| --- | --- | ---: | ---: | ---: | --- | ---: | ---: | ---: |")
    for (direction, mode), group in sorted(runs.items(), key=lambda item: (item[0][1], item[0][0])):
        pooled = [value for samples, *_ in group for value in samples]
        per_run = sorted(percentile(samples, 0.99) for samples, *_ in group if samples)
        print(f"| {direction.replace('-', ' -> ')} | {mode} | {group[0][1]:.1f} | {percentile(pooled, 0.5):.1f} | "
              f"{percentile(pooled, 0.99):.1f} | {per_run[len(per_run) // 2]:.1f} ({per_run[0]:.1f}-{per_run[-1]:.1f}) | "
              f"{max(pooled):.1f} | {sum(rate for *_, rate, _ in group) / len(group):.1f} | {sum(lost for *_, lost in group)} |")


def main():
    action, work = sys.argv[1:3]
    if action == "summary":
        return summary(work)
    nodes = {}
    with open(os.path.join(work, "peers"), encoding="utf-8") as stream:
        for line in stream:
            parts = line.split()
            if len(parts) == 3:
                nodes[parts[1]] = parts[2]
    sjc = nodes["sjc"]
    path = os.path.join(work, "tenant.json")
    if action == "provision":
        org = expect(http(work, sjc, "POST", "/v1/management/organizations", {"name": "xregion lag"}, user=True), 201)["id"]
        tenant = {"organization": org}
        with open(path, "w", encoding="utf-8") as stream:
            json.dump(tenant, stream)
        project = expect(http(work, sjc, "POST", f"/v1/management/organizations/{org}/projects", {"name": "lag"}, user=True), 201)["id"]
        management = f"/v1/management/organizations/{org}/projects/{project}"
        tenant.update(project=project, management=management)
        deadline = time.monotonic() + 120
        while any(http(work, nodes[r], "GET", management, user=True)[0] != 200 for r in REGIONS):
            if time.monotonic() >= deadline:
                raise RuntimeError("the project did not reach every region")
            time.sleep(1)
        name = "lag-" + secrets.token_hex(4)
        database = expect(http(work, sjc, "POST", management + "/databases", {"name": name, "allRegions": True}, user=True), 201)["id"]
        tenant.update(database=database, name=name, collection="probe")
        with open(path, "w", encoding="utf-8") as stream:
            json.dump(tenant, stream)
        data = f"/v1/projects/{project}/databases/{database}"
        deadline = time.monotonic() + 120
        while http(work, sjc, "POST", data + "/collections", {"name": "probe"})[0] not in (200, 201, 409):
            if time.monotonic() >= deadline:
                raise RuntimeError("the probe collection was not created")
            time.sleep(0.5)
        for region in REGIONS:
            deadline = time.monotonic() + 120
            while http(work, nodes[region], "GET", data + "/collections/probe")[0] != 200:
                if time.monotonic() >= deadline:
                    raise RuntimeError(f"the probe collection did not reach {region}")
                time.sleep(0.5)
        print(json.dumps(tenant))
    elif action == "teardown":
        with open(path, encoding="utf-8") as stream:
            tenant = json.load(stream)
        management = tenant.get("management")
        if management:
            if tenant.get("database"):
                expect(http(work, sjc, "DELETE", management + "/databases/" + tenant["database"], user=True), 204, 404)
            expect(http(work, sjc, "DELETE", management, user=True), 204, 404)
        expect(http(work, sjc, "DELETE", "/v1/management/organizations/" + tenant["organization"], user=True), 204, 404)
        print("teardown done")
    else:
        raise SystemExit(__doc__)


if __name__ == "__main__":
    main()
