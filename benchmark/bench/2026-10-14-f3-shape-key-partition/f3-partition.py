"""V4 F3 on ts-vtest-tb: a partitioned machine stops releasing shape keys within one epoch of its last catalog proof.

Usage: python3 -I f3-partition.py <scripts-dir> <out.jsonl>

As the synthetic owner in state.env, through gru's operator API (flyctl proxy to its internal listener): creates database
f3-<ts> (gru home, sjc + fra replicas), mints K_rev (documents:read) and K_ctl (documents:read+write), creates collection
probe. Then cuts sjc off (vtest.sh partition sjc), revokes K_rev through gru, and
every 5 s asks each machine for a shape key with each credential until sjc has refused for 60 s (at most 15 min). Heals
in every case, waits for sjc to see the revocation and release again, then revokes K_ctl and drops the database.

handle and key_id are fresh well-formed values naming no lineage and no project key (production seals no TBS1 objects
yet, so a client cannot learn the real key id; and shape bootstrap answers 429 shape_busy on 512 MB without #307): a
request that passed authentication, the catalog gate and tenancy answers 409 must_refetch and releases nothing. 503 catalog_stale is the gate; 401 is the revocation. Key secrets stay in memory; only ids are written.
"""
import http.client, json, os, subprocess, sys, time

scripts, out = sys.argv[1], sys.argv[2]
state = dict(kv.split("=", 1) for line in open(os.path.join(scripts, "..", "state.env")) for kv in line.split())
APP, HOST, OP = "ts-vtest-tb", "ts-vtest-tb.fly.dev", "http://127.0.0.1:18186"
M = {"gru": state["G"], "sjc": state["S"], "fra": state["F"]}
org, project, owner = state["org"], state["project"], state["owner"]
base = f"/v1/management/organizations/{org}/projects/{project}"
E = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"
DUMMY = "".join(E[((int(time.time() * 1000) << 80 | int.from_bytes(os.urandom(10), "big")) >> (5 * i)) & 31] for i in range(25, -1, -1))
log = open(out, "a")


def emit(row):
    row["utc"] = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())
    log.write(json.dumps(row) + "\n")
    log.flush()
    print(json.dumps(row), file=sys.stderr)


def op(method, path, body=None):
    args = ["curl", "-sS", "-m", "30", "--http2-prior-knowledge", "-X", method, "-H", "X-TerraBase-User-Id: " + owner,
            "-H", "content-type: application/json", "-w", "\n%{http_code}", OP + path]
    if body is not None:
        args[1:1] = ["-d", json.dumps(body)]
    done = subprocess.run(args, capture_output=True, text=True)
    text, code = done.stdout.rsplit("\n", 1)
    return int(code), (json.loads(text) if text.strip() else None)


def call(method, path, key, region, body=None):
    conn = http.client.HTTPSConnection(HOST, timeout=20)
    try:
        headers = {"Authorization": "Bearer " + key, "fly-force-instance-id": M[region]}
        if body is not None:
            headers["content-type"] = "application/json"
        conn.request(method, path, body=None if body is None else json.dumps(body), headers=headers)
        r = conn.getresponse()
        data = r.read().decode(errors="replace")
        return r.status, dict((k.lower(), v) for k, v in r.getheaders()), data
    except (OSError, http.client.HTTPException) as error:
        return 0, {}, str(error)
    finally:
        conn.close()


def vtest(*args):
    return subprocess.run(["bash", os.path.join(scripts, "vtest.sh"), *args], capture_output=True, text=True)


def mint(name, scopes):
    status, issued = op("POST", "/admin/api-keys", {"name": name, "scopes": scopes, "organizationId": org, "projectId": project,
                                                    "expiresAt": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime(time.time() + 7200))})
    assert status in (200, 201), (status, issued)
    return issued["secret"], issued["id"]


def main():
    proxy = subprocess.Popen(["flyctl", "proxy", "18186:8081", f"{M['gru']}.vm.{APP}.internal", "-a", APP],
                             stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    name = "f3-" + time.strftime("%Y%m%d%H%M%S", time.gmtime())
    keys, db_id, partitioned = {}, None, False
    try:
        for _ in range(30):
            if subprocess.run(["curl", "-s", "-m", "2", "-o", "/dev/null", "--http2-prior-knowledge", OP + "/healthz"]).returncode == 0:
                break
            time.sleep(1)
        status, db = op("POST", base + "/databases", {"name": name, "homeRegion": "gru", "replicaRegions": ["sjc", "fra"]})
        assert status == 201, (status, db)
        db_id = db["id"]
        rev, rev_id = mint(name + "-rev", ["documents:read"])
        ctl, ctl_id = mint(name + "-ctl", ["documents:read", "documents:write"])
        keys = {"rev": rev_id, "ctl": ctl_id}
        emit({"event": "setup", "database": name, "database_id": db_id, "k_rev": rev_id, "k_ctl": ctl_id, "dummy_key_id": DUMMY})
        data = f"/v1/projects/{project}/databases/{name}"
        for _ in range(60):
            created = call("POST", data + "/collections", ctl, "gru", {"name": "probe"})
            if created[0] in (201, 409):
                break
            time.sleep(1)
        emit({"event": "collection", "status": created[0], "body": created[2][:300]})
        # Well-formed handles that name no lineage: a request past the gate answers 409 must_refetch (handle checks run after it).
        handles = {region: "h1-" + os.urandom(8).hex() for region in M}
        emit({"event": "handles", "handles": handles})

        def probe(region, key):
            epoch = int(time.time()) // 600
            status, headers, body = call("GET", f"{data}/collections/probe/shape/keys?handle={handles[region]}&epoch={epoch}&key_id={DUMMY}", key, region)
            code = None
            try:
                code = json.loads(body)["error"]["code"]
            except (ValueError, KeyError, TypeError):
                pass
            return {"status": status, "code": code, "cache_control": headers.get("cache-control"), "retry_after": headers.get("retry-after"),
                    "key_in_body": '"key"' in body}

        def sweep(phase, t0):
            row = {"event": "probe", "phase": phase, "t_s": round(time.monotonic() - t0, 1)}
            for region in M:
                row[f"{region}_rev"] = probe(region, rev)
                row[f"{region}_ctl"] = probe(region, ctl)
            emit(row)
            return row

        start = time.monotonic()
        # The keys, database and collection replicate to sjc and fra first.
        while True:
            baseline = sweep("baseline", start)
            if all(baseline[f"{r}_{k}"]["code"] == "must_refetch" for r in M for k in ("rev", "ctl")):
                break
            if time.monotonic() - start > 120:
                raise RuntimeError("the baseline did not pass the gate on every machine")
            time.sleep(3)
        cut = vtest("partition", "sjc")
        partitioned = True
        t_cut = time.monotonic()
        emit({"event": "partition", "rc": cut.returncode, "stderr": cut.stderr.strip()[-300:]})
        sent = time.monotonic()
        status, _ = op("DELETE", f"{base}/api-keys/{rev_id}")
        t_rev = time.monotonic()
        emit({"event": "revoke", "status": status, "rtt_ms": round((t_rev - sent) * 1000), "after_cut_s": round(t_rev - t_cut, 1)})
        stale_since = None
        while time.monotonic() - t_cut < 900:
            row = sweep("partitioned", t_cut)
            row_rev = row["sjc_rev"]
            if row_rev["code"] == "catalog_stale":
                stale_since = stale_since or time.monotonic()
                if time.monotonic() - stale_since >= 60:
                    break
            else:
                stale_since = None
            time.sleep(5)
    finally:
        if partitioned:
            healed = vtest("heal")
            t_heal = time.monotonic()
            emit({"event": "heal", "rc": healed.returncode, "stderr": healed.stderr.strip()[-300:]})
            if keys:
                while time.monotonic() - t_heal < 300:
                    row = sweep("healed", t_heal)
                    if row["sjc_rev"]["status"] == 401 and all(row[f"{r}_ctl"]["code"] == "must_refetch" for r in M):
                        break
                    time.sleep(5)
        for kind, key_id in keys.items():
            emit({"event": "cleanup-key", "key": kind, "id": key_id, "status": op("DELETE", f"{base}/api-keys/{key_id}")[0]})
        if db_id:
            patched = op("PATCH", f"{base}/databases/{db_id}", {"replicaRegions": []})[0]
            emit({"event": "cleanup-database", "id": db_id, "patch": patched, "delete": op("DELETE", f"{base}/databases/{db_id}")[0]})
        proxy.terminate()


main()
