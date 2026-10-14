import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { execFileSync } from "node:child_process";
import { readFile } from "node:fs/promises";
import path from "node:path";
import { v1Routes, shapeV1Routes } from "../src/v1/routes.js";
import { openapiDigest } from "./ci/contract-digest.mjs";
import { currentSourcePin, verifyCurrentSourceRoutes } from "./terrabase-current-source.mjs";

export const releasePin = JSON.parse(await readFile(new URL("./ci/terrabase-release.json", import.meta.url), "utf8"));
export const contractDirectory = new URL("../contracts/terrabase-csharp/", import.meta.url);
/** @param {Buffer | string} bytes @returns {string} */
export const digest = bytes => createHash("sha256").update(bytes).digest("hex");
export const historicalOperations = new Set(["getDocument", "putDocument", "createDocument", "patchDocument", "deleteDocument", "commitTransaction", "queryDocuments", "createCollection", "getCollection", "listCollections", "dropCollection", "readShape"]);
const endpointSources = await Promise.all(Object.keys(releasePin.inputs).filter(name => name.endsWith("Endpoint.cs")).map(name => readFile(new URL(name, contractDirectory), "utf8")));
const scopeSource = await readFile(new URL("IApiKeyStore.cs", contractDirectory), "utf8");

/**
 * @typedef {{ content?: Record<string, unknown> }} ProducerResponse
 * @typedef {{ operationId: string; requestBody?: { content: Record<string, unknown> }; responses: Record<string, ProducerResponse> }} ProducerOperation
 * @typedef {{ paths: Record<string, Record<string, ProducerOperation>>; components: { schemas: Record<string, unknown> } }} ProducerOpenApi
 */
/** @returns {Promise<ProducerOpenApi>} */
export async function producerContract() {
  const bytes = await readFile(new URL("openapi.json", contractDirectory));
  assert.equal(digest(bytes), releasePin.openapiBytesSha256, "Vendored C# OpenAPI digest mismatch.");
  assert.equal(openapiDigest(bytes), releasePin.openapiSha256, "Normalized vendored C# OpenAPI differs from the pinned runtime contract.");
  for (const [name, input] of Object.entries(releasePin.inputs)) {
    const source = await readFile(new URL(name, contractDirectory));
    assert.equal(digest(source), input.sha256, `${name} differs from the pinned C# producer source.`);
    if (process.env.TERRABASE_REPO) {
      const repoSource = execFileSync("git", ["-C", process.env.TERRABASE_REPO, "show", `${releasePin.revision}:${input.sourcePath}`]);
      assert.equal(digest(repoSource), input.sha256, `${name} differs from the pinned C# producer source.`);
    }
  }
  const runtime = process.env.TERRABASE_V1_OPENAPI;
  if (runtime !== undefined) {
    assert.ok(path.isAbsolute(runtime), "TERRABASE_V1_OPENAPI must be absolute.");
    assert.equal(openapiDigest(await readFile(runtime)), releasePin.openapiSha256, "Runtime C# OpenAPI differs from the pinned release.");
  } else assert.notEqual(process.env.TB_V1_REQUIRED, "1", "Real runtime OpenAPI is required for qualification.");
  return JSON.parse(bytes.toString("utf8"));
}

/** @param {readonly import("../src/v1/routes.js").V1Route[]} routes @param {ProducerOpenApi} spec */
export function verifyProducerRoutes(routes, spec) {
  verifyCurrentSourceRoutes(routes.filter(route => route.operation !== "readShape"));
  for (const route of routes.filter(route => historicalOperations.has(route.operation))) {
    // SDK placeholder names are internal; the producer names the database parameter "database".
    const template = route.pathTemplate.replaceAll("{db}", "{database}");
    const operation = spec.paths[template]?.[route.method.toLowerCase()];
    assert.ok(operation, `${route.operation}: C# producer has no ${route.method} ${template}.`);
    const source = endpointSources.find(text => text.includes(`Name("${operation.operationId}")`));
    assert.ok(source, `${route.operation}: producer endpoint security evidence is missing.`);
    const policy = source.match(/RequireAuthorization\(ApiKeyScopes\.(\w+)\)/)?.[1];
    assert.ok(policy, `${route.operation}: producer has no named API-key policy.`);
    const scope = scopeSource.match(new RegExp(`const string ${policy} = "([^"]+)"`))?.[1];
    assert.equal(route.permission, scope, `${route.operation}: SDK scope differs from the producer authorization policy.`);
    for (const media of route.requestMediaTypes)
      assert.ok(operation.requestBody?.content[media], `${route.operation}: producer does not accept ${media}.`);
    for (const status of route.successStatuses) {
      // The producer documents/implements 307 in GetShapeEndpoint.cs but omits it from OpenAPI.
      if (route.operation === "readShape" && status === 307) {
        assert.ok(source.includes("preserveMethod: true"), "Producer no longer documents/implements the 307 shape redirect.");
        continue;
      }
      /** @type {ProducerResponse | undefined} */
      const response = operation.responses[String(status)];
      assert.ok(response, `${route.operation}: producer does not declare success ${status}.`);
      assert.ok(response.content?.[route.responseMediaType], `${route.operation}: producer does not return ${route.responseMediaType}.`);
    }
    assert.equal(route.idempotencyKey, "none", `${route.operation}: this C# producer rejects Idempotency-Key.`);
  }
}

/** Proposed SDK shape protocol evidence; this is not runtime/OpenAPI qualification. */
export async function shapeProtocolContract() {
  const directory = new URL("../contracts/shape-v1/", import.meta.url);
  const pin = JSON.parse(await readFile(new URL("pin.json", directory), "utf8"));
  for (const [name, input] of Object.entries(pin.inputs))
    assert.equal(digest(await readFile(new URL(name, directory))), input.sha256, `${name}: shape protocol digest mismatch.`);
  const spec = await readFile(new URL("shape-reads.md", directory), "utf8");
  const base = "/v1/projects/{project}/databases/{database}/collections/{collection}/shape";
  assert.ok(spec.includes(`GET ${base}?where=&handle=&offset=&live=&cursor=`));
  assert.ok(spec.includes(`GET ${base}/keys?handle=&epoch=&key_id=`));
  assert.ok(spec.includes("documents:read"));
  assert.deepEqual(shapeV1Routes.map(route => ({operation: route.operation, method: route.method, path: route.pathTemplate,
    scope: route.permission, statuses: route.successStatuses, media: route.responseMediaType, idempotency: route.idempotencyKey})), [
    { operation: "readFreeShape", method: "GET", path: base, scope: "documents:read", statuses: [200, 307], media: "application/json", idempotency: "none" },
    { operation: "getShapeKey", method: "GET", path: `${base}/keys`, scope: "documents:read", statuses: [200], media: "application/json", idempotency: "none" },
  ], "Free-read named routes drifted from pinned protocol.");
  return { ...pin, routes: shapeV1Routes, generatedOpenApi: "Unavailable: proposed protocol only; producer qualification skipped." };
}

export async function generateV1Manifest() {
  const spec = await producerContract();
  verifyProducerRoutes(v1Routes, spec);
  return {
    manifestVersion: 4,
    shapeProtocol: await shapeProtocolContract(),
    contractVersion: "terrabase-csharp-v1",
    contractSource: releasePin,
    currentSource: currentSourcePin,
    currentRouteEvidence: verifyCurrentSourceRoutes(v1Routes.filter(route => route.operation !== "readShape")),
    currentGeneratedOpenApi: "Unavailable: current route parity is source-bound, not runtime qualification.",
    routeCount: v1Routes.length,
    routes: v1Routes.map(route => {
      const template = route.pathTemplate.replaceAll("{db}", "{database}");
      return {
        producer: "terrabase-csharp", client: "TerraBaseV1Client",
        operation: route.operation, method: route.method, pathTemplate: route.pathTemplate,
        permission: route.permission, requestMediaTypes: route.requestMediaTypes,
        responseMediaType: route.responseMediaType, successStatuses: route.successStatuses,
        idempotencyKey: route.idempotencyKey, replaySafe: route.replaySafe,
        evidence: route.operation === "readShape" ? "historical-release-only; absent from current source" : "current-handler-source",
        historicalProducerOperation: historicalOperations.has(route.operation) ? spec.paths[template]?.[route.method.toLowerCase()] : undefined,
      };
    }),
    // Keep the producer's complete schema bytes, including its current nullability/requiredness limitations.
    schemas: spec.components.schemas,
    securityEvidence: Object.keys(releasePin.inputs).filter(name => name.endsWith(".cs")),
    openapiLimitations: "Producer OpenAPI omits some conditional statuses and the 307 shape redirect; pinned source and real integration tests supplement it. Historical bootstrap routes remain source-bound. Current sources cover 25 SDK routes; shape is retained only for historical compatibility. Current generated OpenAPI and immutable runtime qualification remain required separately.",
  };
}
