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
test("named shape-key operation uses no-store and enforces handle/epoch/key binding", async () => {
  const { client, requests } = await fixture((_r, response) => json(response, { handle, epoch: "100", key_id: entity, key: "A".repeat(43) }));
  const key = await client.database("app").collection("users").shapeKey({ handle, epoch: "100", keyId: entity });
  expect(key.key).toEqual(new Uint8Array(32));
  expect(requests[0]?.url.pathname).toBe(`${base}/keys`);
  expect(Object.fromEntries(requests[0]?.url.searchParams ?? [])).toEqual({ handle, epoch: "100", key_id: entity });
});
