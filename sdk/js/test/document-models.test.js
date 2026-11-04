import { afterEach, expect, test } from "vitest";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import path from "node:path";
import { execFileSync } from "node:child_process";
import ts from "typescript";
import { generateCollectionModels, generateDocumentModels } from "../src/codegen.js";
import { fixture, json, prefix } from "./helpers/v1-http.js";
const schema = { type: "object", required: ["name", "nullable"], additionalProperties: false, properties: {
  name: { type: "string", minLength: 1 }, nullable: { type: ["string", "null"] },
  generated: { type: "string", readOnly: true }, count: { type: "integer" },
  profile: { type: "object", required: ["name"], properties: { name: { type: "string" } }, additionalProperties: false },
  openNullable: { type: ["object", "null"], properties: { x: { type: "string" } } },
  items: { type: "array", items: { type: "object", properties: { value: { type: "string" } }, required: ["value"], additionalProperties: false } },
} };
/** @type {string[]} */
const directories = [];
afterEach(async () => { for (const dir of directories.splice(0)) await rm(dir, { recursive: true, force: true }); });
test("declared models compile with SDK writes and reject invalid create/merge-patch bodies", async () => {
  const dir = await mkdtemp(path.join(process.cwd(), ".model-test-")); directories.push(dir);
  const output = await generateDocumentModels(schema, "Order");
  await writeFile(path.join(dir, "models.ts"), output + await generateDocumentModels({ type: "object", properties: { name: { type: "string" }, generated: { type: "string", readOnly: true } } }, "Open") + await generateDocumentModels({ type: "object", additionalProperties: { type: "string" } }, "Map") + await generateDocumentModels({ type: "object", additionalProperties: false }, "Empty"));
  const app = path.join(dir, "app.ts");
  await writeFile(app, `import { createTerraBaseClient, ExactDecimal } from "@terrascale/sdk";
import type { OrderDocument, OrderCreate, OrderPatch } from "./models.js";
import type { OpenCreate, OpenPatch, MapPatch, EmptyCreate } from "./models.js";
const collection = createTerraBaseClient({ project: "p", baseUrl: "https://example.invalid", token: "fixture" }).database("app").collection("orders");
const create: OrderCreate = { name: "ok", nullable: null, count: 9007199254740993n, openNullable: null };
void collection.create(create);
void collection.put("id", create);
const patch: OrderPatch = { profile: {}, count: null, items: [{ value: "ok" }] };
void collection.patch("id", { merge: patch });
const open: OpenCreate = { name: "ok", extra: [1, true] };
void collection.create(open);
const openPatch: OpenPatch = { name: null };
void collection.patch("id", { merge: openPatch });
export const map: MapPatch = { deleted: null, changed: "value" };
// @ts-expect-error closed empty objects exclude primitives
export const empty: EmptyCreate = 42;
// @ts-expect-error open objects still forbid readOnly fields
export const readOnlyOpen: OpenCreate = { generated: "no" };
export const document: OrderDocument = { name: "ok", nullable: null, count: new ExactDecimal("9007199254740993"), openNullable: null };
// @ts-expect-error missing required field
export const missing: OrderCreate = { nullable: null };
// @ts-expect-error absent differs from null
export const nullName: OrderCreate = { name: null, nullable: null };
// @ts-expect-error exact optional properties reject explicit undefined
export const absent: OrderCreate = { name: "ok", nullable: null, generated: undefined };
// @ts-expect-error readOnly field is excluded from create
export const generated: OrderCreate = { name: "ok", nullable: null, generated: "no" };
// @ts-expect-error required nullable fields cannot be deleted by merge patch
export const deleteRequired: OrderPatch = { nullable: null };
// @ts-expect-error arrays replace complete items
export const incompleteItem: OrderPatch = { items: [{}] };
// @ts-expect-error readOnly field is excluded from patches
export const overwrite: OrderPatch = { generated: "no" };
`);
  const program = ts.createProgram([app], { strict: true, exactOptionalPropertyTypes: true, noUnusedLocals: true, allowJs: true,
    baseUrl: process.cwd(), paths: { "@terrascale/sdk": ["./src/index.js"] },
    target: ts.ScriptTarget.ESNext, module: ts.ModuleKind.NodeNext, skipLibCheck: true, noEmit: true });
  expect(ts.getPreEmitDiagnostics(program).map(d => ts.flattenDiagnosticMessageText(d.messageText, "\n"))).toEqual([]);
  expect(await generateDocumentModels(schema, "Order")).toBe(output);
  expect(await generateDocumentModels({ ...schema, properties: { ...schema.properties, extra: { type: "boolean" } } }, "Order")).not.toBe(output);
});
test("live-source workflow uses the named authorized declared-schema operation", async () => {
  const { client, requests } = await fixture((_r, response) => json(response, { schema, dialect: "https://json-schema.org/draft/2020-12/schema" }));
  const collection = client.database("app").collection("orders");
  expect(await generateCollectionModels(collection, "Order")).toBe(await generateDocumentModels(schema, "Order"));
  expect(requests.map(r => [r.method, r.url.pathname, r.headers.authorization])).toEqual([
    ["GET", `${prefix}/databases/app/collections/orders/schema`, "Bearer fixture.payload.signature"],
  ]);
});
test("no declaration never falls back to inferred samples", async () => {
  const { client } = await fixture((_r, response) => json(response, { schema: null }));
  await expect(generateCollectionModels(client.database("app").collection("orders"), "Order")).rejects.toThrow("no declared JSON Schema");
});
test.each(["$ref", "oneOf", "$defs", "pattern", "x-generated", "if"])("unsupported %s reports a JSON Pointer", async keyword => {
  await expect(generateDocumentModels({ type: "object", properties: { "a/b": { type: "string", [keyword]: true } } }, "Order")).rejects.toThrow(`/properties/a~1b/${keyword}`);
});
test("required readOnly is diagnosed because the server does not populate defaults", async () => {
  await expect(generateDocumentModels({ type: "object", properties: { id: { type: "string", readOnly: true, default: "x" } }, required: ["id"] }, "Order")).rejects.toThrow("required readOnly");
});
test.each([
  { type: "object", properties: null }, { type: "object", required: null },
  { type: "object", properties: { x: { type: "string", minLength: -1 } } },
  { type: "object", properties: { x: { type: "integer", enum: [1.5] } } },
  { type: "object", properties: { x: { type: "string", enum: ["a"], const: "b" } } },
  { type: "object", properties: { name: { type: "string" } }, additionalProperties: { type: "number" } },
  { type: "object", properties: { x: { type: ["array", "null"], const: null, items: true } } },
  { type: "object", properties: { x: { type: ["object", "null"], enum: [null] } } },
  { type: "object", properties: { x: { type: "string", properties: null } } },
  { type: "object", properties: { x: { type: "array", items: true, required: null } } },
  { type: "object", items: 42 },
])("unsupported or invalid schema never becomes a weaker model: %j", async input => {
  await expect(generateDocumentModels(input, "Order")).rejects.toThrow("Collection model /");
});
test("CLI reports unsupported exact literals without overwriting output", async () => {
  const dir = await mkdtemp(path.join(process.cwd(), ".model-test-")); directories.push(dir);
  const source = path.join(dir, "schema.json"), output = path.join(dir, "models.ts");
  await writeFile(output, "keep");
  await writeFile(source, '{"type":"object","properties":{"n":{"type":"number","const":1.00000000000000001}}}');
  expect(() => execFileSync(process.execPath, ["src/codegen-cli.js", source, "Order", output], { stdio: "pipe" })).toThrow();
  expect(await readFile(output, "utf8")).toBe("keep");
});
