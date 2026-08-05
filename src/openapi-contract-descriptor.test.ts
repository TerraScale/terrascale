import assert from "node:assert/strict";
import { mkdtemp, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import { test } from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const generator = import(pathToFileURL(path.join(root, "scripts", "openapi-contract-descriptor.mjs")).href) as Promise<{ generateOpenApiContractDescriptor(input: { manifestPath: string; specPath: string }): Promise<unknown> }>;

test("descriptor pins operation identity, parameter contract, and complete JSON shapes", async () => {
  const directory = await mkdtemp(path.join(tmpdir(), "terrascale-openapi-descriptor-"));
  try {
    const manifestPath = path.join(directory, "manifest.json");
    const specPath = path.join(directory, "openapi.json");
    await writeFile(manifestPath, JSON.stringify({ routes: [{ base: "api", client: "widgets", method: "POST", operation: "create", pathTemplate: "/api/v1/widgets/{widgetId}", routeName: "widgets.create", requestType: "CreateWidget", responseType: "Widget" }] }));
    const descriptor = await descriptorFor(specPath, { operationId: "CreateWidget", parameterName: "widgetId", required: true, type: "string", nullable: true, fields: { label: { type: "string", nullable: true } } });
    for (const mutation of [
      { operationId: "CreateWidgetV2" }, { parameterName: "id" }, { required: false }, { type: "integer" }, { nullable: false }, { fields: { label: { type: "string", nullable: true }, count: { type: "integer" } } },
    ]) assert.notDeepEqual(await descriptorFor(specPath, { operationId: "CreateWidget", parameterName: "widgetId", required: true, type: "string", nullable: true, fields: { label: { type: "string", nullable: true } }, ...mutation }), descriptor);
    await writeFile(specPath, JSON.stringify(spec({ operationId: "CreateWidget", parameterName: "widgetId", required: true, type: "string", nullable: true, fields: { label: { type: "string", nullable: true } } })));
    assert.deepEqual(await (await generator).generateOpenApiContractDescriptor({ manifestPath, specPath }), descriptor);
  } finally { await rm(directory, { recursive: true, force: true }); }
});

async function descriptorFor(specPath: string, input: Input): Promise<unknown> {
  await writeFile(specPath, JSON.stringify(spec(input)));
  const manifestPath = path.join(path.dirname(specPath), "manifest.json");
  return (await generator).generateOpenApiContractDescriptor({ manifestPath, specPath });
}
type Input = { operationId: string; parameterName: string; required: boolean; type: string; nullable: boolean; fields: Record<string, { type: string; nullable?: boolean }> };
function spec(input: Input) {
  return { openapi: "3.1.0", paths: { "/api/v1/widgets/{widgetId}": { post: {
    operationId: input.operationId,
    parameters: [{ in: "path", name: input.parameterName, required: input.required, schema: { type: input.type, minLength: 3 } }],
    requestBody: { required: true, content: { "application/json": { schema: { type: "object", properties: input.fields, required: ["label"] } } } },
    responses: { 200: { content: { "application/json": { schema: { type: "object", nullable: input.nullable, properties: input.fields, required: ["label"] } } } } },
  } } } };
}
