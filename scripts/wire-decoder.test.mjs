import { strict as assert } from "node:assert";
import { test } from "node:test";
import { canonicalEnvelope, canonicalMessage } from "./run-snippets.mjs";

const envelope = (body) => Uint8Array.from([0x97, 1, 0, 0, 4, ...uint64(0n), ...uint64(0n), ...body]);
const uint64 = (value) => {
  const bytes = new Uint8Array(9);
  bytes[0] = 0xcf;
  new DataView(bytes.buffer).setBigUint64(1, value);
  return [...bytes];
};

test("reactive revision offsets preserve all 64 bits", () => {
  const revision = 9007199254740993n;
  // Delta: subscription, generation, from/to revision, operations, digest, checkpoint.
  const message = canonicalMessage(canonicalEnvelope(envelope([
    0x97, 0xa1, 0x76, ...uint64(1n), ...uint64(revision), ...uint64(revision + 1n),
    0x90, 0xa0, 0xc4, 0,
  ])));
  assert.equal(message.from_revision, revision);
  assert.equal(message.to_revision, revision + 1n);
  const url = new URL("https://example.test/log");
  url.searchParams.set("offset", String(message.to_revision));
  assert.equal(url.searchParams.get("offset"), "9007199254740994");
});

test("small 64-bit encoded values retain bigint representation", () => {
  assert.equal(canonicalEnvelope(envelope([0x91, ...uint64(42n)])).body[0], 42n);
});

test("malformed container lengths and excessive nesting fail within input bounds", () => {
  for (const body of [
    [0xdd, 0xff, 0xff, 0xff, 0xff],
    [0x91, 0xdf, 0xff, 0xff, 0xff, 0xff],
    [...Array(65).fill(0x91), 0xc0],
  ]) {
    assert.throws(() => canonicalEnvelope(envelope(body)), /truncated MessagePack|nesting limit|maps are forbidden/);
  }
});

test("maps are rejected by the indexed protocol", () => {
  assert.throws(() => canonicalEnvelope(envelope([
    0x91, 0x82, 0xa1, 0x61, 1, 0xa1, 0x61, 2,
  ])), /MessagePack maps are forbidden/);
});


test("correlation identifiers preserve the unsigned 64-bit protocol range", () => {
  const maximum = 0xffffffffffffffffn;
  const decoded = canonicalEnvelope(Uint8Array.from([
    0x97, 1, 0, 0, 6, ...uint64(maximum), ...uint64(maximum), 0x90,
  ]));
  assert.equal(decoded.correlation, maximum);
  assert.equal(decoded.causation, maximum);
  assert.throws(() => canonicalEnvelope(Uint8Array.from([
    0x97, 1, 0, 0, 6, 0xff, 0, 0x90,
  ])), /expected protocol/);
});

// Small independent wire encoder for the remote-runner request/response test.
function encode(value) {
  if (Number.isInteger(value) && value >= 0 && value < 128) return [value];
  if (typeof value === "bigint") return uint64(value);
  if (typeof value === "string") {
    const bytes = [...new TextEncoder().encode(value)];
    return bytes.length < 32 ? [0xa0 | bytes.length, ...bytes] : [0xd9, bytes.length, ...bytes];
  }
  if (value instanceof Uint8Array) return [0xc4, value.length, ...value];
  if (Array.isArray(value)) return [0x90 | value.length, ...value.flatMap(encode)];
  throw new Error("unsupported fixture value");
}

test("remote runner attaches the configured database and follows the server view identity", async (t) => {
  const { createRemoteSandbox } = await import("./run-snippets.mjs");
  const settings = {
    TERRASCALE_SANDBOX_URL: "https://sandbox.example.test",
    TERRASCALE_API_KEY: "test-api-token",
    TERRASCALE_DATABASE_ID: "demo",
  };
  const before = Object.fromEntries(Object.keys(settings).map((key) => [key, process.env[key]]));
  Object.assign(process.env, settings);
  t.after(() => {
    for (const [key, value] of Object.entries(before)) {
      if (value === undefined) delete process.env[key]; else process.env[key] = value;
    }
  });
  const view = "v-server-view";
  const calls = [];
  t.mock.method(globalThis, "fetch", async (input, options) => {
    const url = new URL(input);
    const headers = new Headers(options.headers);
    calls.push(url.pathname);
    if (url.pathname === "/api/v1/reactive/views") {
      assert.equal(JSON.parse(options.body).databaseId, "demo");
      assert.equal(headers.get("authorization"), "Bearer test-api-token");
      return Response.json(
        { view, generation: 1, capability: "test-capability", capability_header: "X-TerraScale-Capability", log: "/log", snapshot_chunks: ["/snapshot"] },
        { headers: { "x-terrascale-serving-region": "GRU" } },
      );
    }
    if (url.pathname === "/api/v1/regions") {
      return Response.json({ hosts: { GRU: "https://gru.example" } });
    }
    if (url.pathname === "/snapshot" || url.pathname === "/log") {
      assert.equal(headers.get("authorization"), "Bearer test-api-token");
      assert.equal(headers.get("x-terrascale-capability"), "test-capability");
      const body = url.pathname === "/snapshot"
        ? [3, [view, [1n, 1n, 0n, [], "0".repeat(64), new Uint8Array()]]]
        : [4, [view, 1n, 0n, 1n, [[1, "e-1", 0, []]], "0".repeat(64), new Uint8Array()]];
      return new Response(Uint8Array.from(encode([1, 0, 0, body[0], 0n, 0n, body[1]])));
    }
    assert.equal(url.pathname, "/api/v1/databases/demo/collections/events/documents/e-1");
    return Response.json({ revision: "1" });
  });
  const sandbox = createRemoteSandbox();
  const live = await sandbox.sync.subscribe({ databaseId: "demo", subscription: "client-name", query: "SELECT * FROM events", parameters: new Uint8Array() });
  try {
    await sandbox.client.documents.put("demo", "events", "e-1", { kind: "click" });
    assert.deepEqual(live.view.getVisibleItems().map((item) => item.identity), ["e-1"]);
    assert.deepEqual(calls, ["/api/v1/reactive/views", "/api/v1/regions", "/snapshot", "/api/v1/databases/demo/collections/events/documents/e-1", "/log"]);
  } finally { live.close(); }
});
