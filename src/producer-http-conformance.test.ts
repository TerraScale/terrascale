import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { readFile } from "node:fs/promises";
import path from "node:path";
import { test } from "vitest";
import { capturePublicClientRoutes, exercisePublicClientRoutes } from "./client-route-capture.js";
import type { TerraScaleRouteName } from "./http.js";
import type { TerraScaleResult } from "./result.js";

// These are producer response bytes supplied to the transport codec. They do
// not stand in for a running product service or prove a live deployment.
test("public HTTP clients consume the pinned producer response and error corpus", async () => {
  const { resolveContractBundle } = await import(new URL("../scripts/openapi-contract-paths.ts", import.meta.url).href);
  const bundle = await resolveContractBundle();
  try {
    assert.ok(bundle.artifactDigests.has("conformance/http.json"));
    const specBytes = await readFile(path.join(bundle.root, "openapi.json"));
    const spec = JSON.parse(specBytes.toString());
    const corpus = JSON.parse(await readFile(path.join(bundle.root, "conformance/http.json"), "utf8"));
    assert.equal(corpus.openapiSha256, createHash("sha256").update(specBytes).digest("hex"));
    // The edge owns `regions.list` at the same public path where TerraBase
    // owns member status (`regions.regions`). The edge bundle currently
    // publishes OpenAPI without the TerraBase HTTP corpus, so its exact
    // catalog body is covered by regions-client tests rather than being
    // paired with the engine HealthResponse vector here.
    const captured = (await capturePublicClientRoutes()).filter(route => route.routeName !== "regions.list");
    const vectors = captured.map(route => {
      const operation = spec.paths[route.pathTemplate][route.method.toLowerCase()];
      const vector = corpus.operations.find((value: Vector) => value.operationId === operation.operationId) as Vector | undefined;
      assert.ok(vector, `Missing producer vector: ${route.routeName}`);
      assert.equal(vector.request.method, route.method);
      const schema = operation.requestBody?.content?.["application/json"]?.schema;
      const bytesSchema = operation.requestBody?.content?.["application/octet-stream"]?.schema;
      assert.equal(route.sourceContract.body.kind === "json", schema !== undefined, route.routeName);
      assert.equal(route.sourceContract.body.kind === "bytes", bytesSchema !== undefined, route.routeName);
      if (route.sourceContract.body.kind === "json") assertSchema(route.sourceContract.body.shape, schema, spec, route.routeName);
      const parameters = [...(spec.paths[route.pathTemplate].parameters ?? []), ...(operation.parameters ?? [])];
      for (const query of route.sourceContract.query) {
        assert.ok(parameters.some((parameter: any) => parameter.in === "query" && parameter.name === query.name), `${route.routeName}: undocumented query ${query.name}`);
      }
      for (const parameter of parameters.filter((parameter: any) => parameter.required && parameter.in === "header")) {
        assert.ok(route.sourceContract.headers.some(header => header.name.toLowerCase() === parameter.name.toLowerCase()), `${route.routeName}: missing header ${parameter.name}`);
      }
      for (const parameter of parameters.filter((parameter: any) => parameter.required && parameter.in === "query")) {
        assert.ok(route.sourceContract.query.some(query => query.name === parameter.name));
      }
      return { route, vector };
    });
    const scenarios = [
      ...Array.from({ length: Math.max(...vectors.map(({ vector }) => vector.responses.length)) }, (_, successIndex) => ({ successIndex, status: undefined })),
      ...[...new Set(vectors.flatMap(({ vector }) => vector.errors.map(error => error.status)))].map(status => ({ successIndex: undefined, status })),
    ];
    for (const { status, successIndex } of scenarios) {
      const responseFor = (vector: Vector) => successIndex === undefined ? vector.errors.find(error => error.status === status) : vector.responses[successIndex];
      const selected = vectors.filter(({ vector }) => responseFor(vector) !== undefined);
      let cursor = 0;
      let expected: WireResponse | undefined;
      await exercisePublicClientRoutes(async input => {
        const request = new Request(input);
        const entry = vectors.find(({ route }) => route.method === request.method && matchesPath(route.pathTemplate, new URL(request.url).pathname));
        assert.ok(entry, `${request.method} ${request.url}`);
        const vectorResponse = responseFor(entry.vector);
        assert.ok(vectorResponse, `Missing producer status ${status}: ${entry.route.routeName}`);
        expected = materializeResponse(entry.route.routeName, vectorResponse);
        return new Response(expected.body === undefined || expected.status === 204 ? null : expected.mediaType === "application/octet-stream" ? String(expected.body) : JSON.stringify(expected.body), {
          status: expected.status,
          headers: { ...(expected.mediaType ? { "content-type": expected.mediaType } : {}), ...expected.headers },
        });
      }, (routeName, raw) => {
        cursor++;
        const result = raw as TerraScaleResult<unknown>;
        assert.ok(expected);
        assert.equal(result.status, expected.status, routeName);
        for (const [name, value] of Object.entries(expected.headers ?? {})) assert.equal(result.headers?.get(name), value, `${routeName}: ${name}`);
        // Member health uses a typed HealthResponse body even when the
        // liveness verdict is HTTP 503; all SDKs expose that observation.
        const typedUnhealthyHealth = routeName === "health.health" && expected.status === 503;
        assert.equal(result.ok, status === undefined || typedUnhealthyHealth, routeName);
        if (result.ok) {
          const expectedValue = expected.status === 204 ? undefined
            : expected.mediaType === "application/octet-stream"
              ? { bytes: new TextEncoder().encode(String(expected.body)), length: Number(expected.headers?.["x-terrascale-artifact-length"] ?? "0"), checksum: expected.headers?.["x-terrascale-artifact-checksum"] }
              : expected.body;
          assert.deepEqual(result.value, expectedValue, routeName);
        }
        else {
          const body = expected.body as { error: { code: string; message: string; fix: string } };
          assert.equal(result.error.code, body.error.code, routeName);
          assert.equal(result.error.message, body.error.message, routeName);
          assert.equal(result.error.fix, body.error.fix, routeName);
        }
      }, selected.map(({ route }) => route.routeName));
      assert.equal(cursor, selected.length);
    }
  } finally { await bundle.cleanup(); }
});

type WireResponse = { status: number; body?: unknown; mediaType?: string; headers?: Record<string, string> };
type Vector = { operationId: string; request: { method: string }; response: WireResponse; responses: WireResponse[]; errors: WireResponse[] };

/**
 * Conformance vectors use `sample` for unconstrained strings.  Health's
 * region is an operational RegionCode, which the SDK deliberately validates
 * as an uppercase code. Materialize that generated placeholder to the
 * canonical fixture region before exercising the typed consumer; otherwise
 * this test would reject the vector's synthetic value instead of checking the
 * producer health status (including the updated `healthy: false` fixture).
 */
function materializeResponse(routeName: TerraScaleRouteName, response: WireResponse): WireResponse {
  if (routeName !== "health.health" || response.body === undefined || typeof response.body !== "object" || response.body === null || Array.isArray(response.body)) {
    return response;
  }
  const body = response.body as Record<string, unknown>;
  return body.region === "sample" ? { ...response, body: { ...body, region: "GRU" } } : response;
}

function matchesPath(template: string, pathname: string): boolean {
  const expected = template.split("/");
  const actual = pathname.split("/");
  return expected.length === actual.length && expected.every((segment, index) => segment.startsWith("{") || segment === actual[index]);
}

// Validate the JSON type, required fields, alternatives, and enum constraints
// exercised by the producer HTTP request schemas.
function assertSchema(value: unknown, schema: any, spec: any, label: string): void {
  if (schema.$ref) return assertSchema(value, schema.$ref.split("/").slice(1).reduce((node: any, key: string) => node[key], spec), spec, label);
  if (Array.isArray(schema.type)) return assertSchema(value, { anyOf: schema.type.map((type: string) => ({ ...schema, type })) }, spec, label);
  if (schema.allOf) for (const branch of schema.allOf) assertSchema(value, branch, spec, label);
  if (schema.oneOf || schema.anyOf) {
    assert.ok((schema.oneOf ?? schema.anyOf).some((branch: any) => {
      try { assertSchema(value, branch, spec, label); return true; } catch { return false; }
    }), `${label}: no matching schema alternative`);
    return;
  }
  if (value === null && schema.nullable) return;
  if (schema.enum) assert.ok(schema.enum.includes(value), label);
  if (typeof value === "number") {
    assert.ok(Number.isFinite(value), label);
    if (schema.minimum !== undefined) assert.ok(value >= schema.minimum, `${label}: below minimum`);
    if (schema.maximum !== undefined) assert.ok(value <= schema.maximum, `${label}: above maximum`);
  }
  if (typeof value === "string") {
    if (schema.minLength !== undefined) assert.ok([...value].length >= schema.minLength, label);
    if (schema.maxLength !== undefined) assert.ok([...value].length <= schema.maxLength, label);
    if (schema.pattern !== undefined) assert.match(value, new RegExp(schema.pattern, "u"), label);
  }
  if (schema.type === undefined) return;
  if (schema.type === "object") {
    assert.ok(typeof value === "object" && value !== null && !Array.isArray(value), label);
    const object = value as Record<string, unknown>;
    for (const key of schema.required ?? []) assert.ok(key in object, `${label}.${key} is required`);
    for (const [key, member] of Object.entries(object)) {
      if (schema.properties?.[key]) assertSchema(member, schema.properties[key], spec, `${label}.${key}`);
      else if (schema.additionalProperties === false) assert.fail(`${label}.${key} is undocumented`);
      else if (typeof schema.additionalProperties === "object") assertSchema(member, schema.additionalProperties, spec, `${label}.${key}`);
    }
  } else if (schema.type === "array") {
    assert.ok(Array.isArray(value), label);
    if (schema.minItems !== undefined) assert.ok(value.length >= schema.minItems, label);
    if (schema.maxItems !== undefined) assert.ok(value.length <= schema.maxItems, label);
    for (const member of value) assertSchema(member, schema.items, spec, label);
  } else if (schema.type === "integer") assert.ok(typeof value === "number" && Number.isSafeInteger(value), label);
  else if (schema.type === "null") assert.equal(value, null, label);
  else assert.equal(typeof value, schema.type, label);
}
