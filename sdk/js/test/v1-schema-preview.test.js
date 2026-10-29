import { createHash } from "node:crypto";
import { readFile } from "node:fs/promises";
import { expect, test } from "vitest";
import { fixture, json } from "./helpers/v1-http.js";
import { v1Route } from "../src/v1/routes.js";

const preview = { schema_hash: "a".repeat(64), frontier: "b".repeat(64), examined: 2, valid: 1, invalid: 1,
  complete: false, next_cursor: "opaque+cursor", diagnostics: [{ id_hash: "c".repeat(64), path_hash: "d".repeat(64), keyword: "type" }] };

test("schema preview repeats raw schema bytes and preserves bounded continuation and redacted diagnostics", async () => {
  const { client, requests } = await fixture((_r, response) => json(response, preview));
  const collection = client.database("app").collection("users");
  const schema = { type: "object", required: ["email"] };
  const page = await collection.previewSchema(schema);
  expect(page).toEqual({ schemaHash: preview.schema_hash, frontier: preview.frontier, examined: 2, valid: 1, invalid: 1,
    complete: false, nextCursor: "opaque+cursor", diagnostics: [{ idHash: "c".repeat(64), pathHash: "d".repeat(64), keyword: "type" }] });
  await collection.previewSchema(schema, { cursor: /** @type {string} */ (page.nextCursor) });
  expect(requests.map(r => r.method)).toEqual(["POST", "POST"]);
  expect(requests[0]?.body).toBe(JSON.stringify(schema));
  expect(requests[1]?.body).toBe(requests[0]?.body);
  expect(requests[1]?.url.searchParams.get("cursor")).toBe("opaque+cursor");
  expect(requests[0]?.url.pathname).toMatch(/\/collections\/users\/schema\/preview$/u);
  expect(requests[0]?.headers["content-type"]).toBe("application/json");
  expect(requests[0]?.headers["idempotency-key"]).toBeUndefined();
  await expect(collection.previewSchema(schema, { cursor: "x".repeat(2049) })).rejects.toThrow(TypeError);
  await expect(collection.previewSchema(/** @type {any} */ (null))).rejects.toThrow(TypeError);
  expect(requests).toHaveLength(2);
});

test.each([401, 403, 409, 503])("schema preview preserves %i errors without replay", async status => {
  const { client, requests } = await fixture((_r, response) => json(response, { error: { code: "preview_stale", message: "Restart preview" } }, status));
  await expect(client.database("app").collection("users").previewSchema(true)).rejects.toMatchObject({ status, code: "preview_stale" });
  expect(requests).toHaveLength(1);
});

test.each([{ examined: 101 }, { valid: 2 }, { schema_hash: "invalid" }, { next_cursor: null }, { diagnostics: Array(17).fill(preview.diagnostics[0]) }])(
  "schema preview rejects malformed response %j", async changes => {
    const { client } = await fixture((_r, response) => json(response, { ...preview, ...changes }));
    await expect(client.database("app").collection("users").previewSchema(false)).rejects.toThrow(TypeError);
  },
);

test("schema preview route and DTO match the pinned served OpenAPI", async () => {
  const directory = new URL("../contracts/schema-preview/", import.meta.url);
  const bytes = await readFile(new URL("openapi.json", directory));
  const pin = JSON.parse(await readFile(new URL("pin.json", directory), "utf8"));
  expect(createHash("sha256").update(bytes).digest("hex")).toBe(pin.selectedOpenApiSha256);
  const spec = JSON.parse(bytes.toString("utf8"));
  const route = v1Route("previewCollectionSchema");
  const operation = spec.paths[route.pathTemplate].post;
  expect(operation.operationId).toBe(route.operation);
  expect(operation.security).toEqual([{ TsAuth: [] }]);
  expect(operation["x-required-scopes"]).toEqual([route.permission, .../** @type {readonly string[]} */ (route.additionalPermissions)]);
  expect(Object.keys(operation.requestBody.content)).toEqual(route.requestMediaTypes);
  expect(operation.parameters.find(/** @param {{name: string}} p */ p => p.name === "cursor")).toMatchObject({ in: "query", schema: { maxLength: 2048 } });
  expect(Object.keys(operation.responses[200].content)).toEqual([route.responseMediaType]);
  expect(spec.components.schemas.CollectionSchemaPreviewBody.required).toEqual([
    "schema_hash", "frontier", "examined", "valid", "invalid", "complete", "next_cursor", "diagnostics",
  ]);
});
