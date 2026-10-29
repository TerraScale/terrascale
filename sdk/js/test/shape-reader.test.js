import { readFileSync } from "node:fs";
import { describe, expect, test } from "vitest";
import { V1Transport } from "../src/v1/transport.js";
import { readShapePage, decodeFreeShapePage, decryptSealedShapePage } from "../src/v1/shape-reader.js";
import { shapeVectors, vectors, skippedShapeVectorIds, shapeVectorDigest } from "./support/shape-vectors.js";
import { shapeProtocolContract } from "../scripts/terrabase-contract.mjs";
import { requireShapeOffset } from "../src/v1/shapes.js";
import { v1Filter } from "../src/v1/filter.js";
/** @type {Record<string, string>} */
const params = { database: "demo", collection: "notes" };
/** @param {string} id */
function vector(id) {
  const value = vectors("http_vectors").find(x => x.id === id);
  if (value === undefined) throw new Error(`Unknown vector ${id}`);
  return value;
}
/** @param {any} row @returns {Response} */
function reply(row) {
  const v = row.response;
  return new Response(v.body_utf8 ?? (v.body == null ? null : JSON.stringify(v.body)), { status: v.status, headers: v.headers });
}
/** @param {(url: URL, init: RequestInit) => Response | Promise<Response>} fetch */
function transport(fetch) {
  return new V1Transport({ project: shapeVectors.fixture.scope.project_id, token: "fixture.payload.signature", baseUrl: "https://origin.example.invalid",
    fetch: (url, init) => Promise.resolve(fetch(new URL(String(url)), init ?? {})) });
}
const handle = shapeVectors.fixture.handle;

test.each(vectors("offset_vectors"))("offset grammar matches upstream: $value", row => {
  if (row.valid) expect(requireShapeOffset(row.value)).toBe(row.value);
  else expect(() => requireShapeOffset(row.value)).toThrow();
});

test.each(vectors("cursor_vectors").filter(row => row.id !== "opaque-r8-proof"))("live bucket passthrough matches upstream: $id", async row => {
  const response = vector("live-timeout").response;
  let calls = 0;
  const t = transport(() => {
    calls++;
    return new Response(response.body_utf8, { headers: { ...response.headers, "TB-Shape-Cursor": row.expected_cursor } });
  });
  const result = readShapePage(t, params, { handle, offset: "4", live: true, cursor: row.input_cursor }, undefined);
  if (row.expected_error !== undefined) {
    await expect(result).rejects.toThrow(/below Int64.MaxValue/u);
    expect(calls).toBe(0);
  } else {
    expect((await result).cursor).toBe(row.expected_cursor);
    expect(calls).toBe(1);
  }
});

test("shape fixtures are source-bound and explicitly defer presigned-manifest", async () => {
  const source = JSON.parse(readFileSync(new URL("./fixtures/shape-vectors.source.json", import.meta.url), "utf8"));
  expect(shapeVectorDigest()).toBe(source.sha256);
  expect(source.commit).toBe("0d2582d1d6cc73972b49f6b6089b6f9ed0848da9");
  expect(skippedShapeVectorIds).toEqual(new Set(["presigned-manifest"]));
  expect(vectors("http_vectors").some(x => x.id === "presigned-manifest")).toBe(false);
  expect((await shapeProtocolContract()).status).toContain("proposed");
});

describe("origin free shape reads", () => {
  test("307 bootstrap follows only the authorized route and preserves filter", async () => {
    let calls = 0;
    const t = transport((url, init) => {
      expect(new Headers(init.headers).get("authorization")).toBe("Bearer fixture.payload.signature");
      expect(init.redirect).toBe("manual");
      calls++;
      if (calls === 1) { expect(url.searchParams.get("offset")).toBe("-1"); return reply(vector("bootstrap")); }
      expect(url.searchParams.get("offset")).toBe("snap.2.0");
      expect(url.searchParams.get("handle")).toBe(handle);
      return reply(vector("snapshot-first-page"));
    });
    const page = await readShapePage(t, params, {}, undefined);
    expect(page.cursorResume).toBe(vector("snapshot-first-page").response.body.cursor_resume);
    expect(calls).toBe(2);
  });
  test.each(["https://evil.invalid/shape?handle=h1-f3171a2230109328&offset=snap.2.0", "?handle=h1-f3171a2230109328&offset=snap.2.0&where=%7B%7D", "?handle=h1-f3171a2230109328&offset=snap.2.0&offset=2"])("rejects untrusted redirect %s", async location => {
    const t = transport(() => new Response(null, { status: 307, headers: { location } }));
    await expect(readShapePage(t, params, {}, undefined)).rejects.toThrow();
  });
  test.each(["proven-cursor-resume-query", "large-resume-token-uses-header"])("passes opaque resume verbatim: %s", async id => {
    const row = vector(id);
    const token = id === "proven-cursor-resume-query"
      ? vectors("cursor_vectors").find(row => row.id === "opaque-r8-proof").token
      : row.request.headers["TB-Shape-Resume"];
    if (id === "proven-cursor-resume-query") expect(token).toBe(row.request.query.cursor);
    let calls = 0;
    const t = transport((url, init) => {
      if (++calls === 1) {
        expect(url.searchParams.get("offset")).toBe("-1");
        expect(url.searchParams.has("handle")).toBe(false);
        if (id === "large-resume-token-uses-header") {
          expect(new Headers(init.headers).get("tb-shape-resume")).toBe(token);
          expect(url.searchParams.has("cursor")).toBe(false);
        } else expect(url.searchParams.get("cursor")).toBe(token);
        return reply(row);
      }
      expect(new Headers(init.headers).has("tb-shape-resume")).toBe(false);
      expect(url.searchParams.has("cursor")).toBe(false);
      const offset = url.searchParams.get("offset");
      return Response.json({ ...vector("live-timeout").response.body, handle: url.searchParams.get("handle"), offset, next_offset: offset, covered_offset: offset });
    });
    await readShapePage(t, params, { offset: "-1", cursor: token }, undefined);
    expect(calls).toBe(2);
  });
  test("checks live cursor advance without parsing resume tokens", async () => {
    const row = vector("live-timeout");
    const t = transport(() => reply(row));
    const page = await readShapePage(t, params, { ...row.request.query, live: true }, undefined);
    expect(page.upToDate).toBe(true);
    await expect(readShapePage(t, params, { ...row.request.query, live: true, cursor: page.cursor }, undefined)).rejects.toThrow(/cursor advance/u);
  });
  test("accepts the maximum response bucket but never sends the forbidden maximum input", async () => {
    const row = vector("live-timeout");
    let calls = 0;
    const t = transport(() => {
      calls++;
      return new Response(row.response.body_utf8, { headers: { ...row.response.headers, "TB-Shape-Cursor": "9223372036854775807" } });
    });
    const page = await readShapePage(t, params, { ...row.request.query, live: true, cursor: "9223372036854775806" }, undefined);
    expect(page.cursor).toBe("9223372036854775807");
    await expect(readShapePage(t, params, { ...row.request.query, live: true, cursor: page.cursor }, undefined)).rejects.toThrow(/below Int64.MaxValue/u);
    expect(calls).toBe(1);
  });
  test("rejects a malformed response before applying any operation", () => {
    const body = structuredClone(vector("closed-commit-upsert-delete").response.body);
    body.changes.push(body.changes[0]);
    expect(() => decodeFreeShapePage(body, new Headers())).toThrow(/commit order/u);
  });
  test("bootstrap rejects a redirect past the first snapshot page", async () => {
    const t = transport(() => new Response(null, { status: 307, headers: { location: `?handle=${handle}&offset=snap.2.MA` } }));
    await expect(readShapePage(t, params, {}, undefined)).rejects.toThrow(/redirect offset/u);
  });
  test("rejects unsorted operations, backwards snapshot pages, and a stalled closed chunk", () => {
    const body = structuredClone(vector("closed-commit-upsert-delete").response.body);
    body.changes[0].ops.reverse();
    expect(() => decodeFreeShapePage(body, new Headers())).toThrow(/document order/u);
    const snapshot = structuredClone(vector("snapshot-first-page").response.body);
    snapshot.offset = "snap.2.Mg";
    snapshot.next_offset = "snap.2.MQ";
    expect(() => decodeFreeShapePage(snapshot, new Headers())).toThrow(/snapshot progression/u);
    body.changes = []; body.next_offset = body.offset; body.covered_offset = body.offset;
    expect(() => decodeFreeShapePage(body, new Headers())).toThrow(/log coverage/u);
  });
  test("rejects oversized wire filters before a request", async () => {
    let calls = 0;
    const t = transport(() => { calls++; throw new Error("unexpected request"); });
    const where = v1Filter.and(...Array.from({ length: 20 }, (_, index) => v1Filter.eq(`/field${index}`, "é".repeat(400))));
    await expect(readShapePage(t, params, { where }, undefined)).rejects.toThrow(/8 KiB/u);
    expect(calls).toBe(0);
  });
  test("origin JSON rejects a leading UTF-8 BOM", async () => {
    const row = vector("closed-commit-upsert-delete");
    const t = transport(() => new Response(`\ufeff${row.response.body_utf8}`, { headers: row.response.headers }));
    await expect(readShapePage(t, params, row.request.query, undefined)).rejects.toThrow();
  });
  test("a live request behind the head accepts a closed chunk with a live bucket", async () => {
    const row = vector("closed-commit-upsert-delete");
    const t = transport(() => new Response(row.response.body_utf8, { headers: { ...row.response.headers, "TB-Shape-Cursor": "52" } }));
    const page = await readShapePage(t, params, { ...row.request.query, live: true, cursor: "51" }, undefined);
    expect(page.upToDate).toBe(false);
    expect(page.cursor).toBe("52");
  });
});

describe("authenticated sealed object reads", () => {
  test("fetches the exact historical epoch/key id without HTTP or past-epoch key cache reuse", async () => {
    const object = shapeVectors.tbs1_vectors[0];
    const position = { handle: object.handle, offset: object.request_offset, epoch: object.epoch, keyId: object.key_id };
    const bytes = Uint8Array.from(Buffer.from(object.tbs1_hex, "hex"));
    let calls = 0;
    const t = transport((url, init) => {
      expect(url.pathname.endsWith("/shape/keys")).toBe(true);
      expect(Object.fromEntries(url.searchParams)).toEqual({ handle: position.handle, epoch: position.epoch, key_id: position.keyId });
      expect(init.cache).toBe("no-store");
      calls++;
      return reply(vector("epoch-key"));
    });
    const page = await decryptSealedShapePage(t, params, bytes, position, undefined);
    expect(page.nextOffset).toBe("4");
    await decryptSealedShapePage(t, params, bytes, position, undefined);
    expect(calls).toBe(2);
    const corrupted = bytes.slice(); corrupted[corrupted.length - 1] = (corrupted[corrupted.length - 1] ?? 0) ^ 1;
    await expect(decryptSealedShapePage(t, params, corrupted, position, undefined)).rejects.toThrow();
  });
  test("rejects an epoch key response for another binding", async () => {
    const v = shapeVectors.tbs1_vectors[0];
    const t = transport(() => Response.json({ ...vector("epoch-key").response.body, epoch: "101" }));
    await expect(decryptSealedShapePage(t, params, new Uint8Array(), { handle: v.handle, offset: v.request_offset, epoch: v.epoch, keyId: v.key_id }, undefined)).rejects.toThrow(/key binding/u);
  });
});
