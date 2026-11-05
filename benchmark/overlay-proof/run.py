"""Run on ts-sjc-1 with {apiKey, project} on stdin; emit only synthetic proof data.

Uses the public API through the local edge, preserving TLS/SNI. Every database
is disposable. The caller owns the ts-throwaway-account lifecycle.
"""

import base64
import concurrent.futures
import http.client
import json
import secrets
import socket
import ssl
import statistics
import sys
import threading
import time
import urllib.parse
import urllib.request


CREDS = json.load(sys.stdin)
PROJECT = CREDS["project"]
TOKEN = ""
TOKEN_AT = 0
LOCK = threading.Lock()
CONNECTIONS = threading.local()
READ_RETRIES = []


class Edge(http.client.HTTPSConnection):
    def connect(self):
        raw = socket.create_connection(("127.0.0.1", 443), self.timeout)
        raw.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
        self.sock = self._context.wrap_socket(raw, server_hostname=self.host)


def token():
    global TOKEN, TOKEN_AT
    with LOCK:
        if time.monotonic() - TOKEN_AT < 40 and TOKEN:
            return TOKEN
        basic = base64.b64encode((CREDS["apiKey"][:26] + ":" + CREDS["apiKey"]).encode()).decode()
        form = urllib.parse.urlencode({"grant_type": "client_credentials", "project": PROJECT}).encode()
        request = urllib.request.Request(
            "https://auth.terrascale.tech/terrascale/oauth/token", form,
            {"Authorization": "Basic " + basic, "Content-Type": "application/x-www-form-urlencoded"})
        with urllib.request.urlopen(request, timeout=20) as response:
            TOKEN = json.load(response)["access_token"]
        TOKEN_AT = time.monotonic()
        return TOKEN


def call(method, path, body=None, headers=None, redirects=0, sent=None):
    bearer = token()
    if not hasattr(CONNECTIONS, "edge"):
        CONNECTIONS.edge = Edge("api.terrascale.tech", timeout=25, context=ssl.create_default_context())
    connection = CONNECTIONS.edge
    connection.request(method, path, None if body is None else json.dumps(body),
                       {"Authorization": "Bearer " + bearer, "Content-Type": "application/json", **(headers or {})})
    if sent is not None:
        sent.set()
    response = connection.getresponse()
    data = response.read()
    received = {key.lower(): value for key, value in response.getheaders()}
    if response.status == 307 and method == "GET" and redirects < 8:
        location = response.getheader("Location")
        target = urllib.parse.urlsplit(location)
        if target.netloc and target.netloc != "api.terrascale.tech":
            raise RuntimeError("shape redirect left the selected edge")
        return call(method, target.path + ("?" + target.query if target.query else ""),
                    headers=headers, redirects=redirects + 1)
    return response.status, json.loads(data) if data else None, received


def require(status, wanted, body, action):
    if status != wanted:
        code = body.get("error", {}).get("code") if isinstance(body, dict) else None
        raise RuntimeError(f"{action}: HTTP {status}, code={code}")


def sql(database, text, headers=None, sent=None):
    return call("POST", f"/v1/projects/{PROJECT}/databases/{database}/sql/execute",
                {"statements": [{"sql": text}]}, headers, sent=sent)


def ulid():
    value = (int(time.time() * 1000) << 80) | secrets.randbits(80)
    alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"
    return "".join(alphabet[(value >> shift) & 31] for shift in range(125, -1, -5))


def shape(database, query):
    deadline = time.monotonic() + 45
    while True:
        status, body, headers = call("GET", f"/v1/projects/{PROJECT}/databases/{database}/tables/items/shape?" + urllib.parse.urlencode(query))
        if status not in (429, 500, 502, 503, 504) or time.monotonic() >= deadline:
            break
        READ_RETRIES.append({"status": status, "code": body.get("error", {}).get("code"),
                             "request_id": headers.get("x-request-id"), "at_unix": time.time()})
        time.sleep(0.1)
    require(status, 200, body, "shape")
    if query.get("live") == "true" and headers.get("x-terrabase-region") != "sjc":
        raise RuntimeError("shape did not answer in sjc")
    return body


class Tail:
    def __init__(self, database):
        self.database = database
        self.handle = None
        self.offset = "-1"
        self.local = "0"
        end = time.monotonic() + 120
        last_error = None
        while time.monotonic() < end:
            query = {"offset": self.offset}
            if self.handle:
                query["handle"] = self.handle
            try:
                page = shape(database, query)
                self.accept(page)
                if page.get("up_to_date") and not self.offset.startswith("snap."):
                    # Snapshot pages can be served by home. Force an immediate
                    # live response to qualify sjc's reader before measuring.
                    self.local = "1111111111111111"
                    self.next()
                    return
            except RuntimeError as error:
                last_error = str(error)
                time.sleep(0.2)
        raise RuntimeError("replica shape did not bootstrap: " + str(last_error))

    def accept(self, page):
        self.handle = page.get("handle", self.handle)
        self.offset = page.get("next_offset", self.offset)
        self.local = page.get("local", self.local)

    def next(self):
        page = shape(self.database, {"handle": self.handle, "offset": self.offset,
                                    "live": "true", "local": self.local})
        self.accept(page)
        return page

    def follow(self, row):
        evidence = {"pages": [], "provisional_at": None, "settled_at": None}
        end = time.monotonic() + 70
        pending_id = None
        while time.monotonic() < end:
            page = self.next()
            at = time.perf_counter()
            found = [p for p in page.get("provisional", [])
                     if any(op.get("row", {}).get("id") == row and op.get("row", {}).get("v") == "sjc"
                            for op in p.get("ops", []))]
            if found and pending_id is None:
                pending_id = found[0]["id"]
                evidence.update(provisional_at=at, transaction=pending_id)
            changes = page.get("changes", [])
            confirmed = [x for change in changes for x in change.get("confirmed", [])]
            reverted = [x for change in changes for x in change.get("revert", [])]
            ops = [op for change in changes for op in change.get("ops", []) if op.get("row", {}).get("id") == row]
            evidence["pages"].append({"next_offset": self.offset, "local": self.local,
                                      "provisional": found, "confirmed": confirmed, "revert": reverted, "ops": ops})
            if pending_id and pending_id in confirmed + reverted:
                if any(p["id"] == pending_id for p in page.get("provisional", [])):
                    raise RuntimeError("settled transaction remained provisional")
                evidence.update(settled_at=at, state="revert" if pending_id in reverted else "confirmed")
                return evidence
            if not pending_id and ops:
                evidence.update(settled_at=at, state="confirmed_without_provisional")
                return evidence
        raise RuntimeError("tail did not settle the transaction")


def measure(database, tail, pool, row, text):
    # Qualify the parked request before starting the measured write. This delay
    # is outside the measurement; it is not an assertion or a service test.
    future = pool.submit(tail.follow, row)
    time.sleep(0.15)
    started = time.perf_counter()
    status, body, headers = sql(database, text, {"TerraScale-Durability": "home"})
    ack = time.perf_counter()
    evidence = future.result(timeout=75)
    normalized = {key.lower(): value for key, value in headers.items()}
    evidence.update(status=status, code=body.get("error", {}).get("code") if isinstance(body, dict) else None,
                    route=normalized.get("terrascale-write-route"), region=normalized.get("x-terrabase-region"),
                    write_ms=1000 * (ack - started),
                    write_to_provisional_ms=None if evidence["provisional_at"] is None else 1000 * (evidence["provisional_at"] - started),
                    ack_to_provisional_ms=None if evidence["provisional_at"] is None else 1000 * (evidence["provisional_at"] - ack),
                    ack_to_settled_ms=1000 * (evidence["settled_at"] - ack))
    evidence.pop("provisional_at")
    evidence.pop("settled_at")
    return evidence


def main():
    database = "f7local" + str(int(time.time()))
    path = f"/v1/projects/{PROJECT}/databases/{database}"
    status, body, _ = call("POST", f"/v1/projects/{PROJECT}/databases",
                            {"name": database, "homeRegion": "gru", "replicaRegions": ["sjc"]})
    require(status, 201, body, "create database")
    result = {"database": database, "region": "sjc", "warmup": [], "repeats": [],
              "revert_attempts": [], "read_retries": READ_RETRIES}
    try:
        status, body, _ = sql(database, "CREATE TABLE items (id TEXT PRIMARY KEY, v TEXT)")
        require(status, 200, body, "create table")
        tail = Tail(database)
        print("sjc shape bootstrapped", file=sys.stderr, flush=True)
        with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
            for _ in range(5):
                row = ulid()
                sample = measure(database, tail, pool, row, f"INSERT INTO items (id,v) VALUES ('{row}','sjc')")
                result["warmup"].append(sample)
            for repeat in range(1, 4):
                samples = []
                for _ in range(20):
                    row = ulid()
                    sample = measure(database, tail, pool, row, f"INSERT INTO items (id,v) VALUES ('{row}','sjc')")
                    if sample["status"] != 200 or sample["region"] != "sjc" or "commit=local" not in (sample["route"] or ""):
                        raise RuntimeError("write did not use sjc's local commit path")
                    if sample["state"] != "confirmed" or sample["ack_to_provisional_ms"] is None:
                        raise RuntimeError("local write did not show provisional then confirmed")
                    samples.append(sample)
                result["repeats"].append({"repeat": repeat, "samples": samples,
                                          "p50_ack_to_provisional_ms": statistics.median(s["ack_to_provisional_ms"] for s in samples)})
                print(json.dumps({"repeat": repeat, "local_samples": len(samples),
                                  "p50_ack_to_provisional_ms": result["repeats"][-1]["p50_ack_to_provisional_ms"]}), file=sys.stderr, flush=True)

            # Dispatch a home insert, then insert the same key in sjc while
            # that request crosses the region link. Home's insert reaches gru
            # before the local push, while sjc still has its preceding copy.
            # Record any race the local push wins and retry with a fresh key.
            for attempt in range(1, 16):
                row = ulid()
                future = pool.submit(tail.follow, row)
                time.sleep(0.15)
                sent = threading.Event()
                home = pool.submit(sql, database, f"INSERT INTO items (id,v) VALUES ('{row}','gru')",
                                   {"TerraScale-Write": "home"}, sent)
                if not sent.wait(10):
                    raise RuntimeError("home insert did not dispatch")
                time.sleep(0.01)
                status, body, headers = sql(database, f"INSERT INTO items (id,v) VALUES ('{row}','sjc')", {"TerraScale-Durability": "home"})
                home_status, home_body, _ = home.result(timeout=30)
                evidence = future.result(timeout=75)
                evidence.update(attempt=attempt, local_status=status,
                                home_status=home_status,
                                home_code=home_body.get("error", {}).get("code") if isinstance(home_body, dict) else None,
                                local_code=body.get("error", {}).get("code") if isinstance(body, dict) else None)
                result["revert_attempts"].append(evidence)
                if evidence["state"] == "revert":
                    if evidence["local_code"] not in ("primary_key_violation", "unique_violation") or status == 200 or home_status != 200:
                        raise RuntimeError("reverted write did not report the home's constraint error")
                    if not any(op.get("row", {}).get("v") == "gru"
                               for page in evidence["pages"] for op in page["ops"]):
                        raise RuntimeError("reverted tail did not contain the home winner")
                    status, body, _ = sql(database, f"SELECT v FROM items WHERE id='{row}'", {"TerraScale-Write": "home"})
                    require(status, 200, body, "verify home winner")
                    if body["results"][0]["rows"] != [["gru"]]:
                        raise RuntimeError("home winner was not preserved")
                    result["revert_proven"] = True
                    break
            if not result.get("revert_proven"):
                raise RuntimeError("no home-rejected local transaction reached the tail")
    finally:
        status, body, _ = call("DELETE", path)
        result["database_deleted"] = status in (200, 204, 404)
        print(json.dumps(result), flush=True)
        require(status, 204, body, "delete database")


if __name__ == "__main__":
    main()
