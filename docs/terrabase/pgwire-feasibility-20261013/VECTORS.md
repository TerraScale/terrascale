# Offline fixture review

Run the documentation check below from the repository root with `nice -n 19 python3`. It reads only [vectors.json](vectors.json); it creates no sockets, native connections, servers, authentication payloads or files.

The checked-in bytes are frozen specification examples, not captures. The fixed CancelRequest PID/key is inert and has never been registered. Startup uses untrusted fixture labels, not a real tenant or accepted production selector. There is no PasswordMessage, JWT, API key or connection credential in these fixtures.

## Coverage and limits

1. **33 valid wire examples:** startup 3.0/3.2, SSL/GSSENC/cancel requests, version fallback, simple/extended messages, Ready states, fixed RowDescription, Unicode/NULL DataRows, completion and ErrorResponse.
2. **9 invalid examples:** lengths, incomplete input at EOF, allocation ceiling, malformed startup, duplicate startup keys, strict UTF-8, extra bytes after a String and illegal negative DataRow lengths. An incomplete read is not invalid until EOF/deadline; a transport must retain or reject it at the correct stage.
3. **15 values:** empty versus SQL NULL versus JSON null, signed int64 extrema, an integer beyond exact double precision, decimal token preservation, bool/float binary specification samples, CJK/emoji, composed/decomposed Unicode and escaped JSON NUL.
4. **16 proposed SQLSTATE mappings and 13 proposed flows:** these document expected behavior, including Sync recovery, auth-negative categories and explicit LIMIT/truncation distinctions. No real service implemented or executed these outcomes.

The Python inspector checks documented packet structure, exact decoded values, byte lengths, numeric preservation, SQLSTATE membership and fixture consistency. It is not a server/parser implementation or a TerraBase integration test. It cannot establish SQL acceptance, auth, TLS, cancellation, snapshot isolation, client compatibility or durability. Future integration cases belong in the existing Fast/Slow suites, with isolated synthetic resources and parallel execution.

The primary protocol/type/error references and exact versions are in [EVIDENCE.md](EVIDENCE.md). Binary scalar examples are specification aids; R1 has text-format id/json output only. Error and flow expectations are design proposals.

## Documentation check

This block is also extracted and executed by the scoped check recorded in [CHECKS.md](CHECKS.md). Standard-library code only; no installation needed.

```python
from pathlib import Path
from decimal import Decimal
import json
import struct

root = Path("docs/terrabase/pgwire-feasibility-20261013")
data = json.loads((root / "vectors.json").read_text())
assert data["producer_head"] == "e30ffdc287530551b161b0130220be53d3c0475e"
assert data["protocol_target"] == "3.0"

class Cursor:
    def __init__(self, raw):
        self.raw, self.pos = raw, 0

    def take(self, count):
        assert 0 <= count <= len(self.raw) - self.pos
        start = self.pos
        self.pos += count
        return self.raw[start:self.pos]

    def integer(self, fmt):
        size = struct.calcsize(fmt)
        return struct.unpack(fmt, self.take(size))[0]

    def string(self):
        end = self.raw.index(0, self.pos)
        result = self.take(end - self.pos).decode("utf-8", errors="strict")
        assert self.take(1) == b"\0"
        return result

    def value(self):
        count = self.integer("!i")
        assert count >= -1
        return None if count == -1 else self.take(count).decode("utf-8", errors="strict")

    def end(self):
        assert self.pos == len(self.raw)

def payload(frame):
    raw = bytes.fromhex(frame["hex"])
    offset = 1 if frame["kind"] == "typed" else 0
    assert len(raw) >= offset + 4
    size = struct.unpack("!I", raw[offset:offset + 4])[0]
    minimum, maximum = (4, 65536) if offset else (8, 8192)
    assert minimum <= size <= maximum
    assert len(raw) == size + offset
    return raw[:offset].decode("ascii"), raw[offset + 4:]

def startup_pairs(raw):
    parts = raw.split(b"\0")
    assert parts[-2:] == [b"", b""]
    parts = parts[:-2]
    assert len(parts) % 2 == 0
    pairs = {}
    for index in range(0, len(parts), 2):
        key, value = (part.decode("utf-8", errors="strict") for part in parts[index:index + 2])
        assert key and key not in pairs
        pairs[key] = value
    return pairs

for frame in data["frames"]:
    kind, body = payload(frame)
    expected = frame["expected"]
    cursor = Cursor(body)
    if frame["kind"] == "initial":
        assert cursor.integer("!I") == expected["code"]
        if "pairs" in expected:
            assert startup_pairs(cursor.take(len(body) - cursor.pos)) == expected["pairs"]
        elif "pid" in expected:
            assert cursor.integer("!I") == expected["pid"]
            assert cursor.take(4).hex() == expected["key_hex"]
    else:
        assert kind == expected["type"]
        if "query" in expected and kind == "Q":
            assert cursor.string() == expected["query"]
        elif kind == "P":
            assert cursor.string() == expected["statement"]
            assert cursor.string() == expected["query"]
            assert [cursor.integer("!I") for _ in range(cursor.integer("!H"))] == expected["parameter_oids"]
        elif kind == "B":
            assert cursor.string() == expected["portal"]
            assert cursor.string() == expected["statement"]
            assert [cursor.integer("!H") for _ in range(cursor.integer("!H"))] == expected["parameter_formats"]
            assert [cursor.value() for _ in range(cursor.integer("!H"))] == expected["parameters"]
            assert [cursor.integer("!H") for _ in range(cursor.integer("!H"))] == expected["result_formats"]
        elif "columns" in expected:
            columns = []
            for _ in range(cursor.integer("!H")):
                columns.append(dict(zip(
                    ["name", "table_oid", "attribute", "oid", "type_length", "typmod", "format"],
                    [cursor.string(), cursor.integer("!I"), cursor.integer("!h"), cursor.integer("!I"),
                     cursor.integer("!h"), cursor.integer("!i"), cursor.integer("!H")],
                )))
            assert columns == expected["columns"]
        elif "values" in expected:
            assert [cursor.value() for _ in range(cursor.integer("!H"))] == expected["values"]
        elif "target" in expected:
            assert cursor.take(1).decode() == expected["target"]
            assert cursor.string() == expected["name"]
        elif "max_rows" in expected:
            assert cursor.string() == expected["portal"]
            assert cursor.integer("!I") == expected["max_rows"]
        elif "fields" in expected:
            fields = {}
            while (field := cursor.take(1)) != b"\0":
                key = field.decode()
                assert key not in fields
                fields[key] = cursor.string()
            assert fields == expected["fields"]
        elif "tag" in expected:
            assert cursor.string() == expected["tag"]
        elif "status" in expected:
            assert cursor.take(1).decode() == expected["status"]
        elif "auth_code" in expected:
            assert cursor.integer("!I") == expected["auth_code"]
        elif "minor" in expected:
            assert cursor.integer("!I") == expected["minor"]
            assert cursor.integer("!I") == expected["unsupported_options"]
    cursor.end()
for frame in data["invalid_frames"]:
    rejected = False
    try:
        kind, body = payload(frame)
        cursor = Cursor(body)
        if frame["kind"] == "initial":
            cursor.integer("!I")
            startup_pairs(cursor.take(len(body) - cursor.pos))
        elif kind == "Q":
            cursor.string()
        elif kind == "D":
            for _ in range(cursor.integer("!H")):
                cursor.value()
        cursor.end()
    except (AssertionError, ValueError, UnicodeDecodeError):
        rejected = True
    assert rejected, frame["name"]

values = {value["name"]: value for value in data["values"]}
for value in values.values():
    text = value["text"]
    assert (None if text is None else text.encode("utf-8").hex()) == value["utf8_hex"]
    if value["domain"] == "json":
        json.loads(text, parse_int=int, parse_float=Decimal)
    if "binary_hex" in value and value["oid"] == 20:
        assert struct.unpack("!q", bytes.fromhex(value["binary_hex"]))[0] == int(text)
    if "binary_hex" in value and value["oid"] == 701:
        assert struct.unpack("!d", bytes.fromhex(value["binary_hex"]))[0] == float(text)
assert values["sql_null"]["text"] is None
assert values["empty_text"]["utf8_hex"] == ""
assert values["json_null"]["utf8_hex"] == "6e756c6c"
assert len(bytes.fromhex(values["unicode"]["utf8_hex"])) == 16
assert values["composed"]["utf8_hex"] != values["decomposed"]["utf8_hex"]
assert int(float(values["integer_beyond_exact_double"]["text"])) != int(values["integer_beyond_exact_double"]["text"])
assert str(Decimal(values["precise_decimal"]["text"])) == values["precise_decimal"]["text"]
assert json.loads(values["escaped_json_nul"]["text"]) == "\0"
assert values["bool_true"]["binary_hex"] == "01"
assert bytes.fromhex(values["json_binary_layout"]["binary_hex"]).decode() == values["json_binary_layout"]["text"]

# Transcribed from the pinned PostgreSQL errcodes.txt, not generated from the mappings.
known_codes = {"08P01", "0A000", "42601", "22021", "22003", "22P02", "28P01", "42501",
               "3D000", "57014", "54000", "53300", "53200", "55000", "XX000"}
for mapping in data["error_mappings"]:
    assert mapping["sqlstate"] in known_codes
for frame in data["invalid_frames"]:
    assert frame["expected_sqlstate"] in known_codes
flows = {flow["name"]: flow for flow in data["proposed_flows"]}
assert flows["explicit_limit_reached"]["rows"] == flows["explicit_limit_reached"]["limit"]
assert flows["byte_truncation"]["rows"] < flows["byte_truncation"]["limit"]
assert flows["wire_size_overflow"]["encoded_result_bytes"] > 8 * 1024 * 1024
for group in ["frames", "invalid_frames", "values", "proposed_flows"]:
    names = [item["name"] for item in data[group]]
    assert len(names) == len(set(names))
assert len(data["frames"]) == 33
assert len(data["invalid_frames"]) == 9
assert len(data["values"]) == 15
assert len(data["error_mappings"]) == 16
assert len(data["proposed_flows"]) == 13
print("PASS: 33 wire examples, 9 invalid examples, 15 values, 16 SQLSTATE mappings; 13 flow expectations structurally checked")
```

No live assertions were made about the proposed flow/auth outcomes. Invalid examples are confirmed invalid by this fixture inspector; the check does not demonstrate that a future server will reject them or choose the expected SQLSTATE. Socket fragmentation, TLS transitions, cooperative cancellation, byte admission and integration outcomes require the separately admitted real-server gates in README.
