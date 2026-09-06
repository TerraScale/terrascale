import assert from "node:assert/strict";
import { test, vi } from "vitest";
import { createTerraScaleClient } from "./factory.js";
import type { SqlValue } from "./models.js";
import { createTerraScaleManagementClient } from "./management.js";

function clientFor(body: string) {
  return createTerraScaleClient({
    fetch: async () => new Response(body, { headers: { "content-type": "application/json" } }),
  });
}

test("SQL row values preserve integer lexemes recursively without changing floats or strings", async () => {
  const client = clientFor('{"matched":1,"rows":[{"id":"row-1","value":{"min":-9223372036854775808,"max":9223372036854775807,"overSafe":9007199254740993,"safe":9007199254740991,"negativeSafe":-9007199254740991,"float":1.25,"exponent":1e20,"decimal":9223372036854775807.0,"text":"9223372036854775807","nested":[null,true,18446744073709551615]}}]}');
  const result = await client.sql.execute("demo", "SELECT * FROM orders");
  assert.ok(result.ok);
  assert.ok("rows" in result.value);
  const expected: SqlValue = {
    min: -9223372036854775808n, max: 9223372036854775807n,
    overSafe: 9007199254740993n, safe: 9007199254740991,
    negativeSafe: -9007199254740991, float: 1.25, exponent: 1e20,
    decimal: Number("9223372036854775807.0"), text: "9223372036854775807",
    nested: [null, true, 18446744073709551615n],
  };
  assert.deepEqual(result.value.rows[0]?.value, expected);
  assert.equal(result.value.matched, 1);
});

test("document GET and list preserve large integer values while metadata stays numeric", async () => {
  const client = clientFor('{"amount":9223372036854775807}');
  const result = await client.documents.get("demo", "orders", "row-1");
  assert.ok(result.ok);
  assert.deepEqual(result.value, { amount: 9223372036854775807n });
  const listed = await clientFor('{"documents":[{"id":"a","version":1,"value":{"nested":[-9223372036854775808]}}]}')
    .documents.list("demo", "orders");
  assert.ok(listed.ok);
  assert.deepEqual(listed.value.documents, [{ id: "a", version: 1, value: { nested: [-9223372036854775808n] } }]);
  assert.throws(() => client.documents.put("demo", "orders", "a", { amount: 1n } as never), /JSON/);
});

test("SQL rejects unsafe integers when the runtime does not provide original number sources", async () => {
  const nativeParse = JSON.parse;
  const parse = vi.spyOn(JSON, "parse").mockImplementation((text, reviver) => nativeParse(text,
    reviver === undefined ? undefined : function (this: unknown, key: string, value: unknown) {
      return reviver.call(this, key, value);
    }));
  try {
    const result = await clientFor('{"matched":1,"rows":[{"id":"a","value":9223372036854775807}]}')
      .sql.execute("demo", "SELECT * FROM orders");
    assert.ok(!result.ok);
    assert.equal(result.error.code, "invalid_response");
    assert.match(result.error.message, /reviver source support/);
    const document = await clientFor('{"amount":9223372036854775807}').documents.get("demo", "orders", "a");
    assert.ok(!document.ok);
    assert.equal(document.error.code, "invalid_response");
    const safe = await clientFor('{"matched":1,"rows":[{"id":"a","value":42}]}')
      .sql.execute("demo", "SELECT * FROM orders");
    assert.ok(safe.ok);
  } finally {
    parse.mockRestore();
  }
});

test("management uint64 fields preserve full range and reject out-of-range values", async () => {
  const request = async (raw: string) => createTerraScaleManagementClient({
    fetch: async () => new Response(raw, { headers: { "content-type": "application/json" } }),
  }).schemas.get("demo", "orders");
  const result = await request('{"generation":18446744073709551615,"schema_version":1,"migration":{"total":9007199254740993,"completed":2},"schema":{"version":1}}');
  assert.ok(result.ok);
  assert.deepEqual(result.value, {
    generation: 18446744073709551615n, schema_version: 1,
    migration: { total: 9007199254740993n, completed: 2 }, schema: { version: 1 },
  });
  for (const value of ["18446744073709551616", "-1", "1.5", '"1"']) {
    const rejected = await request(`{"generation":${value}}`);
    assert.ok(!rejected.ok);
    assert.equal(rejected.error.code, "invalid_response");
  }
});

test("index backfill remaining preserves unsafe uint64 values", async () => {
  const client = createTerraScaleManagementClient({
    fetch: async () => new Response(
      '{"indexes":[{"field":"email","state":"building","backfill_remaining":18446744073709551615},{"field":"name","state":"active","backfill_remaining":3}]}',
      { headers: { "content-type": "application/json" } },
    ),
  });
  const result = await client.collections.listIndexes("demo", "users");
  assert.ok(result.ok);
  assert.equal(result.value.indexes[0]?.backfill_remaining, 18446744073709551615n);
  assert.equal(result.value.indexes[1]?.backfill_remaining, 3);
});

test("database metadata preserves signed int64 timestamps and rejects overflow", async () => {
  const read = (timestamp: string) => createTerraScaleManagementClient({
    fetch: async () => new Response(`{"databases":[{"created_at":${timestamp},"name":"demo","deleted":false,"placement":"GRU","residency":"br-only","parent":null}]}`, {
      headers: { "content-type": "application/json" },
    }),
  }).databases.list();
  for (const timestamp of [-9223372036854775808n, 9223372036854775807n, -1, 0]) {
    const result = await read(String(timestamp));
    assert.ok(result.ok);
    assert.equal(result.value.databases[0]?.created_at, timestamp);
  }
  for (const timestamp of ["-9223372036854775809", "9223372036854775808", "18446744073709551615"]) {
    const result = await read(timestamp);
    assert.ok(!result.ok);
    assert.equal(result.error.code, "invalid_response");
  }
});

test("export lifecycle methods preserve uint64 values in statuses and list envelopes", async () => {
  const status = '{"job_id":"job-1","status":"Running","created_at_millis":18446744073709551615,"phase":"Documents","cancellation":"available","pages":9007199254740993,"records":18446744073709551615,"estimated_records":null,"progress_basis_points":50,"resume_cursor":"cursor","manifest":{"path":"manifest","length":9007199254740993,"checksum":"checksum"},"error":null}';
  const client = createTerraScaleManagementClient({
    fetch: async input => {
      const url = new URL(input instanceof Request ? input.url : String(input));
      return new Response(url.pathname.endsWith("/exports") ? `{"exports":[${status}],"next_cursor":null}` : status, {
        headers: { "content-type": "application/json" },
      });
    },
  });
  const listed = await client.exports.list("demo");
  assert.ok(listed.ok);
  const expected = listed.value.exports[0]!;
  assert.equal(expected.created_at_millis, 18446744073709551615n);
  assert.equal(expected.pages, 9007199254740993n);
  assert.equal(expected.records, 18446744073709551615n);
  assert.equal(expected.manifest?.length, 9007199254740993n);
  assert.equal(expected.estimated_records, null);
  assert.equal(expected.progress_basis_points, 50);
  const results = await Promise.all([
    client.exports.get("demo", "job-1"),
    client.exports.resume("demo", "job-1", { resume_cursor: "cursor" }),
    client.exports.cancel("demo", "job-1"),
  ]);
  for (const result of results) {
    assert.ok(result.ok);
    assert.deepEqual(result.value, expected);
  }
});
