import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import { test } from "vitest";
import { fileURLToPath, pathToFileURL } from "node:url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const generator = import(pathToFileURL(path.join(root, "scripts", "openapi-contract-descriptor.mjs")).href) as Promise<{ generateOpenApiContractDescriptor(input: { manifestPath: string; specPath: string }): Promise<unknown> }>;
const capture = import(pathToFileURL(path.join(root, "dist", "client-route-capture.js")).href) as Promise<{ canonicalContractHeaders(headers: Headers): readonly { readonly name: string; readonly value: string }[] }>;

test("capture pins only allowlisted values and canonical single-valued Fetch headers", async () => {
  const headers = new Headers();
  headers.append("accept", "application/json");
  headers.append("accept", "text/plain");
  headers.set("x-credential", "never-commit");
  assert.deepEqual((await capture).canonicalContractHeaders(headers), [
    { name: "accept", value: "application/json, text/plain" },
    { name: "x-credential", value: "<redacted>" },
  ]);
});

test("descriptor pins operation identity, parameter contract, and complete JSON shapes", async () => {
  const directory = await mkdtemp(path.join(tmpdir(), "terrascale-openapi-descriptor-"));
  try {
    const manifestPath = path.join(directory, "manifest.json");
    const specPath = path.join(directory, "openapi.json");
    await writeFile(manifestPath, JSON.stringify({ routes: [{ base: "api", client: "widgets", method: "POST", operation: "create", pathTemplate: "/api/v1/widgets/{widgetId}", routeName: "widgets.create", requestType: "CreateWidget", responseType: "Widget", sourceContract: { body: { kind: "json", shape: { label: "Ada" } }, headers: [{ name: "content-type", value: "application/json" }], query: [] } }] }));
    const descriptor = await descriptorFor(specPath, { operationId: "CreateWidget", parameterName: "widgetId", required: true, type: "string", nullable: true, fields: { label: { type: "string", nullable: true } } });
    for (const mutation of [
      { operationId: "CreateWidgetV2" }, { parameterName: "id" }, { required: false }, { type: "integer" }, { nullable: false }, { fields: { label: { type: "string", nullable: true }, count: { type: "integer" } } },
    ]) assert.notDeepEqual(await descriptorFor(specPath, { operationId: "CreateWidget", parameterName: "widgetId", required: true, type: "string", nullable: true, fields: { label: { type: "string", nullable: true } }, ...mutation }), descriptor);
    await writeFile(specPath, JSON.stringify(spec({ operationId: "CreateWidget", parameterName: "widgetId", required: true, type: "string", nullable: true, fields: { label: { type: "string", nullable: true } } })));
    assert.deepEqual(await (await generator).generateOpenApiContractDescriptor({ manifestPath, specPath }), descriptor);
  } finally { await rm(directory, { recursive: true, force: true }); }
});

test("descriptor binds the exact OpenAPI and route-manifest bytes", async () => {
  const directory = await mkdtemp(path.join(tmpdir(), "terrascale-openapi-descriptor-digest-"));
  try {
    const manifestPath = path.join(directory, "manifest.json");
    const specPath = path.join(directory, "openapi.json");
    const manifest = { routes: [{ base: "api", client: "widgets", method: "POST", operation: "create", pathTemplate: "/api/v1/widgets/{widgetId}", routeName: "widgets.create", requestType: "CreateWidget", responseType: "Widget", sourceContract: { body: { kind: "json", shape: { label: "Ada" } }, headers: [{ name: "content-type", value: "application/json" }], query: [] } }] };
    const openApi = spec({ operationId: "CreateWidget", parameterName: "widgetId", required: true, type: "string", nullable: false, fields: { label: { type: "string" } } });
    await writeFile(manifestPath, JSON.stringify(manifest));
    await writeFile(specPath, JSON.stringify(openApi));

    const first = await (await generator).generateOpenApiContractDescriptor({ manifestPath, specPath }) as ContractDescriptor;
    assert.equal(first.descriptorVersion, 2);
    assert.equal(first.sources.openApiSha256, await fileSha256(specPath));
    assert.equal(first.sources.routeManifestSha256, await fileSha256(manifestPath));

    await writeFile(specPath, `${JSON.stringify(openApi, null, 2)}\n`);
    const reformattedSpec = await (await generator).generateOpenApiContractDescriptor({ manifestPath, specPath }) as ContractDescriptor;
    assert.notEqual(reformattedSpec.sources.openApiSha256, first.sources.openApiSha256);
    assert.equal(reformattedSpec.sources.routeManifestSha256, first.sources.routeManifestSha256);

    await writeFile(manifestPath, `${JSON.stringify(manifest)}\n`);
    const reformattedManifest = await (await generator).generateOpenApiContractDescriptor({ manifestPath, specPath }) as ContractDescriptor;
    assert.notEqual(reformattedManifest.sources.routeManifestSha256, first.sources.routeManifestSha256);
  } finally { await rm(directory, { recursive: true, force: true }); }
});

test("descriptor fails closed for stale source body metadata and preserves reference identity", async () => {
  const directory = await mkdtemp(path.join(tmpdir(), "terrascale-openapi-descriptor-"));
  try {
    const manifestPath = path.join(directory, "manifest.json");
    const specPath = path.join(directory, "openapi.json");
    const route = { base: "api", client: "widgets", method: "POST", operation: "create", pathTemplate: "/api/v1/widgets", routeName: "widgets.create", requestType: "void", responseType: "never", sourceContract: { body: { kind: "json", shape: { name: "Ada" } }, headers: [{ name: "content-type", value: "application/json" }, { name: "x-confirm", value: "true" }], query: [{ name: "limit", value: "50" }, { name: "tag", value: "one" }, { name: "tag", value: "two" }] } };
    await writeFile(manifestPath, JSON.stringify({ routes: [route] }));
    const openApi = { openapi: "3.1.0", paths: { "/api/v1/widgets": { post: {
      operationId: "Create",
      parameters: ["path", "query", "header", "cookie"].map((inLocation, index) => index === 1 ? { $ref: "#/components/parameters/Query" } : ({ in: inLocation, name: `p${index}`, required: inLocation === "path", schema: { type: "string", minLength: index + 1 } })),
      requestBody: { required: true, content: { "application/json": { schema: { $ref: "#/components/schemas/First" } } } },
      responses: { 200: { $ref: "#/components/responses/Ok" }, 201: { content: { "application/json": { schema: { $ref: "#/components/schemas/Second" } } } } },
    } } }, components: { parameters: { Query: { in: "query", name: "p1", required: false, schema: { type: "string", minLength: 2 } } }, responses: { Ok: { description: "ok", content: { "application/json": { schema: { $ref: "#/components/schemas/First" } } } }, Other: { description: "ok", content: { "application/json": { schema: { $ref: "#/components/schemas/First" } } } } }, schemas: { First: { type: "object", properties: { name: { type: "string" }, self: { $ref: "#/components/schemas/First" } } }, Second: { type: "object", properties: { name: { type: "string" } } } } } };
    await writeFile(specPath, JSON.stringify(openApi));
    await assert.rejects((await generator).generateOpenApiContractDescriptor({ manifestPath, specPath }), /declares void but captures a request body/u);
    const updatedRoute = { ...route, requestType: "json", responseType: "Widget" };
    await writeFile(manifestPath, JSON.stringify({ routes: [updatedRoute] }));
    const first = await (await generator).generateOpenApiContractDescriptor({ manifestPath, specPath });
    const rangeSpec = JSON.parse(await (await import("node:fs/promises")).readFile(specPath, "utf8"));
    rangeSpec.paths["/api/v1/widgets"].post.responses = { "2XX": { description: "any success" } };
    await writeFile(specPath, JSON.stringify(rangeSpec));
    await writeFile(manifestPath, JSON.stringify({ routes: [{ ...updatedRoute, responseType: "never" }] }));
    await assert.rejects((await generator).generateOpenApiContractDescriptor({ manifestPath, specPath }), /response never mismatch/u);
    await writeFile(specPath, JSON.stringify(openApi));
    await writeFile(manifestPath, JSON.stringify({ routes: [updatedRoute] }));
    const changedValues = { ...updatedRoute, sourceContract: { ...updatedRoute.sourceContract, headers: [{ name: "content-type", value: "application/json" }, { name: "x-confirm", value: "false" }], query: [{ name: "limit", value: "51" }, { name: "tag", value: "one" }, { name: "tag", value: "two" }] } };
    await writeFile(manifestPath, JSON.stringify({ routes: [changedValues] }));
    assert.notDeepEqual(await (await generator).generateOpenApiContractDescriptor({ manifestPath, specPath }), first);
    await writeFile(manifestPath, JSON.stringify({ routes: [updatedRoute] }));
    const swapped = JSON.parse(await (await import("node:fs/promises")).readFile(specPath, "utf8"));
    assert.match(JSON.stringify(first), /"\$ref":"#\/components\/(?:parameters\/Query|responses\/Ok|schemas\/First)"/u);
    swapped.paths["/api/v1/widgets"].post.responses[200].$ref = "#/components/responses/Other";
    await writeFile(specPath, JSON.stringify(swapped));
    assert.notDeepEqual(await (await generator).generateOpenApiContractDescriptor({ manifestPath, specPath }), first);
    swapped.paths["/api/v1/widgets"].post.responses[200] = { $ref: "#/components/responses/Ok", description: "alternate" };
    swapped.components.schemas.First.properties.name = { $ref: "#/components/schemas/Text", maxLength: 4 };
    swapped.components.schemas.Text = { type: "string" };
    await writeFile(specPath, JSON.stringify(swapped));
    const nested = await (await generator).generateOpenApiContractDescriptor({ manifestPath, specPath });
    swapped.components.schemas.First.properties.name = { $ref: "#/components/schemas/OtherText", maxLength: 5 };
    swapped.components.schemas.OtherText = { type: "string" };
    await writeFile(specPath, JSON.stringify(swapped));
    assert.notDeepEqual(await (await generator).generateOpenApiContractDescriptor({ manifestPath, specPath }), nested);
  } finally { await rm(directory, { recursive: true, force: true }); }
});

async function descriptorFor(specPath: string, input: Input): Promise<unknown> {
  await writeFile(specPath, JSON.stringify(spec(input)));
  const manifestPath = path.join(path.dirname(specPath), "manifest.json");
  return (await generator).generateOpenApiContractDescriptor({ manifestPath, specPath });
}
type Input = { operationId: string; parameterName: string; required: boolean; type: string; nullable: boolean; fields: Record<string, { type: string; nullable?: boolean }> };
type ContractDescriptor = {
  readonly descriptorVersion: number;
  readonly sources: {
    readonly openApiSha256: string;
    readonly routeManifestSha256: string;
  };
};

async function fileSha256(filePath: string): Promise<string> {
  return createHash("sha256").update(await readFile(filePath)).digest("hex");
}

function spec(input: Input) {
  return { openapi: "3.1.0", paths: { "/api/v1/widgets/{widgetId}": { post: {
    operationId: input.operationId,
    parameters: [{ in: "path", name: input.parameterName, required: input.required, schema: { type: input.type, minLength: 3 } }],
    requestBody: { required: true, content: { "application/json": { schema: { type: "object", properties: input.fields, required: ["label"] } } } },
    responses: { 200: { content: { "application/json": { schema: { type: "object", nullable: input.nullable, properties: input.fields, required: ["label"] } } } } },
  } } } };
}
