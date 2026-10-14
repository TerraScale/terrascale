"""V4 F3 live proof on ts-vtest-tb with real served epoch keys and real TBS1 chunks.

Usage: uv run --no-project --with cryptography python f3-live.py <scripts-dir> <out.jsonl>

As the synthetic owner in state.env (gru's operator API through flyctl proxy):
  1. Create database f3l-<ts> (gru home, sjc + fra replicas), collection probe, keys K_rev (documents:read) and K_ctl
     (read+write), and write documents through gru.
  2. Bootstrap the shape on sjc with K_rev. For every mirrored object of the chain, the manifest gives url, epoch and
     key_id. Fetch the ciphertext from Tigris (no origin credential) and check size and sha256. Get K_e from sjc's
     /shape/keys with K_rev (200), decrypt with tbs1.py, and require the plaintext to equal the origin body of that offset.
  3. Cut sjc off (vtest.sh partition sjc: 6PN only; a DNS cut also blocks Tigris and fly ssh, see run 1), revoke K_rev through gru, then ask every machine every 5 s for the current
     epoch's key (real handle and key_id) with both keys. Every key K_rev still gets is kept, with its epoch. Stop once
     sjc has refused K_rev for 60 s (at most 15 min).
  4. Heal (always). Wait until sjc answers 401 to K_rev and 200 to K_ctl.
  5. After the newest epoch whose key K_rev holds has passed, write more documents, read sjc's shape with K_ctl, and take a
     mirrored object sealed in a later epoch. Every key K_rev holds must fail to open it; K_ctl's key must open it.
  6. Revoke K_ctl and drop the database (always).
Key secrets and epoch keys stay in memory; only ids, epochs, statuses and digests are written.
"""
import hashlib, http.client, json, os, subprocess, sys, time, urllib.parse, urllib.request
from base64 import urlsafe_b64decode

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import tbs1  # noqa: E402

scripts, out = sys.argv[1], sys.argv[2]
state = dict(kv.split("=", 1) for line in open(os.path.join(scripts, "..", "state.env")) for kv in line.split())
APP, HOST, OP = "ts-vtest-tb", "ts-vtest-tb.fly.dev", "http://127.0.0.1:18187"
M = {"gru": state["G"], "sjc": state["S"], "fra": state["F"]}
org, project, owner = state["org"], state["project"], state["owner"]
base = f"/v1/management/organizations/{org}/projects/{project}"
log = open(out, "a")
T0 = time.monotonic()


def emit(row):
    row["utc"], row["t"] = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()), round(time.monotonic() - T0, 1)
    log.write(json.dumps(row) + "\n")
    log.flush()
    print(json.dumps(row), file=sys.stderr)


def op(method, path, body=None):
    args = ["curl", "-sS", "-m", "30", "--http2-prior-knowledge", "-X", method, "-H", "X-TerraBase-User-Id: " + owner,
            "-H", "content-type: application/json", "-w", "\n%{http_code}", OP + path]
    if body is not None:
        args[1:1] = ["-d", json.dumps(body)]
    text, code = subprocess.run(args, capture_output=True, text=True).stdout.rsplit("\n", 1)
    return int(code), (json.loads(text) if text.strip() else None)


def call(method, path, key, region, body=None):
    conn = http.client.HTTPSConnection(HOST, timeout=30)
    try:
        headers = {"Authorization": "Bearer " + key, "fly-force-instance-id": M[region]}
        if body is not None:
            headers["content-type"] = "application/json"
        conn.request(method, path, body=None if body is None else json.dumps(body), headers=headers)
        r = conn.getresponse()
        return r.status, {k.lower(): v for k, v in r.getheaders()}, r.read()
    except (OSError, http.client.HTTPException) as error:
        return 0, {}, str(error).encode()
    finally:
        conn.close()


def code_of(body):
    try:
        return json.loads(body)["error"]["code"]
    except (ValueError, KeyError, TypeError):
        return None


def vtest(*args):
    return subprocess.run(["bash", os.path.join(scripts, "vtest.sh"), *args], capture_output=True, text=True)


def mint(name, scopes):
    status, issued = op("POST", "/admin/api-keys", {"name": name, "scopes": scopes, "organizationId": org, "projectId": project,
                                                    "expiresAt": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime(time.time() + 7200))})
    assert status in (200, 201), (status, issued)
    return issued["secret"], issued["id"]


def until(fn, seconds, what):
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        result = fn()
        if result:
            return result
        time.sleep(2)
    raise RuntimeError("timed out: " + what)


def main():
    proxy = subprocess.Popen(["flyctl", "proxy", "18187:8081", f"{M['gru']}.vm.{APP}.internal", "-a", APP],
                             stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    name = "f3l-" + time.strftime("%Y%m%d%H%M%S", time.gmtime())
    keys, db_id, partitioned = {}, None, False
    rev = ctl = None
    try:
        until(lambda: subprocess.run(["curl", "-s", "-m", "2", "-o", "/dev/null", "--http2-prior-knowledge", OP + "/healthz"]).returncode == 0, 30, "proxy")
        status, db = op("POST", base + "/databases", {"name": name, "homeRegion": "gru", "replicaRegions": ["sjc", "fra"]})
        assert status == 201, (status, db)
        db_id = db["id"]
        rev, rev_id = mint(name + "-rev", ["documents:read"])
        ctl, ctl_id = mint(name + "-ctl", ["documents:read", "documents:write"])
        keys = {"rev": rev_id, "ctl": ctl_id}
        emit({"event": "setup", "database": name, "database_id": db_id, "k_rev": rev_id, "k_ctl": ctl_id})
        data = f"/v1/projects/{project}/databases/{name}"
        coll = data + "/collections/probe"
        until(lambda: call("POST", data + "/collections", ctl, "gru", {"name": "probe"})[0] in (201, 409), 60, "collection")

        def write(count, tag):
            for n in range(count):
                until(lambda: call("POST", coll + "/documents", ctl, "gru", {"tag": tag, "n": n, "pad": os.urandom(24).hex()})[0] == 201, 60, "write")

        write(20, "before")
        # Every machine accepts both keys and holds the collection.
        until(lambda: all(call("GET", coll + "/documents/by-key?id=none", k, r)[0] == 404 for r in M for k in (rev, ctl)), 120, "replication")

        def chain(region, key, handle=None):
            """Reads the shape from bootstrap (or handle's offset -1) to up_to_date; returns handle and [(offset, body)]."""
            status, headers, body = call("GET", coll + "/shape?offset=-1", key, region)
            if status != 307:
                return None, [("-1", status, code_of(body))]
            handle, target, pages = headers["tb-shape-handle"], headers["location"], []
            for _ in range(64):
                offset = urllib.parse.parse_qs(urllib.parse.urlsplit(target).query)["offset"][0]
                status, headers, body = call("GET", target, key, region)
                if status != 200:
                    pages.append((offset, status, code_of(body)))
                    break
                page = json.loads(body)
                pages.append((offset, body))
                if page.get("up_to_date"):
                    break
                target = f"{coll}/shape?handle={handle}&offset={page['next_offset']}"
            return handle, pages

        def mirrored(region, key, handle, pages, newer_than=-1):
            """Mirrored objects of the chain: (offset, origin body, manifest entry), with epoch above newer_than."""
            found = []
            for offset, *rest in pages:
                if len(rest) != 1:
                    continue
                status, _, body = call("GET", f"{coll}/shape/manifest?handle={handle}&offset={offset}", key, region)
                if status == 200:
                    for entry in json.loads(body)["objects"]:
                        if entry["offset"] == offset and int(entry["epoch"]) > newer_than:
                            found.append((offset, rest[0], entry))
            return found

        def fetch(entry):
            with urllib.request.urlopen(entry["url"], timeout=30) as response:
                cipher = response.read()
            assert len(cipher) == entry["size"] and hashlib.sha256(cipher).hexdigest() == entry["sha256"], "size or sha256"
            return cipher

        def key_for(region, key, handle, epoch, key_id):
            status, headers, body = call("GET", f"{coll}/shape/keys?handle={handle}&epoch={epoch}&key_id={key_id}", key, region)
            k_e = urlsafe_b64decode(json.loads(body)["key"] + "==") if status == 200 else None
            return status, code_of(body) if status != 200 else None, headers.get("cache-control"), headers.get("retry-after"), k_e

        # 2. Real served objects, keys and decryption on sjc before the partition.
        def ready():
            handle, pages = chain("sjc", rev)
            objects = mirrored("sjc", rev, handle, pages) if handle else []
            return (handle, pages, objects) if objects else None
        handle, pages, objects = until(ready, 180, "mirrored objects on sjc")
        held = {}  # epoch -> K_e released to K_rev (memory only)
        for offset, origin, entry in objects:
            cipher = fetch(entry)
            status, code, cache, _, k_e = key_for("sjc", rev, handle, entry["epoch"], entry["key_id"])
            plain = tbs1.open_object(cipher, handle, offset, int(entry["epoch"]), entry["key_id"], k_e) if k_e else None
            held[int(entry["epoch"])] = k_e
            emit({"event": "pre-object", "handle": handle, "offset": offset, "epoch": entry["epoch"], "key_id": entry["key_id"],
                  "size": entry["size"], "sha256": entry["sha256"], "key_status": status, "key_cache_control": cache,
                  "decrypted": plain is not None, "plaintext_equals_origin_body": plain == origin})
        key_id = objects[0][2]["key_id"]
        handles = {"sjc": handle}
        for region in ("gru", "fra"):
            handles[region] = until(lambda: (lambda r: r[1].get("tb-shape-handle") if r[0] == 307 else None)(
                call("GET", coll + "/shape?offset=-1", ctl, region)), 120, "handle " + region)
        emit({"event": "handles", "handles": handles})
        assert all(e["key_id"] == key_id for _, _, e in objects)

        def sweep(phase, t0):
            row = {"event": "probe", "phase": phase, "t_s": round(time.monotonic() - t0, 1), "epoch": int(time.time()) // 600}
            for region in M:
                for name_, key in (("rev", rev), ("ctl", ctl)):
                    status, code, cache, retry, k_e = key_for(region, key, handles[region], row["epoch"], key_id)
                    row[f"{region}_{name_}"] = {"status": status, "code": code, "cache_control": cache, "retry_after": retry}
                    if name_ == "rev" and k_e is not None:
                        held[row["epoch"]] = k_e
            emit(row)
            return row

        start = time.monotonic()
        baseline = sweep("baseline", start)
        assert all(baseline[f"{r}_{k}"]["status"] == 200 for r in M for k in ("rev", "ctl")), "baseline"

        # 3. Partition sjc, revoke K_rev through gru.
        cut = vtest("partition", "sjc")
        partitioned = True
        t_cut = time.monotonic()
        emit({"event": "partition", "rc": cut.returncode})
        sent = time.monotonic()
        status, _ = op("DELETE", f"{base}/api-keys/{rev_id}")
        t_rev = time.monotonic()
        e_rev = int(time.time()) // 600
        emit({"event": "revoke", "status": status, "rtt_ms": round((t_rev - sent) * 1000), "after_cut_s": round(t_rev - t_cut, 1), "epoch": e_rev})
        released_after = []
        stale_since = None
        while time.monotonic() - t_cut < 900:
            row = sweep("partitioned", t_cut)
            if row["sjc_rev"]["status"] == 200:
                released_after.append((round(time.monotonic() - t_rev, 1), row["epoch"]))
            if row["sjc_rev"]["code"] == "catalog_stale":
                stale_since = stale_since or time.monotonic()
                if time.monotonic() - stale_since >= 60:
                    break
            else:
                stale_since = None
            time.sleep(5)
        emit({"event": "released-to-revoked", "after_revoke_s_and_epoch": released_after, "revoke_epoch": e_rev,
              "max_epoch_held": max(held), "within_next_epoch": max(held) <= e_rev + 1})
    finally:
        if partitioned:
            healed = vtest("heal")
            t_heal = time.monotonic()
            emit({"event": "heal", "rc": healed.returncode})
            try:
                until(lambda: (lambda r: r["sjc_rev"]["status"] == 401 and all(r[f"{x}_ctl"]["status"] == 200 for x in M))(sweep("healed", t_heal)), 300, "heal")
                # 5. A later-epoch object: nothing K_rev holds opens it; K_ctl's key does.
                newest = max(held)
                while int(time.time()) // 600 <= newest:
                    time.sleep(10)
                write(10, "after")
                def later():
                    h, pages = chain("sjc", ctl)
                    found = mirrored("sjc", ctl, h, pages, newer_than=newest) if h else []
                    return (h, found) if found else None
                h, found = until(later, 300, "later-epoch object")
                for offset, origin, entry in found:
                    cipher = fetch(entry)
                    epoch = int(entry["epoch"])
                    opened_by_revoked = [e for e, k_e in held.items() if tbs1.open_object(cipher, h, offset, epoch, entry["key_id"], k_e) is not None]
                    rev_status = [key_for(r, rev, h, epoch, entry["key_id"])[0] for r in M]
                    rev_manifest = [call("GET", f"{coll}/shape/manifest?handle={h}&offset={offset}", rev, r)[0] for r in M]
                    status, _, _, _, k_ctl = key_for("sjc", ctl, h, epoch, entry["key_id"])
                    plain = tbs1.open_object(cipher, h, offset, epoch, entry["key_id"], k_ctl) if k_ctl else None
                    emit({"event": "later-object", "offset": offset, "epoch": epoch, "held_epochs": sorted(held),
                          "opened_by_revoked_keys": opened_by_revoked, "revoked_key_status_by_region": rev_status,
                          "revoked_manifest_status_by_region": rev_manifest, "ctl_key_status": status,
                          "ctl_decrypted_equals_origin": plain == origin})
            except Exception as error:  # the cleanup below still runs
                emit({"event": "post-heal-error", "error": repr(error)[:300]})
        for kind, kid in keys.items():
            emit({"event": "cleanup-key", "key": kind, "id": kid, "status": op("DELETE", f"{base}/api-keys/{kid}")[0]})
        if db_id:
            patched = op("PATCH", f"{base}/databases/{db_id}", {"replicaRegions": []})[0]
            emit({"event": "cleanup-database", "id": db_id, "patch": patched, "delete": op("DELETE", f"{base}/databases/{db_id}")[0]})
        proxy.terminate()


main()
