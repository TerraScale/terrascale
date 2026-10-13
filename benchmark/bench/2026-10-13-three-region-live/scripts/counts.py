"""Writes synthetic documents across the three pinned machines of ts-terrabase-e2e and prints each region's count and digest.

  counts.py load <collection> <n>   create the collection, write n documents round-robin, wait until all regions agree
  counts.py verify <collection> [n] print each region's count and digest (exit 1 if they differ, or differ from n)
Env: TERRABASE_E2E_FLY_API_KEY (inherited, never printed), TERRABASE_E2E_FLY_PROJECT, G/S/F machine ids.
Every request is pinned with fly-force-instance-id and refused unless the answer names that machine and region.
"""
import concurrent.futures, hashlib, http.client, json, os, random, string, sys, time, urllib.parse

HOST = "ts-terrabase-e2e.fly.dev"
KEY = os.environ["TERRABASE_E2E_FLY_API_KEY"]
DB = f"/v1/projects/{os.environ['TERRABASE_E2E_FLY_PROJECT']}/databases/e2e"
MACHINES = {"gru": os.environ["G"], "sjc": os.environ["S"], "fra": os.environ["F"]}
IPS = {region: ip for region, ip in (pair.split("=") for pair in os.environ.get("IPS", "").split()) } if os.environ.get("IPS") else {}


def call(region, method, path, body=None, content_type="application/json"):
    for attempt in range(8):
        conn = http.client.HTTPSConnection(HOST, timeout=60)
        headers = {"Authorization": f"Bearer {KEY}", "fly-force-instance-id": MACHINES[region], "content-type": content_type}
        conn.request(method, path, None if body is None else json.dumps(body), headers)
        r = conn.getresponse()
        data = r.read()
        served = (r.getheader("X-TerraBase-Region"), r.getheader("X-TerraBase-Machine"))
        conn.close()
        if served != (region, MACHINES[region]):
            raise SystemExit(f"{method} {path}: pinned to {region}/{MACHINES[region]}, answered by {served} ({r.status})")
        if r.status in (429, 503) and attempt < 7:
            time.sleep(0.5 * (attempt + 1))
            continue
        if r.status >= 400 and not (method == "GET" and r.status == 404):
            raise SystemExit(f"{method} {path} on {region}: {r.status} {data[:300]!r}")
        return r.status, (json.loads(data) if data else None)


def canonical(doc):
    return json.dumps(doc, sort_keys=True, separators=(",", ":"), ensure_ascii=False)


def digest(pairs):
    return hashlib.sha256("".join(f"{i}\t{c}\n" for i, c in sorted(pairs)).encode()).hexdigest()


def listing(region, collection):
    pairs, cursor = [], None
    while True:
        q = "limit=1000" + (f"&cursor={urllib.parse.quote(cursor)}" if cursor else "")
        _, page = call(region, "GET", f"{DB}/collections/{collection}/documents?{q}")
        pairs += [(item["id"], canonical(item["document"])) for item in page["items"]]
        cursor = page.get("next_cursor")
        if not cursor:
            return len(pairs), digest(pairs)


def doc(rng):
    return {"name": "".join(rng.choices(string.ascii_lowercase, k=12)), "n": rng.randint(0, 10**9),
            "tags": [rng.choice(["a", "b", "c", "d"]) for _ in range(3)], "nested": {"x": rng.random(), "ok": rng.random() < 0.5}}


def verify(collection, n=None):
    views = {region: listing(region, collection) for region in MACHINES}
    for region, (count, d) in views.items():
        print(f"{region}/{MACHINES[region]}: count={count} digest={d}")
    same = len(set(views.values())) == 1 and (n is None or all(c == n for c, _ in views.values()))
    print("AGREE" if same else "DISAGREE")
    return same, views


def load(collection, n):
    call("gru", "POST", f"{DB}/collections", {"name": collection})
    deadline = time.time() + 60
    while any(call(r, "GET", f"{DB}/collections/{collection}")[0] == 404 for r in MACHINES):
        if time.time() > deadline:
            raise SystemExit("collection did not reach every region in 60 s")
        time.sleep(0.5)
    rng = random.Random()
    docs = [(f"{i:06d}-{rng.getrandbits(48):012x}", doc(rng)) for i in range(n)]
    regions = list(MACHINES)
    start = time.time()
    with concurrent.futures.ThreadPoolExecutor(24) as pool:
        list(pool.map(lambda ix: call(regions[ix % 3], "PUT", f"{DB}/collections/{collection}/documents/by-key?id={docs[ix][0]}", docs[ix][1]), range(n)))
    acked = time.time()
    expected = digest((i, canonical(d)) for i, d in docs)
    print(f"wrote {n} documents round-robin gru/sjc/fra in {acked - start:.1f} s; expected digest {expected}")
    while True:
        same, views = verify(collection, n)
        if same and next(iter(views.values()))[1] == expected:
            print(f"converged {time.time() - acked:.1f} s after the last ack (EXPLORATORY; includes listing time)")
            return
        if time.time() - acked > 300:
            raise SystemExit("did not converge within 300 s")
        time.sleep(2)




def operator(method, path, body=None):
    """Operator call on gru's internal listener (flyctl proxy :18181, h2c); no credential, the acting user is the tenant owner."""
    import subprocess
    args = ["curl", "-sS", "-m", "120", "--http2-prior-knowledge", "-X", method, "-H", f"X-TerraBase-User-Id: {os.environ['OWNER']}",
            "-H", "content-type: application/json", "-w", "\n%{http_code}", f"http://127.0.0.1:18181{path}"]
    if body is not None:
        args += ["-d", json.dumps(body)]
    out = subprocess.run(args, capture_output=True, text=True, check=True).stdout
    data, _, status = out.rpartition("\n")
    return int(status), (json.loads(data) if data.strip() else None)


def management_db(name):
    """The internal listener's operator path for a database: /v1/management/organizations/{org}/projects/{project}/databases/{id}."""
    base = f"/v1/management/organizations/{os.environ['ORG']}/projects/{os.environ['TERRABASE_E2E_FLY_PROJECT']}/databases"
    status, page = operator("GET", base)
    match = [d["id"] for d in page["items"] if d["name"] == name]
    if status != 200 or len(match) != 1:
        raise SystemExit(f"database {name}: {status} {page}")
    return f"{base}/{match[0]}"


def pitr(collection, n, extra):
    """n documents, T after the last ack and its convergence, then extra later documents; restore at T into a new database
    and require exactly the first n (count + digest), never a partial set."""
    global DB
    import datetime
    MDB = management_db("e2e")
    load(collection, n)
    expected = verify(collection, n)[1]["gru"]
    t = datetime.datetime.now(datetime.timezone.utc)
    time.sleep(2)
    rng = random.Random()
    for i in range(extra):
        call(list(MACHINES)[i % 3], "PUT", f"{DB}/collections/{collection}/documents/by-key?id=late-{i:04d}", doc(rng))
    print(f"T = {t.isoformat()}; wrote {extra} later documents")
    while True:
        status, window = operator("GET", f"{MDB}/point-in-time")
        latest = window and window.get("latest")
        print(f"window {status} {window}")
        if latest and datetime.datetime.fromisoformat(latest) >= t:
            break
        if status != 200 or time.time() - t.timestamp() > 600:
            raise SystemExit(f"restore window never reached T: {status} {window}")
        time.sleep(5)
    name = f"e2e-pitr-{int(time.time())}"
    print("plan", operator("POST", f"{MDB}/point-in-time/plan", {"pointInTime": t.isoformat()}))
    status, restored = operator("POST", f"{MDB}/point-in-time/restore", {"pointInTime": t.isoformat(), "name": name})
    print("restore", status, json.dumps(restored)[:400])
    if status != 201:
        raise SystemExit(f"restore refused or failed: {status}")
    source, DB = DB, DB.rsplit("/", 1)[0] + "/" + name
    deadline = time.time() + 300
    while True:
        views = {region: listing(region, collection) for region in MACHINES}
        for region, view in views.items():
            print(f"restored {name} in {region}: count={view[0]} digest={view[1]}")
        if all(view == expected for view in views.values()):
            print(f"PITR OK: restored = source at T ({expected[0]} documents, digest {expected[1]}); later {extra} excluded")
            return
        if time.time() > deadline:
            raise SystemExit(f"PITR MISMATCH: expected {expected}")
        time.sleep(5)


def ssh(machine, command):
    import subprocess
    out = subprocess.run(["flyctl", "ssh", "console", "-a", "ts-terrabase-e2e", "--machine", machine, "-C", command],
                         capture_output=True, text=True, timeout=120)
    if out.returncode != 0:
        raise SystemExit(f"ssh {machine} {command!r}: {out.stderr[-300:]}")
    return out.stdout


def cut(isolated, others, heal=False):
    """Drops (or restores) every 6PN packet between the isolated machine and each other machine, in both directions, on
    both ends. Client traffic arrives from Fly Proxy, not from these addresses, so every region keeps serving."""
    op = "-D" if heal else "-A"
    for other in others:
        for machine, peer in ((isolated, other), (other, isolated)):
            ip = IPS[peer]
            ssh(MACHINES[machine], f"sh -c 'ip6tables {op} INPUT -s {ip} -j DROP && ip6tables {op} OUTPUT -d {ip} -j DROP'")


def partition(collection, n):
    """fra is cut off from gru and sjc and every region writes n documents of its own;
    the cut is proven (no write crosses it for 30 s), healed, and all regions must converge to the union."""
    isolated, others = "fra", ["gru", "sjc"]
    call("gru", "POST", f"{DB}/collections", {"name": collection})
    deadline = time.time() + 60
    while any(call(r, "GET", f"{DB}/collections/{collection}")[0] == 404 for r in MACHINES):
        if time.time() > deadline:
            raise SystemExit("collection did not reach every region in 60 s")
        time.sleep(0.5)
    shared = f"{DB}/collections/{collection}/documents/by-key?id=shared"
    call("gru", "PUT", shared, {"base": True})
    while any(call(r, "GET", shared)[0] == 404 for r in MACHINES):
        time.sleep(0.5)
    cut(isolated, others)
    t0 = time.time()
    print(f"{time.strftime('%H:%M:%S', time.gmtime())} cut {isolated} from {others} (ip6tables DROP both ways, both ends)")
    rng = random.Random()
    docs = {r: [(f"{r}-{i:05d}", doc(rng)) for i in range(n)] for r in MACHINES}
    with concurrent.futures.ThreadPoolExecutor(24) as pool:
        list(pool.map(lambda job: call(job[0], "PUT", f"{DB}/collections/{collection}/documents/by-key?id={job[1][0]}", job[1][1]),
                      [(r, d) for r in MACHINES for d in docs[r]]))
    # Same-document edits on both sides of the cut: each region sets its own field and the same contested field.
    for r in MACHINES:
        call(r, "PATCH", shared, {r: f"{r}-edit", "winner": r}, "application/merge-patch+json")
    print(f"wrote {n} documents in each region and edited the shared document in all three during the cut ({time.time() - t0:.1f} s)")
    time.sleep(30)
    views = {r: listing(r, collection) for r in MACHINES}
    for r, (count, d) in views.items():
        print(f"during cut {r}: count={count} digest={d}")
    edits = {r: call(r, "GET", shared)[1] for r in MACHINES}
    print("shared version tokens during the cut: " + ", ".join(f"{r}={v.get('version')}" for r, v in edits.items()))
    print("shared document during the cut: " + "; ".join(f"{r}={canonical(v)}" for r, v in edits.items()))
    side = views[isolated][0]
    rest = {views[r][0] for r in others}
    if side != n + 1 or rest != {2 * n + 1}:
        raise SystemExit(f"PARTITION NOT REAL or not isolated: {isolated} has {side}, others {rest} (expected {n + 1} and {2 * n + 1})")
    print(f"partition proven: {isolated} holds only its own {n} (+shared), {others} hold their {2 * n} (+shared) after 30 s")
    cut(isolated, others, heal=True)
    healed = time.time()
    print(f"{time.strftime('%H:%M:%S', time.gmtime())} healed after {healed - t0:.0f} s")
    while True:
        views = {r: listing(r, collection) for r in MACHINES}
        # The document only: the read also returns a per-replica version token.
        sharedviews = {r: (lambda v: v.get("document", v))(call(r, "GET", shared)[1]) for r in MACHINES}
        states = {canonical(v) for v in sharedviews.values()}
        if len({v for v in views.values()}) == 1 and next(iter(views.values()))[0] == 3 * n + 1 and len(states) == 1:
            merged = next(iter(sharedviews.values()))
            own = all(merged.get(r) == f"{r}-edit" for r in MACHINES)
            for r, (count, d) in views.items():
                print(f"after heal {r}: count={count} digest={d}")
            print(f"shared document in every region: {canonical(merged)}")
            print(f"converged {time.time() - healed:.1f} s after the heal: {3 * n + 1} documents, equal digest in every region; "
                  f"every region's own field survived: {own}; contested field 'winner' = {merged.get('winner')!r} everywhere")
            union = digest([(i, canonical(d)) for r in MACHINES for i, d in docs[r]] + [("shared", canonical(merged))])
            print(f"union of every write = listing in every region: {union == next(iter(views.values()))[1]}")
            if union != next(iter(views.values()))[1] or not own or merged.get("winner") not in MACHINES or not merged.get("base"):
                raise SystemExit("CONFLICT CONVERGENCE WRONG: a field was lost or the contested field holds no written value")
            return
        if time.time() - healed > 600:
            raise SystemExit(f"did not converge within 600 s of the heal: {views}")
        time.sleep(5)


def shared_check(collection):
    """Prints each region's read of the partition step's shared document (document and version token)."""
    for r in MACHINES:
        print(r, canonical(call(r, "GET", f"{DB}/collections/{collection}/documents/by-key?id=shared")[1]))


if __name__ == "__main__":
    mode, collection = sys.argv[1], sys.argv[2]
    if mode == "load":
        load(collection, int(sys.argv[3]))
    elif mode == "list":   # list <collection> <database> <region>: one region's count and digest of another database
        DB = DB.rsplit("/", 1)[0] + "/" + sys.argv[3]
        count, d = listing(sys.argv[4], collection)
        print(f"{sys.argv[3]}/{collection} in {sys.argv[4]}: count={count} digest={d}")
    elif mode == "shared":
        shared_check(collection)
    elif mode == "partition":
        partition(collection, int(sys.argv[3]))
    elif mode == "pitr":
        pitr(collection, int(sys.argv[3]), int(sys.argv[4]))
    else:
        ok, _ = verify(collection, int(sys.argv[3]) if len(sys.argv) > 3 else None)
        sys.exit(0 if ok else 1)
