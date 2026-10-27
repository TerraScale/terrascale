#!/usr/bin/env python3
"""Production endpoint replication matrix (RT6), run by endpoint-replication-proof.sh on a ts-orch host.

Python standard library only. HTTP goes through curl (h2c) to every region's internal listener as the platform operator.
Postgres and Wire use each region's public TLS edge with a database-scoped key issued for the run. Arguments: the work
directory holding `peer` (the peer credential header), `user` (the acting user header) and `peers` (the ts-orch peers
file), the target TerraBase SHA, and the output JSON-lines file. The tenant is created here and deleted afterwards.

Writes are never retried. elapsed_ms runs from the write's acknowledgement to the end of the first matching read,
including the client's process and network time. Reads poll every 50 ms, up to 30 s.
"""

import concurrent.futures
import json
import os
import secrets
import socket
import ssl
import struct
import subprocess
import sys
import threading
import time
import traceback

REGIONS = ("gru", "sjc", "fra")
PUBLIC = {"gru": "163.176.124.12", "sjc": "163.192.54.17", "fra": "130.162.238.198"}
ENDPOINTS = ("documents", "sql", "postgres", "hrana", "wire")
RELATIONAL = ("sql", "postgres", "hrana")
NATIVE_RELAYS = {}
LOCAL = threading.local()
DEADLINE = 30


def ulid():
    number = (int(time.time() * 1000) << 80) | secrets.randbits(80)
    alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"
    return "".join(alphabet[(number >> shift) & 31] for shift in range(125, -1, -5))


class HttpError(Exception):
    def __init__(self, status, body):
        super().__init__(f"{status} {body[:300]!r}")
        self.status, self.body = status, body


class PgError(Exception):
    pass


class WireError(Exception):
    def __init__(self, status, code, message):
        super().__init__(f"{status} {code}: {message}")
        self.status, self.code = status, code


def receive(stream, size):
    data = b""
    while len(data) < size:
        part = stream.recv(size - len(data))
        if not part:
            raise ConnectionError("connection closed")
        data += part
    return data


class Pg:
    """PostgreSQL protocol 3.0 simple queries over the edge's TLS."""

    def __init__(self, region, key, database):
        raw = socket.create_connection(("127.0.0.1", NATIVE_RELAYS[region]["postgres"]), timeout=20)
        raw.sendall(struct.pack("!II", 8, 80877103))
        if receive(raw, 1) != b"S":
            raise PgError("TLS refused")
        self.stream = ssl.create_default_context().wrap_socket(raw, server_hostname="pg.terrascale.tech")
        params = b"".join(part.encode() + b"\0" for part in ("user", "wire", "database", database, "application_name", "rt6")) + b"\0"
        self.stream.sendall(struct.pack("!II", 8 + len(params), 196608) + params)
        while True:
            kind, body = self.message()
            if kind == b"R" and struct.unpack("!I", body[:4])[0] == 3:
                secret = key.encode() + b"\0"
                self.stream.sendall(b"p" + struct.pack("!I", 4 + len(secret)) + secret)
            elif kind == b"E":
                raise PgError(self.fields(body))
            elif kind == b"Z":
                return

    def message(self):
        kind = receive(self.stream, 1)
        size = struct.unpack("!I", receive(self.stream, 4))[0]
        return kind, receive(self.stream, size - 4)

    @staticmethod
    def fields(body):
        return {chr(part[0]): part[1:].decode() for part in body.split(b"\0") if part}

    def query(self, sql):
        text = sql.encode() + b"\0"
        self.stream.sendall(b"Q" + struct.pack("!I", 4 + len(text)) + text)
        rows, error = [], None
        while True:
            kind, body = self.message()
            if kind == b"D":
                count, offset, row = struct.unpack("!H", body[:2])[0], 2, []
                for _ in range(count):
                    size = struct.unpack("!i", body[offset:offset + 4])[0]
                    offset += 4
                    row.append(None if size < 0 else body[offset:offset + size].decode())
                    offset += max(size, 0)
                rows.append(row)
            elif kind == b"E":
                error = self.fields(body)
            elif kind == b"Z":
                if error:
                    raise PgError(error)
                return rows


class Wire:
    """Wire protocol v1 over the edge's TLS (docs/terrabase/wire-protocol.md)."""

    def __init__(self, region, key, project, database):
        raw = socket.create_connection(("127.0.0.1", NATIVE_RELAYS[region]["wire"]), timeout=20)
        self.stream = ssl.create_default_context().wrap_socket(raw, server_hostname="api.terrascale.tech")
        self.next = 0
        status, body = self.call(0, b"\x01" + b"".join(self.string(part) for part in (key, project, database)))
        if status != 200 or body != b"\x01":
            raise ConnectionError("unexpected Wire hello version")

    @staticmethod
    def string(value):
        data = value.encode()
        return struct.pack("<H", len(data)) + data

    @staticmethod
    def address(collection, identifier):
        return bytes([len(collection)]) + collection.encode() + identifier.encode()

    def call(self, operation, body):
        self.next += 1
        self.stream.sendall(struct.pack("<IIB", 5 + len(body), self.next, operation) + body)
        size = struct.unpack("<I", receive(self.stream, 4))[0]
        if not 6 <= size <= 1049600:
            raise ConnectionError("invalid Wire answer length")
        answer = receive(self.stream, size)
        request, status = struct.unpack("<IH", answer[:6])
        if request != self.next:
            raise ConnectionError("wire request id mismatch")
        if status >= 300:
            length = answer[6]
            raise WireError(status, answer[7:7 + length].decode(), answer[7 + length:].decode())
        return status, answer[6:]

    def get(self, collection, identifier):
        try:
            _, head = self.call(1, self.address(collection, identifier))
        except WireError as error:
            if error.status == 404 and error.code == "not_found":
                return None
            raise
        return json.loads(head[1 + head[0]:])

    def put(self, collection, identifier, document):
        status, _ = self.call(2, self.address(collection, identifier) + b"\x00" + json.dumps(document).encode())
        return status

    def delete(self, collection, identifier):
        status, _ = self.call(3, self.address(collection, identifier) + b"\x00")
        if status != 200:
            raise RuntimeError(f"unexpected Wire delete status {status}")


class Matrix:
    def __init__(self, work, sha, output):
        self.work, self.sha, self.output = work, sha, output
        self.lock = threading.Lock()
        self.node = {}
        with open(os.path.join(os.path.dirname(__file__), "native-relays.json"), encoding="utf-8") as stream:
            NATIVE_RELAYS.update(json.load(stream))
        with open(os.path.join(work, "peers"), encoding="utf-8") as stream:
            for line in stream:
                parts = line.split()
                if len(parts) == 3:
                    self.node[parts[1]] = (parts[0], parts[2])
        missing = [region for region in REGIONS if region not in self.node]
        if missing:
            raise RuntimeError(f"regions absent from the peers file: {missing}")

    def record(self, **result):
        result.update(target_sha=self.sha, at=time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()))
        with self.lock:
            self.output.write(json.dumps(result) + "\n")
            self.output.flush()

    # HTTP over the internal listener: `user` adds the acting user (Management API only).
    def http(self, region, method, path, body=None, user=False, expect=None):
        command = ["curl", "-sS", "--http2-prior-knowledge", "-m", "30", "-D", "-", "-X", method,
                   "-H", "@" + os.path.join(self.work, "peer")]
        if user:
            command += ["-H", "@" + os.path.join(self.work, "user")]
        if body is not None:
            command += ["-H", "Content-Type: application/json", "--data-binary", "@-"]
        command.append(f"http://{self.node[region][1]}:8081{path}")
        done = subprocess.run(command, input=None if body is None else json.dumps(body).encode(), capture_output=True, check=False)
        if done.returncode != 0:
            raise ConnectionError(f"curl {done.returncode}: {done.stderr.decode()[:200]}")
        head, _, payload = done.stdout.partition(b"\r\n\r\n")
        lines = head.decode().split("\r\n")
        status = int(lines[0].split()[1])
        headers = {name.strip().lower(): value.strip() for name, _, value in (line.partition(":") for line in lines[1:])}
        served = headers.get("x-terrabase-region")
        if served is not None and served != region:
            raise RuntimeError(f"requested {region}, served by {served}")
        text = payload.decode()
        if expect is not None and status not in expect:
            raise HttpError(status, text)
        return status, (json.loads(text) if text.strip() else None)

    def data(self, route):
        return f"/v1/projects/{self.project}/databases/{route}"

    def pg(self, region, route=None):
        route = route or self.name
        connection_key = (region, route)
        connections = LOCAL.__dict__.setdefault("pg", {})
        if connection_key not in connections:
            connections[connection_key] = Pg(region, self.key, f"{self.project}/{route}")
        return connections[connection_key]

    def wire(self, region, route=None):
        route = route or self.name
        connection_key = (region, route)
        connections = LOCAL.__dict__.setdefault("wire", {})
        if connection_key not in connections:
            connections[connection_key] = Wire(region, self.key, self.project, route)
        return connections[connection_key]

    def drop_connection(self, kind, region, route):
        connection = getattr(LOCAL, kind, {}).pop((region, route), None)
        if connection is not None:
            connection.stream.close()

    def sql(self, region, endpoint, statements, route):
        if endpoint == "postgres":
            rows = []
            for statement in statements:
                rows = self.pg(region, route).query(statement)
            return rows
        if endpoint == "hrana":
            requests = [{"type": "execute", "stmt": {"sql": statement, "want_rows": True}} for statement in statements]
            _, body = self.http(region, "POST", self.data(route) + "/libsql/v2/pipeline", {"requests": requests + [{"type": "close"}]}, expect={200})
            results = body["results"][:-1]
            for result in results:
                if result["type"] != "ok":
                    raise HttpError(200, json.dumps(result))
            return [[cell.get("value") for cell in row] for row in results[-1]["response"]["result"]["rows"]]
        _, body = self.http(region, "POST", self.data(route) + "/sql/execute", {"statements": [{"sql": statement} for statement in statements]}, expect={200})
        return body["results"][-1]["rows"]

    def document(self, route, table, identifier):
        return self.data(route) + f"/collections/{table}/documents/by-key?id={identifier}"

    def write(self, region, endpoint, table, identifier, value, operation, route):
        if endpoint == "documents":
            if operation == "delete":
                self.http(region, "DELETE", self.document(route, table, identifier), expect={200, 204})
            else:
                self.http(region, "PUT", self.document(route, table, identifier), {"value": value}, expect={200, 201})
            return
        if endpoint == "wire":
            if operation == "delete":
                self.wire(region, route).delete(table, identifier)
            else:
                status = self.wire(region, route).put(table, identifier, {"value": value})
                if status != (201 if operation == "insert" else 200):
                    raise RuntimeError(f"unexpected Wire {operation} status {status}")
            return
        statement = {
            "insert": f"INSERT INTO {table} VALUES ('{identifier}', '{value}')",
            "update": f"UPDATE {table} SET value = '{value}' WHERE id = '{identifier}'",
            "delete": f"DELETE FROM {table} WHERE id = '{identifier}'",
        }[operation]
        self.sql(region, endpoint, [statement], route)

    def read(self, region, endpoint, table, identifier, route):
        if endpoint == "documents":
            status, body = self.http(region, "GET", self.document(route, table, identifier), expect={200, 404})
            if status == 404:
                # An unknown database or collection must never count as a deleted row.
                if body["error"]["code"] != "not_found":
                    raise HttpError(status, json.dumps(body))
                return None
            return body["document"].get("value")
        if endpoint == "wire":
            document = self.wire(region, route).get(table, identifier)
            return None if document is None else document.get("value")
        rows = self.sql(region, endpoint, [f"SELECT value FROM {table} WHERE id = '{identifier}'"], route)
        return rows[0][0] if rows else None

    def observe(self, source, writer, target, reader, table, identifier, value, operation, ack, repeat, route):
        errors = 0
        while True:
            try:
                matched = self.read(target, reader, table, identifier, route) == value
            except (OSError, HttpError, PgError, WireError, KeyError, ValueError) as error:
                errors += 1
                if reader in ("postgres", "wire"):
                    self.drop_connection("pg" if reader == "postgres" else "wire", target, route)
                self.record(kind="read_error", target=target, reader=reader, source=source, writer=writer,
                            operation=operation, repeat=repeat, error=f"{type(error).__name__}: {error}")
                retryable = (isinstance(error, OSError) and not isinstance(error, ssl.SSLCertVerificationError)
                             or isinstance(error, HttpError) and error.status in (429, 503)
                             or isinstance(error, WireError) and error.status in (429, 503)
                             or isinstance(error, PgError) and isinstance(error.args[0], dict)
                                and error.args[0].get("C") in ("57P03", "53200", "53300"))
                if not retryable:
                    raise RuntimeError(f"{target}/{reader}: {type(error).__name__}: {error}") from error
                matched = False
            elapsed = time.monotonic() - ack
            if matched or elapsed >= DEADLINE:
                self.record(kind="observation", source=source, writer=writer, target=target, reader=reader, operation=operation,
                            repeat=repeat, route=route, matched=matched, read_errors=errors, elapsed_ms=round(elapsed * 1000, 3))
                return matched
            time.sleep(0.05)

    def everywhere(self, pool, source, writer, table, identifier, value, operation, ack, repeat, route):
        futures = [pool.submit(self.observe, source, writer, target, reader, table, identifier, value, operation, ack, repeat, route)
                   for target in REGIONS for reader in ENDPOINTS]
        lost = sum(1 for future in futures if not future.result())
        if lost:
            raise RuntimeError(f"lost: {source}/{writer} {operation} unseen by {lost} readers")

    def table_everywhere(self, table, route):
        for region in REGIONS:
            for reader in RELATIONAL:
                deadline = time.monotonic() + DEADLINE
                while True:
                    try:
                        self.sql(region, reader, [f"SELECT value FROM {table}"], route)
                        break
                    except (HttpError, PgError) as error:
                        if "no such table" not in str(error) and "does not exist" not in str(error) or time.monotonic() >= deadline:
                            raise
                        time.sleep(0.05)

    def provision(self):
        _, body = self.http("sjc", "POST", "/v1/management/organizations", {"name": "rt6 endpoint matrix"}, user=True, expect={201})
        self.organization = body["id"]
        _, body = self.http("sjc", "POST", f"/v1/management/organizations/{self.organization}/projects", {"name": "rt6"}, user=True, expect={201})
        self.project = body["id"]
        self.management = f"/v1/management/organizations/{self.organization}/projects/{self.project}"
        self.record(kind="tenant", organization=self.organization, project=self.project)
        # The project must reach every region before a database is placed in all of them.
        deadline = time.monotonic() + 120
        while any(self.http(region, "GET", self.management, user=True)[0] != 200 for region in REGIONS):
            if time.monotonic() >= deadline:
                raise RuntimeError("the project did not reach every region")
            time.sleep(1)
        self.name = "rt6-" + secrets.token_hex(4)
        _, body = self.http("sjc", "POST", self.management + "/databases", {"name": self.name, "allRegions": True}, user=True, expect={201})
        self.database = body["id"]
        _, body = self.http("sjc", "POST", "/admin/api-keys",
                            {"name": "rt6 matrix", "scopes": ["documents:read", "documents:write"], "organizationId": self.organization,
                             "projectId": self.project, "databaseId": self.database}, expect={201})
        self.key_id, self.key = body["id"], body["secret"]
        self.record(kind="database", database=self.database, name=self.name, key_id=self.key_id)
        for route in (self.name, self.database):
            for region in REGIONS:
                deadline = time.monotonic() + 120
                while True:
                    try:
                        rows = self.sql(region, "sql", ["SELECT 1"], route)
                        if rows != [[1]]:
                            raise RuntimeError(f"unexpected SQL route probe result in {region}: {rows}")
                        break
                    except HttpError as error:
                        if error.status not in (404, 503) or time.monotonic() >= deadline:
                            raise
                        time.sleep(0.5)
                self.record(kind="route", target=region, route="ulid" if route == self.database else "name", matched=True)

    def prepare_transports(self):
        for route in (self.name, self.database):
            for region in REGIONS:
                for transport in ("postgres", "wire"):
                    deadline = time.monotonic() + 120
                    while True:
                        try:
                            self.record(kind="socket_probe", target=region, reader=transport, route=route)
                            if transport == "postgres":
                                expected = f"{self.project}/{route}"
                                if self.pg(region, route).query("SELECT current_database()") != [[expected]]:
                                    raise RuntimeError("Postgres did not preserve its configured database selector")
                            else:
                                self.wire(region, route)
                            break
                        except (PgError, WireError) as error:
                            retryable = (isinstance(error, PgError) and isinstance(error.args[0], dict)
                                         and error.args[0].get("C") in ("28P01", "57P03")
                                         or isinstance(error, WireError) and error.status in (401, 503))
                            if not retryable or time.monotonic() >= deadline:
                                raise
                            time.sleep(0.5)
                    self.record(kind="socket_route", target=region, reader=transport,
                                route="ulid" if route == self.database else "name", matched=True)

    def attribute_native_writes(self):
        table = "rt6_origin_" + secrets.token_hex(3)
        self.sql("sjc", "sql", [f"CREATE TABLE {table} (id TEXT PRIMARY KEY, value TEXT)"], self.name)
        self.table_everywhere(table, self.name)
        for region in REGIONS:
            markers = {ulid(): endpoint for endpoint in ("documents", "postgres", "wire")}
            for identifier, endpoint in markers.items():
                self.write(region, endpoint, table, identifier, region, "insert", self.name)
            origins = {}
            process = subprocess.Popen(
                ["curl", "-sSN", "--http2-prior-knowledge", "-m", str(DEADLINE),
                 "-H", "@" + os.path.join(self.work, "peer"),
                 f"http://{self.node[region][1]}:8081{self.data(self.name)}/changes?collections={table}"],
                stdout=subprocess.PIPE, stderr=subprocess.DEVNULL)
            try:
                for line in process.stdout:
                    if not line.startswith(b"data: "):
                        continue
                    event = json.loads(line[6:])
                    if event.get("id") in markers:
                        origins[markers[event["id"]]] = int(event["origin"], 16) >> 16
                    if len(origins) == 3:
                        break
            finally:
                process.kill()
                process.wait()
            if len(origins) != 3 or len(set(origins.values())) != 1:
                raise RuntimeError(f"native transport origin mismatch or missing evidence in {region}: {origins}")
            self.record(kind="native_origin", target=region, origins=origins, matched=True)

    def crud(self, pool):
        for repeat in range(3):
            route = self.database if repeat == 1 else self.name
            for endpoint in ENDPOINTS:
                for source in REGIONS:
                    table = f"rt6_{endpoint}_{source}_{repeat}_{secrets.token_hex(2)}"
                    self.record(kind="case_start", repeat=repeat, writer=endpoint, source=source, route=route, table=table)
                    ddl = endpoint if endpoint in RELATIONAL else "sql"
                    self.sql(source, ddl, [f"CREATE TABLE {table} (id TEXT PRIMARY KEY, value TEXT)"], route)
                    self.table_everywhere(table, route)
                    identifier = ulid()
                    for operation, value in (("insert", "created"), ("update", "updated"), ("delete", None)):
                        if endpoint == "wire" and operation == "delete" and not self.wire_delete[source]:
                            self.record(kind="blocked", source=source, writer="wire", operation="delete", repeat=repeat, reason="unknown_operation_on_serving_build")
                            continue
                        self.write(source, endpoint, table, identifier, value, operation, route)
                        self.everywhere(pool, source, endpoint, table, identifier, value, operation, time.monotonic(), repeat, route)
                    if endpoint in RELATIONAL:
                        other = ulid()
                        statements = [f"INSERT INTO {table} VALUES ('{identifier}', 'transaction')",
                                      f"INSERT INTO {table} VALUES ('{other}', 'transaction')",
                                      f"UPDATE {table} SET value = 'committed' WHERE id = '{identifier}'"]
                        if endpoint != "sql":
                            statements = ["BEGIN", *statements, "COMMIT"]
                        self.sql(source, endpoint, statements, route)
                        ack = time.monotonic()
                        self.everywhere(pool, source, endpoint, table, identifier, "committed", "commit", ack, repeat, route)
                        self.everywhere(pool, source, endpoint, table, other, "transaction", "commit", ack, repeat, route)
                    print(f"crud repeat {repeat + 1}: {endpoint}/{source} passed", flush=True)

    def alter(self):
        for endpoint in RELATIONAL:
            table = f"rt6_alter_{endpoint}_{secrets.token_hex(2)}"
            self.sql("sjc", "sql", [f"CREATE TABLE {table} (id TEXT PRIMARY KEY, value TEXT)"], self.name)
            try:
                self.sql("sjc", endpoint, [f"ALTER TABLE {table} ADD COLUMN note TEXT"], self.name)
                self.record(kind="alter", writer=endpoint, accepted=True)
            except (HttpError, PgError) as error:
                self.record(kind="alter", writer=endpoint, accepted=False, refusal=str(error)[:300])

    def conflicts(self, pool):
        pairs = (("gru", "sjc"), ("sjc", "fra"), ("fra", "gru"))
        for repeat in range(3):
            for index, writer in enumerate(ENDPOINTS):
                other_writer = ENDPOINTS[(index + 1) % len(ENDPOINTS)]
                left, right = pairs[(repeat + index) % 3]
                table = "rt6_lww_" + secrets.token_hex(5)
                self.sql(left, "sql", [f"CREATE TABLE {table} (id TEXT PRIMARY KEY, value TEXT)"], self.name)
                self.table_everywhere(table, self.name)
                # A marker per region maps each origin replica to its region.
                markers = {ulid(): region for region in REGIONS}
                for identifier, region in markers.items():
                    self.write(region, "documents", table, identifier, region, "insert", self.name)
                identifier = ulid()
                self.write(left, "documents", table, identifier, "seed", "insert", self.name)
                self.everywhere(pool, left, "documents", table, identifier, "seed", "seed", time.monotonic(), repeat, self.name)
                barrier = threading.Barrier(2)

                def mutate(region, endpoint):
                    self.read(region, endpoint, table, identifier, self.name)
                    barrier.wait(timeout=15)
                    self.write(region, endpoint, table, identifier, region, "update", self.name)

                for future in [pool.submit(mutate, left, writer), pool.submit(mutate, right, other_writer)]:
                    future.result()
                events, origins = {}, {}
                feed = threading.Lock()

                def collect(region):
                    # Union every region's feed: a losing remote merge need not emit a change.
                    process = subprocess.Popen(
                        ["curl", "-sSN", "--http2-prior-knowledge", "-m", str(DEADLINE), "-H", "@" + os.path.join(self.work, "peer"),
                         f"http://{self.node[region][1]}:8081{self.data(self.name)}/changes?collections={table}"],
                        stdout=subprocess.PIPE, stderr=subprocess.DEVNULL)
                    try:
                        for line in process.stdout:
                            if not line.startswith(b"data: "):
                                continue
                            event = json.loads(line[6:])
                            with feed:
                                if event.get("id") in markers:
                                    origins[int(event["origin"], 16) >> 16] = markers[event["id"]]
                                if event.get("id") == identifier:
                                    events[(int(event["version"]), int(event["origin"], 16))] = event
                                if len(events) == 3 and len(origins) == len(REGIONS):
                                    return
                    finally:
                        process.kill()
                        process.wait()

                for future in [pool.submit(collect, region) for region in REGIONS]:
                    future.result()
                if len(events) != 3 or len(origins) != len(REGIONS):
                    raise RuntimeError(f"incomplete LWW oracle: {len(events)} mutations, {len(origins)} regions")
                _, origin = max(events)
                winner = origins[origin >> 16]
                self.everywhere(pool, left, writer, table, identifier, winner, "lww", time.monotonic(), repeat, self.name)
                self.everywhere(pool, left, writer, table, identifier, winner, "lww_stability", time.monotonic(), repeat, self.name)
                self.record(kind="lww", repeat=repeat, writers=[writer, other_writer], writer_regions=[left, right], winner=winner,
                            versions=[[stamp, f"{source:016x}"] for stamp, source in sorted(events)])
                print(f"lww repeat {repeat + 1}: {writer}@{left} vs {other_writer}@{right}, winner {winner}", flush=True)

    def rollbacks(self, pool):
        table = "rt6_rollback_" + secrets.token_hex(3)
        self.sql("sjc", "sql", [f"CREATE TABLE {table} (id TEXT PRIMARY KEY, value TEXT)"], self.name)
        self.table_everywhere(table, self.name)
        for repeat in range(3):
            for endpoint in RELATIONAL:
                for source in REGIONS:
                    identifier, sentinel = ulid(), ulid()
                    insert = f"INSERT INTO {table} VALUES ('{identifier}', 'rolled_back')"
                    if endpoint == "sql":
                        try:
                            self.sql(source, endpoint, [insert, insert], self.name)
                            raise RuntimeError("a duplicate insert committed")
                        except HttpError as error:
                            if error.status != 400 or json.loads(error.body).get("error", {}).get("sqlite_code") != "SQLITE_CONSTRAINT_PRIMARYKEY":
                                raise
                    else:
                        self.sql(source, endpoint, ["BEGIN", insert, "ROLLBACK"], self.name)
                    self.write(source, endpoint, table, sentinel, "barrier", "insert", self.name)
                    self.everywhere(pool, source, endpoint, table, sentinel, "barrier", "barrier", time.monotonic(), repeat, self.name)
                    self.everywhere(pool, source, endpoint, table, identifier, None, "rollback", time.monotonic(), repeat, self.name)
            print(f"rollback repeat {repeat + 1} passed", flush=True)

    def probe_wire_delete(self):
        table = "rt6_wire_delete_" + secrets.token_hex(2)
        self.sql("sjc", "sql", [f"CREATE TABLE {table} (id TEXT PRIMARY KEY, value TEXT)"], self.name)
        self.table_everywhere(table, self.name)
        self.wire_delete = {}
        for region in REGIONS:
            try:
                self.wire(region).delete(table, ulid())
                raise RuntimeError("delete of a never-written identifier unexpectedly succeeded")
            except WireError as error:
                if (error.status, error.code) == (404, "not_found"):
                    self.wire_delete[region] = True
                elif (error.status, error.code) == (400, "unknown_operation"):
                    self.wire_delete[region] = False
                else:
                    raise
            self.record(kind="wire_delete", target=region, supported=self.wire_delete[region])

    def teardown(self):
        def remove(step, path, user=True):
            status, body = self.http("sjc", "DELETE", path, user=user)
            self.record(kind="teardown", step=step, status=status)
            print(f"teardown {step}: {status}", flush=True)
            if status not in (204, 404):
                raise HttpError(status, json.dumps(body))
        if getattr(self, "key_id", None):
            remove("revoke key", "/admin/api-keys/" + self.key_id, user=False)
        if getattr(self, "database", None):
            remove("delete database", self.management + "/databases/" + self.database)
        if getattr(self, "project", None):
            remove("delete project", self.management)
        if getattr(self, "organization", None):
            remove("delete organization", "/v1/management/organizations/" + self.organization)
        if getattr(self, "project", None):
            for region in REGIONS:
                deadline = time.monotonic() + 120
                while True:
                    status, _ = self.http(region, "GET", self.management, user=True)
                    if status == 404:
                        break
                    if time.monotonic() >= deadline:
                        raise RuntimeError(f"project cleanup has not reached {region}: {status}")
                    time.sleep(1)
                self.record(kind="teardown", step="project gone", target=region, status=status)


def main():
    work, sha, path = sys.argv[1:4]
    with open(path, "x", encoding="utf-8") as output:
        matrix = Matrix(work, sha, output)
        failed = None
        try:
            for region in REGIONS:
                status, body = matrix.http(region, "GET", "/readyz")
                matrix.record(kind="ready", target=region, status=status, node=matrix.node[region][0], body=body)
                if status != 200 or not body.get("revision", "").startswith(sha):
                    raise RuntimeError(f"serving revision mismatch in {region}")
            matrix.provision()
            matrix.prepare_transports()
            matrix.probe_wire_delete()
            matrix.attribute_native_writes()
            with concurrent.futures.ThreadPoolExecutor(max_workers=16) as pool:
                matrix.crud(pool)
                matrix.alter()
                matrix.conflicts(pool)
                matrix.rollbacks(pool)
            for region in REGIONS:
                status, body = matrix.http(region, "GET", "/readyz")
                matrix.record(kind="ready_end", target=region, status=status, body=body)
                if status != 200 or not body.get("revision", "").startswith(sha):
                    raise RuntimeError(f"serving revision changed in {region}")
        except Exception as error:  # Report, then always tear the tenant down.
            failed = f"{type(error).__name__}: {error}"
            matrix.record(kind="failure", error=failed, stack=traceback.format_exc())
            print("FAILED " + failed, flush=True)
        finally:
            try:
                matrix.teardown()
            except Exception as error:
                matrix.record(kind="failure", error=f"teardown: {type(error).__name__}: {error}")
                print(f"TEARDOWN FAILED {error}", flush=True)
                failed = failed or "teardown"
        gap = any(not supported for supported in getattr(matrix, "wire_delete", {}).values())
        result = "FAIL" if failed else "PASS_WITH_GAPS" if gap else "PASS"
        matrix.record(kind="summary", result=result, wire_delete=getattr(matrix, "wire_delete", {}))
        print("result: " + result, flush=True)
        sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
