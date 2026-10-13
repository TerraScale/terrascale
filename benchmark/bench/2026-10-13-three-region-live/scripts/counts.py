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


def call(region, method, path, body=None):
    for attempt in range(8):
        conn = http.client.HTTPSConnection(HOST, timeout=60)
        headers = {"Authorization": f"Bearer {KEY}", "fly-force-instance-id": MACHINES[region], "content-type": "application/json"}
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
    """Operator call on gru's internal listener (flyctl proxy :18181); no credential, the acting user is the tenant owner."""
    conn = http.client.HTTPConnection("127.0.0.1", 18181, timeout=120)
    conn.request(method, path, None if body is None else json.dumps(body),
                 {"X-TerraBase-User-Id": os.environ["OWNER"], "content-type": "application/json"})
    r = conn.getresponse()
    data = r.read()
    conn.close()
    return r.status, (json.loads(data) if data else None)


def pitr(collection, n, extra):
    """n documents, T after the last ack and its convergence, then extra later documents; restore at T into a new database
    and require exactly the first n (count + digest), never a partial set."""
    global DB
    import datetime
    load(collection, n)
    expected = verify(collection, n)[1]["gru"]
    t = datetime.datetime.now(datetime.timezone.utc)
    time.sleep(2)
    rng = random.Random()
    for i in range(extra):
        call(list(MACHINES)[i % 3], "PUT", f"{DB}/collections/{collection}/documents/by-key?id=late-{i:04d}", doc(rng))
    print(f"T = {t.isoformat()}; wrote {extra} later documents")
    while True:
        status, window = call("gru", "GET", f"{DB}/point-in-time")
        latest = window and window.get("latest")
        print(f"window {status} {window}")
        if latest and datetime.datetime.fromisoformat(latest) >= t:
            break
        time.sleep(5)
    name = f"e2e-pitr-{int(time.time())}"
    print("plan", operator("POST", f"{DB}/point-in-time/plan", {"pointInTime": t.isoformat()}))
    status, restored = operator("POST", f"{DB}/point-in-time/restore", {"pointInTime": t.isoformat(), "name": name})
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


if __name__ == "__main__":
    mode, collection = sys.argv[1], sys.argv[2]
    if mode == "load":
        load(collection, int(sys.argv[3]))
    elif mode == "pitr":
        pitr(collection, int(sys.argv[3]), int(sys.argv[4]))
    else:
        ok, _ = verify(collection, int(sys.argv[3]) if len(sys.argv) > 3 else None)
        sys.exit(0 if ok else 1)
