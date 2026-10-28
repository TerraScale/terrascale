#!/usr/bin/env python3
"""Run on one serving host: how long the first reads of a newly created database take there (dashboard overview reads).

A synthetic organization and project are created through the local region's internal listener. Each of three databases,
placed in this region and its pair (PAIR; an all-regions create is refused while a region is unreachable), gets its first
collections listing, backup listing and management record read, then the same reads again. Everything is deleted
afterwards and its absence checked in every region.
"""
import http.client
import importlib.util
import json
import secrets
import signal
import sys
import time

# perf-live.sh's arguments; the xregion probe is not used here.
work, region, sha, output, _probe, rt6 = sys.argv[1:7]
for stop in (signal.SIGTERM, signal.SIGINT, signal.SIGHUP):
    signal.signal(stop, lambda *_: sys.exit("terminated; cleaning synthetic tenant"))

spec = importlib.util.spec_from_file_location("rt6", rt6)
matrix = importlib.util.module_from_spec(spec)
spec.loader.exec_module(matrix)
# A database needs two regions; gru and fra pair with each other, sjc with gru.
PAIR = {"gru": "fra", "fra": "gru", "sjc": "gru"}

with open(output, "x") as stream:
    live = matrix.Matrix(work, sha, stream)
    organization = project = key_id = None
    databases = []
    try:
        _, body = live.http(region, "POST", "/v1/management/organizations", {"name": "rt6 endpoint matrix"}, user=True, expect={201})
        organization = body["id"]
        _, body = live.http(region, "POST", f"/v1/management/organizations/{organization}/projects", {"name": "rt6"}, user=True, expect={201})
        project = body["id"]
        live.project = project
        management = f"/v1/management/organizations/{organization}/projects/{project}"
        live.record(kind="tenant", organization=organization, project=project)
        client = http.client.HTTPConnection("127.0.0.1", 8080, timeout=60)
        for n in range(3):
            name = "rt6-" + secrets.token_hex(4)
            began = time.perf_counter()
            _, body = live.http(region, "POST", management + "/databases", {"name": name, "homeRegion": region, "replicaRegions": [PAIR[region]]},
                                user=True, expect={201})
            created_ms = (time.perf_counter() - began) * 1000
            database = body["id"]
            databases.append(database)
            if key_id is None:
                _, key = live.http(region, "POST", "/admin/api-keys", {"name": "rt6 coldopen", "scopes": ["documents:read"],
                                   "organizationId": organization, "projectId": project}, expect={201})
                key_id, secret = key["id"], key["secret"]
            reads = []
            for attempt in ("first", "second"):
                began = time.perf_counter()
                client.request("GET", f"/v1/projects/{project}/databases/{database}/collections", headers={"Authorization": "Bearer " + secret})
                response = client.getresponse()
                response.read()
                reads.append(dict(read="collections", attempt=attempt, status=response.status, ms=(time.perf_counter() - began) * 1000))
                # The backup listing is a management route: the internal listener serves it to the acting user.
                began = time.perf_counter()
                status, _ = live.http(region, "GET", f"/v1/projects/{project}/databases/{database}/backups?limit=100", user=True)
                reads.append(dict(read="backups", attempt=attempt, status=status, ms=(time.perf_counter() - began) * 1000))
                began = time.perf_counter()
                status, _ = live.http(region, "GET", management + "/databases/" + database, user=True)
                reads.append(dict(read="record", attempt=attempt, status=status, ms=(time.perf_counter() - began) * 1000))
            live.record(kind="coldopen", region=region, database=database, create_ms=created_ms, reads=reads)
            print(json.dumps(dict(create_ms=round(created_ms), reads=[(r["read"], r["attempt"], r["status"], round(r["ms"], 1)) for r in reads])), flush=True)
    finally:
        for stop in (signal.SIGTERM, signal.SIGINT, signal.SIGHUP):
            signal.signal(stop, signal.SIG_IGN)
        if key_id:
            live.record(kind="teardown", step="revoke key", status=live.http(region, "DELETE", "/admin/api-keys/" + key_id)[0])
        if project:
            management = f"/v1/management/organizations/{organization}/projects/{project}"
            for database in databases:
                live.record(kind="teardown", step="delete database", status=live.http(region, "DELETE", management + "/databases/" + database, user=True)[0])
            live.record(kind="teardown", step="delete project", status=live.http(region, "DELETE", management, user=True)[0])
        if organization:
            path = f"/v1/management/organizations/{organization}"
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
