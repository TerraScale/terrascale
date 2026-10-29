import { expect, test } from "vitest";
import { entity, fixture, json, prefix } from "./helpers/v1-http.js";
const handle = "h1-0000000000000001";
const base = `${prefix}/databases/app/collections/users/shape`;
const page = { shape_id: "a".repeat(32), handle, offset: "snap.2.0", next_offset: "2", up_to_date: false,
  covered_offset: "2", values_at: "2", cursor_resume: "opaque_resume", changes: [{ offset: "2", ops: [{ op: "upsert", id: "01ARZ3NDEKTSV4RRFFQ69G5FA3", version: "tbv1-1", document: { name: "A" } }] }] };
test("root shape reads current snapshot coverage after safe bootstrap and retains raw keys", async () => {
  const { client, requests } = await fixture((request, response) => {
    if (request.url.searchParams.get("offset") === "-1") { response.writeHead(307, { location: `${base}?handle=${handle}&offset=snap.2.0` }).end(); return; }
    json(response, page);
  });
  const result = await client.database("app").collection("users").shape();
  expect(result.coveredOffset).toBe("2");
  expect(result.cursorResume).toBe("opaque_resume");
  expect(result.changes[0]?.ops[0]?.id).toBe("01ARZ3NDEKTSV4RRFFQ69G5FA3");
  expect(requests).toHaveLength(2);
});
const item = { offset: "2", next_offset: "3", binding: "binding", object: "object.tbs1", url: "https://objects.example.invalid/object?signature=fixture",
  expires_at: "2026-10-22T10:10:00Z", epoch: "100", key_id: entity, sha256: "a".repeat(64), size: 123 };
test("shape manifest exposes bounded presigned objects without fetching them or forwarding authorization", async () => {
  const { client, requests } = await fixture((_r, response) => json(response, { handle, objects: [item] }));
  expect(await client.database("app").collection("users").shapeManifest({ handle, offset: "2" })).toEqual({ handle, objects: [item] });
  expect(requests).toHaveLength(1);
  expect(requests[0]?.url.pathname).toBe(`${base}/manifest`);
  expect(Object.fromEntries(requests[0]?.url.searchParams ?? [])).toEqual({ handle, offset: "2" });
});
test("named shape-key operation uses no-store and enforces handle/epoch/key binding", async () => {
  const { client, requests } = await fixture((_r, response) => json(response, { handle, epoch: "100", key_id: entity, key: "A".repeat(43) }));
  const key = await client.database("app").collection("users").shapeKey({ handle, epoch: "100", keyId: entity });
  expect(key.key).toEqual(new Uint8Array(32));
  expect(requests[0]?.url.pathname).toBe(`${base}/keys`);
  expect(Object.fromEntries(requests[0]?.url.searchParams ?? [])).toEqual({ handle, epoch: "100", key_id: entity });
});
test.each([400, 401, 403, 409, 429, 503])("manifest errors %i retain producer details", async status => {
  const { client, requests } = await fixture((_r, response) => json(response, { error: { code: "must_refetch", message: "Restart", handle } }, status));
  await expect(client.database("app").collection("users").shapeManifest({ handle, offset: "2" })).rejects.toMatchObject({ status, code: "must_refetch", handle });
  expect(requests).toHaveLength(1);
});
test.each([{ offset: "3" }, { url: "http://unsafe.invalid" }, { key_id: "prefixed-key" }])("rejects malformed manifest object %j", async patch => {
  const { client } = await fixture((_r, response) => json(response, { handle, objects: [{ ...item, ...patch }] }));
  await expect(client.database("app").collection("users").shapeManifest({ handle, offset: "2" })).rejects.toThrow(TypeError);
});
test.each(['"cached"', 'W/"cached", "other"', "*", ' \tW/"cached", "other"\t '])("conditional shape read forwards %s and retains 304 cache metadata", async ifNoneMatch => {
  const { client, requests } = await fixture((_r, response) => response.writeHead(304, {
    etag: '"cached"', "tb-shape-handle": handle, "tb-shape-next-offset": "3", "tb-shape-up-to-date": "true", "tb-shape-cursor": "10",
  }).end());
  const error = await client.database("app").collection("users").shape({ handle, offset: "2" }, { ifNoneMatch }).catch(error => error);
  expect(error).toMatchObject({ status: 304, code: "not_modified", etag: "cached" });
  expect(error.headers.get("tb-shape-handle")).toBe(handle);
  expect(error.headers.get("tb-shape-next-offset")).toBe("3");
  expect(error.headers.get("tb-shape-up-to-date")).toBe("true");
  expect(error.headers.get("tb-shape-cursor")).toBe("10");
  expect(requests).toHaveLength(1);
  expect(requests[0]?.headers["if-none-match"]).toBe(ifNoneMatch.trim());
});
test("conditional shape bootstrap preserves the validator on its authorized continuation", async () => {
  const { client, requests } = await fixture((request, response) => {
    if (request.url.searchParams.get("offset") === "-1") { response.writeHead(307, { location: `${base}?handle=${handle}&offset=snap.2.0` }).end(); return; }
    json(response, page);
  });
  expect((await client.database("app").collection("users").shape({}, { ifNoneMatch: '"cached"' })).coveredOffset).toBe("2");
  expect(requests.map(request => request.headers["if-none-match"])).toEqual(['"cached"', '"cached"']);
});
test.each(["unquoted", '*, "tag"', '"tag"\r\nx-injected: value'])("invalid shape validator %j fails before dispatch", async ifNoneMatch => {
  const { client, requests } = await fixture((_r, response) => json(response, page));
  await expect(client.database("app").collection("users").shape({ handle, offset: "2" }, { ifNoneMatch })).rejects.toThrow(TypeError);
  expect(requests).toHaveLength(0);
});
