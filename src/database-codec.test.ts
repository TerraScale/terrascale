import { describe, expect, test } from "vitest";
import {
  decodeDatabasePoint, decodeDatabaseQueryPage, decodeDatabaseSubscription,
  decodeDatabaseValue, encodeDatabaseCommit, encodeDatabaseQuery,
  encodeDatabaseSubscription, encodeDatabaseValue, requireDatabaseU64,
  requireDatabaseUuid, decodeDatabaseStatus, parseDatabaseJson,
} from "./database-codec.js";
import type { DatabaseCommit, DatabasePoint, DatabaseValue } from "./database-types.js";

const tenant = "00000000-0000-0000-0000-000000000001";
const database = "00000000-0000-0000-0000-000000000002";
const collection = "00000000-0000-0000-0000-000000000003";
const document = "00000000-0000-0000-0000-000000000004";
const replica = "00000000-0000-0000-0000-000000000005";
const maxU64 = (1n << 64n) - 1n;
const schemaDigest = "01".repeat(32);
const token = "ab".repeat(32);
const scope = { tenant, database, shard: maxU64 };

function commit(): DatabaseCommit {
  return {
    transaction_id: { expires_at_unix_ms: maxU64, nonce: "02".repeat(16) },
    authority: { mode: "leaderless" },
    writes: [{ collection, document, schema_generation: maxU64, schema_digest: schemaDigest, condition: { kind: "absent" }, observed: [], value: { kind: "live", fields: [{ id: 1, value: { type: "i64", value: -(1n << 63n) } }] } }],
  };
}

function wirePoint(state: DatabasePoint["state"] = "value"): Record<string, unknown> {
  const event = { database, shard: maxU64.toString(), replica, incarnation: "03".repeat(16), sequence: maxU64.toString() };
  const live = { event, schema_generation: maxU64.toString(), schema_digest: schemaDigest, kind: "live", fields: [{ id: 1, value: { type: "i64", value: "9223372036854775807" } }] };
  return {
    version: 1, state, state_version: state === "value" || state === "deleted" ? token : null,
    context: state === "absent" ? [] : [{ replica, incarnation: event.incarnation, sequence: event.sequence }, ...(state === "conflict" ? [{ replica, incarnation: "04".repeat(16), sequence: "1" }] : [])],
    siblings: state === "absent" ? [] : state === "deleted" ? [{ event, schema_generation: maxU64.toString(), schema_digest: schemaDigest, kind: "tombstone" }] : state === "conflict" ? [live, { ...live, event: { ...event, sequence: "1", incarnation: "04".repeat(16) } }] : [live],
  };
}

describe("current database exact codec", () => {
  test("preserves i64 extremes, empty/zero bytes, missing fields, null and recursive numeric maps", () => {
    const value: DatabaseValue = { type: "map", fields: [
      { id: 1, value: { type: "null" } },
      { id: 3, value: { type: "bytes", value: new Uint8Array() } },
      { id: 4, value: { type: "bytes", value: new Uint8Array([0, 255, 1]) } },
      { id: 5, value: { type: "i64", value: -(1n << 63n) } },
      { id: 6, value: { type: "array", items: [{ type: "i64", value: (1n << 63n) - 1n }, { type: "utf8", value: "Olá 🌎" }, { type: "map", fields: [{ id: 99, value: { type: "bool", value: false } }] }] } },
    ] };
    const wire = encodeDatabaseValue(value);
    expect(decodeDatabaseValue(JSON.parse(JSON.stringify(wire)))).toEqual(value);
    expect(wire.fields).toHaveLength(5);
    expect(JSON.stringify(wire)).toContain('"value":"-9223372036854775808"');
    expect(JSON.stringify(wire)).toContain('"value":"AP8B"');
  });

  test.each(["-0", "01", "+1", "9223372036854775808", "-9223372036854775809", 9007199254740992])("rejects lossy/noncanonical i64 %s", value => {
    expect(() => decodeDatabaseValue({ type: "i64", value })).toThrow(TypeError);
  });

  test.each(["AQ==", "AR", "A", "a+", "a/", "a "])("rejects noncanonical bytes %s", value => {
    expect(() => decodeDatabaseValue({ type: "bytes", value })).toThrow(TypeError);
  });

  test("rejects unknown fields, unordered/duplicate IDs, unpaired surrogates and excessive nesting", () => {
    expect(() => decodeDatabaseValue({ type: "null", value: null })).toThrow(TypeError);
    expect(() => decodeDatabaseValue({ type: "map", fields: [{ id: 2, value: { type: "null" } }, { id: 2, value: { type: "null" } }] })).toThrow(TypeError);
    expect(() => encodeDatabaseValue({ type: "utf8", value: "\ud800" })).toThrow(TypeError);
    let value: DatabaseValue = { type: "null" };
    for (let depth = 0; depth < 16; depth++) value = { type: "array", items: [value] };
    expect(() => encodeDatabaseValue(value)).toThrow(TypeError);
    expect(() => encodeDatabaseValue({ type: "array", items: Array.from({ length: 1025 }, () => ({ type: "null" })) })).toThrow(TypeError);
  });

  test("canonical UUID and full-u64 scope do not reinterpret ULIDs, unsafe numbers or uppercase", () => {
    expect(requireDatabaseUuid(document)).toBe(document);
    expect(requireDatabaseU64(maxU64)).toBe(maxU64);
    expect(() => requireDatabaseUuid("01ARZ3NDEKTSV4RRFFQ69G5FAV")).toThrow(TypeError);
    expect(() => requireDatabaseUuid("ABCDEF01-0000-0000-0000-000000000001")).toThrow(TypeError);
    expect(() => requireDatabaseU64(Number(maxU64))).toThrow(TypeError);
    expect(() => requireDatabaseU64(maxU64 + 1n)).toThrow(TypeError);
  });

  test("array descendants are forbidden even when an intervening map separates them", () => {
    for (const value of [
      { type: "array", items: [{ type: "array", items: [] }] },
      { type: "array", items: [{ type: "map", fields: [{ id: 1, value: { type: "array", items: [] } }] }] },
    ] as const) {
      expect(() => encodeDatabaseValue(value)).toThrow(/descendant arrays/u);
      expect(() => decodeDatabaseValue(value)).toThrow(/descendant arrays/u);
    }
    const allowed = { type: "array", items: [{ type: "map", fields: [{ id: 1, value: { type: "null" } }] }] } as const;
    expect(decodeDatabaseValue(encodeDatabaseValue(allowed))).toEqual(allowed);
  });

  test("top-level values begin at depth one and share the document node budget", async () => {
    let value: DatabaseValue = { type: "null" };
    for (let level = 1; level < 16; level++) value = { type: "map", fields: [{ id: 1, value }] };
    expect(decodeDatabaseValue(encodeDatabaseValue(value))).toEqual(value);
    expect(() => encodeDatabaseValue({ type: "map", fields: [{ id: 1, value }] })).toThrow(/depth/u);
    const fields = Array.from({ length: 4 }, (_, index) => ({ id: index + 1, value: { type: "map" as const, fields: Array.from({ length: 1023 }, (_, child) => ({ id: child + 1, value: { type: "null" as const } })) } }));
    const request = commit();
    await expect(encodeDatabaseCommit(scope, { ...request, writes: [{ ...request.writes[0]!, value: { kind: "live", fields } }] })).resolves.toBeDefined();
    await expect(encodeDatabaseCommit(scope, { ...request, writes: [{ ...request.writes[0]!, value: { kind: "live", fields: [...fields, { id: 5, value: { type: "null" } }] } }] })).rejects.toThrow(/node/u);
  });

  test("the 1 MiB native document limit includes its header and every field directory entry", async () => {
    const request = commit();
    const maximumPayload = 1024 * 1024 - 98 - 13;
    for (const extra of [0, 1]) {
      const fields = [{ id: 1, value: { type: "utf8" as const, value: "a".repeat(maximumPayload + extra) } }];
      const encoding = encodeDatabaseCommit(scope, { ...request, writes: [{ ...request.writes[0]!, value: { kind: "live", fields } }] });
      const original = wirePoint();
      const originalSiblings = original.siblings as Record<string, unknown>[];
      const wire = { ...original, siblings: [{ ...originalSiblings[0], fields }] };
      if (extra === 0) {
        await expect(encoding).resolves.toBeDefined();
        expect(decodeDatabasePoint(wire).siblings).toHaveLength(1);
      } else {
        await expect(encoding).rejects.toThrow(/Encoded document/u);
        expect(() => decodeDatabasePoint(wire)).toThrow(/Encoded document/u);
      }
    }
    const fields = [1, 2].map(id => ({ id, value: { type: "utf8" as const, value: "a".repeat(524_288) } }));
    await expect(encodeDatabaseCommit(scope, { ...request, writes: [{ ...request.writes[0]!, value: { kind: "live", fields } }] })).rejects.toThrow(/Encoded document/u);
  });

  test("preserves every point state, causal context, and all conflict siblings", () => {
    for (const state of ["absent", "deleted", "value", "conflict"] as const) {
      const point = decodeDatabasePoint(wirePoint(state));
      expect(point.state).toBe(state);
      expect(point.state_version).toBe(state === "value" || state === "deleted" ? token : null);
      if (state === "conflict") expect(point.siblings).toHaveLength(2);
      if (state !== "absent") expect(point.context[0]?.sequence).toBe(maxU64);
    }
    expect(() => decodeDatabasePoint({ ...wirePoint("conflict"), state_version: token })).toThrow(TypeError);
    expect(() => decodeDatabasePoint({ ...wirePoint("value"), state_version: null })).toThrow(TypeError);
    expect(() => decodeDatabasePoint({ ...wirePoint("absent"), siblings: wirePoint("value").siblings })).toThrow(TypeError);
  });

  test("query/subscription cursors stay opaque and phases remain distinct", () => {
    const predicate = { op: "is_missing", path: [1, 7] } as const;
    const opaque = "b3BhcXVl";
    expect(JSON.parse(encodeDatabaseQuery({ predicate, limit: 128, cursor: opaque }))).toEqual({ version: 1, predicate, limit: 128, cursor: opaque });
    expect(() => encodeDatabaseQuery({ predicate, limit: 129 })).toThrow(TypeError);
    expect(() => decodeDatabaseQueryPage({ version: 1, rows: [{ document, point: wirePoint() }, { document, point: wirePoint() }] })).toThrow(TypeError);
    expect(JSON.parse(encodeDatabaseSubscription({ phase: "poll", predicate, poll_cursor: opaque }))).toEqual({ version: 1, phase: "poll", predicate, poll_cursor: opaque });
    expect(decodeDatabaseSubscription({ version: 1, phase: "poll", groups: [{ sequence: maxU64.toString(), effects: [{ document, point: null }] }], poll_cursor: opaque })).toEqual({ version: 1, phase: "poll", groups: [{ sequence: maxU64, effects: [{ document, point: null }] }], poll_cursor: opaque });
    expect(() => decodeDatabaseSubscription({ version: 1, phase: "snapshot", rows: [], next_snapshot_cursor: "AQ", poll_cursor: "AA" })).toThrow(TypeError);
  });

  test.each([
    { name: "one byte", cursor: "AQ" },
    { name: "two bytes", cursor: "AAA" },
    { name: "three bytes", cursor: "AAAA" },
    { name: "URL-safe alphabet", cursor: "-_8" },
    { name: "maximum length", cursor: "A".repeat(1024) },
  ])("preserves canonical opaque cursors in requests and responses: $name", ({ cursor }) => {
    const predicate = { op: "is_missing", path: [1] } as const;
    expect(JSON.parse(encodeDatabaseQuery({ predicate, limit: 1, cursor }))).toEqual({ version: 1, predicate, limit: 1, cursor });
    expect(decodeDatabaseQueryPage({ version: 1, rows: [], next_cursor: cursor })).toEqual({ version: 1, rows: [], next_cursor: cursor });
    expect(JSON.parse(encodeDatabaseSubscription({ phase: "snapshot", predicate, limit: 1, snapshot_cursor: cursor }))).toEqual({ version: 1, phase: "snapshot", predicate, limit: 1, snapshot_cursor: cursor });
    expect(JSON.parse(encodeDatabaseSubscription({ phase: "poll", predicate, poll_cursor: cursor }))).toEqual({ version: 1, phase: "poll", predicate, poll_cursor: cursor });
    expect(decodeDatabaseSubscription({ version: 1, phase: "snapshot", rows: [], next_snapshot_cursor: cursor })).toEqual({ version: 1, phase: "snapshot", rows: [], next_snapshot_cursor: cursor });
    expect(decodeDatabaseSubscription({ version: 1, phase: "snapshot", rows: [], poll_cursor: cursor })).toEqual({ version: 1, phase: "snapshot", rows: [], poll_cursor: cursor });
    expect(decodeDatabaseSubscription({ version: 1, phase: "poll", groups: [], poll_cursor: cursor })).toEqual({ version: 1, phase: "poll", groups: [], poll_cursor: cursor });
  });

  test.each([
    { name: "empty", cursor: "" },
    { name: "impossible length", cursor: "A" },
    { name: "nonzero four padding bits", cursor: "AB" },
    { name: "nonzero two padding bits", cursor: "AAB" },
    { name: "single padding character", cursor: "AQ=" },
    { name: "double padding characters", cursor: "AQ==" },
    { name: "leading whitespace", cursor: " AQ" },
    { name: "trailing whitespace", cursor: "AQ " },
    { name: "trailing newline", cursor: "AQ\n" },
    { name: "standard base64 plus", cursor: "AQ+" },
    { name: "standard base64 slash", cursor: "AQ/" },
    { name: "oversized", cursor: "A".repeat(1025) },
  ])("rejects noncanonical or oversized cursors in requests and responses: $name", ({ cursor }) => {
    const predicate = { op: "is_missing", path: [1] } as const;
    expect(() => encodeDatabaseQuery({ predicate, limit: 1, cursor })).toThrow(TypeError);
    expect(() => decodeDatabaseQueryPage({ version: 1, rows: [], next_cursor: cursor })).toThrow(TypeError);
    expect(() => encodeDatabaseSubscription({ phase: "snapshot", predicate, limit: 1, snapshot_cursor: cursor })).toThrow(TypeError);
    expect(() => encodeDatabaseSubscription({ phase: "poll", predicate, poll_cursor: cursor })).toThrow(TypeError);
    expect(() => decodeDatabaseSubscription({ version: 1, phase: "snapshot", rows: [], next_snapshot_cursor: cursor })).toThrow(TypeError);
    expect(() => decodeDatabaseSubscription({ version: 1, phase: "snapshot", rows: [], poll_cursor: cursor })).toThrow(TypeError);
    expect(() => decodeDatabaseSubscription({ version: 1, phase: "poll", groups: [], poll_cursor: cursor })).toThrow(TypeError);
  });

  test("initial requests and terminal query pages omit optional cursors", () => {
    const predicate = { op: "is_missing", path: [1] } as const;
    expect(JSON.parse(encodeDatabaseQuery({ predicate, limit: 1 }))).toEqual({ version: 1, predicate, limit: 1 });
    expect(JSON.parse(encodeDatabaseSubscription({ phase: "snapshot", predicate, limit: 1 }))).toEqual({ version: 1, phase: "snapshot", predicate, limit: 1 });
    expect(decodeDatabaseQueryPage({ version: 1, rows: [] })).toEqual({ version: 1, rows: [] });
    expect(() => decodeDatabaseQueryPage({ version: 1, rows: [], next_cursor: null })).toThrow(TypeError);
    expect(() => decodeDatabaseSubscription({ version: 1, phase: "snapshot", rows: [] })).toThrow(TypeError);
  });

  test("fingerprints exact logical effects independently of transaction identity", async () => {
    const request = commit();
    const encoded = await encodeDatabaseCommit(scope, request);
    // Independently encoded with Python uuid/struct from the frozen binary grammar.
    expect(encoded.effect_digest).toBe("80cc8d2dac2e807a4a841d15b100fe41b5d928be7dd0b4e8f980be1f4b75de96");
    const otherIdentity = await encodeDatabaseCommit(scope, { ...request, transaction_id: { expires_at_unix_ms: 1n, nonce: "09".repeat(16) } });
    expect(encoded.effect_digest).toMatch(/^[0-9a-f]{64}$/u);
    expect(otherIdentity.effect_digest).toBe(encoded.effect_digest);
    const changedScope = await encodeDatabaseCommit({ ...scope, shard: 0n }, request);
    expect(changedScope.effect_digest).not.toBe(encoded.effect_digest);
    const write = request.writes[0]!;
    const changedEffect = await encodeDatabaseCommit(scope, { ...request, writes: [{ ...write, value: { kind: "tombstone" } }] });
    expect(changedEffect.effect_digest).not.toBe(encoded.effect_digest);
    const wire = JSON.parse(encoded.body);
    expect(wire.writes[0].schema_generation).toBe("18446744073709551615");
    expect(wire.transaction_id.expires_at_unix_ms).toBe("18446744073709551615");
  });

  test("single-writer mutations cannot silently use unconditional leaderless semantics", async () => {
    const request = commit();
    await expect(encodeDatabaseCommit(scope, { ...request, authority: { mode: "single_writer", generation: maxU64, writer: { replica, incarnation: "05".repeat(16) } }, writes: [{ ...request.writes[0]!, condition: { kind: "none" } }] })).rejects.toThrow(TypeError);
  });

  test("status keeps rejected outcomes explicit and rejects fabricated terminal states", () => {
    const identity = { version: 1, transaction_id: { expires_at_unix_ms: "10", nonce: "01".repeat(16) }, effect_digest: "02".repeat(32), authority: { mode: "leaderless" } };
    expect(decodeDatabaseStatus({ ...identity, state: "rejected", outcome: { error: { code: "precondition_failed" } } }).state).toBe("rejected");
    expect(() => decodeDatabaseStatus({ ...identity, state: "aborted", outcome: null })).toThrow(TypeError);
    expect(() => decodeDatabaseStatus({ ...identity, state: "unknown", outcome: { error: { code: "precondition_failed" } } })).toThrow(TypeError);
  });

  test("bounded JSON rejects duplicate members, invalid UTF-8 and excessive syntax depth", () => {
    const bytes = (value: string): Uint8Array => new TextEncoder().encode(value);
    expect(parseDatabaseJson(bytes('{"a":[true,false,null,-1.2e+3,"escaped\\n"]}'))).toEqual({ a: [true, false, null, -1200, "escaped\n"] });
    expect(() => parseDatabaseJson(bytes('{"state":"absent","st\\u0061te":"value"}'))).toThrow(TypeError);
    expect(() => parseDatabaseJson(new Uint8Array([0xff]))).toThrow(TypeError);
    expect(() => parseDatabaseJson(bytes("[".repeat(66) + "0" + "]".repeat(66)))).toThrow(TypeError);
  });
});
