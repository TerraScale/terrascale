#!/usr/bin/env node

import { readFile, writeFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { contractOpenApiPath, pinnedContractVersion, resolveContractBundle, resolveEdgeContractBundle } from "./openapi-contract-paths.ts";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const manifestPath = path.join(repositoryRoot, "sdk-route-manifest.json");
const checkOnly = process.argv.includes("--check");
const HTTP_METHODS = new Set(["get", "post", "put", "patch", "delete", "head", "options", "trace"]);
const bundle = await resolveContractBundle();
const edgeBundle = await resolveEdgeContractBundle();
try {
  const specPath = await contractOpenApiPath(bundle);
  const edgeSpecPath = await contractOpenApiPath(edgeBundle);
  const spec = JSON.parse(await readFile(specPath, "utf8")) as OpenApiDocument;
  const edgeSpec = JSON.parse(await readFile(edgeSpecPath, "utf8")) as OpenApiDocument;
  const routes = [...Object.entries(spec.paths ?? {}).flatMap(([pathTemplate, pathItem]) =>
    Object.entries(pathItem).filter(([method]) => HTTP_METHODS.has(method)).map(([method, operation]) => {
      const operationValue = operation as OpenApiOperation;
      const operationId = operationValue.operationId;
      if (!operationId) throw new Error(`contracts: OpenAPI operation ${method.toUpperCase()} ${pathTemplate} has no operationId.`);
      return {
        base: "api",
        producer: "terrabase",
        client: clientName(pathTemplate),
        operation: operationName(operationId),
        operationId,
        routeName: `${clientName(pathTemplate)}.${operationName(operationId)}`,
        method: method.toUpperCase(),
        pathTemplate,
        requestType: requestType(operationValue.requestBody),
        responseType: responseType(operationValue.responses),
      };
    }),
  ), ...Object.entries(edgeSpec.paths ?? {}).flatMap(([pathTemplate, pathItem]) =>
    Object.entries(pathItem).filter(([method]) => HTTP_METHODS.has(method)).map(([method, operation]) => {
      const operationValue = operation as OpenApiOperation;
      const operationId = operationValue.operationId;
      if (!operationId) throw new Error(`contracts: OpenAPI operation ${method.toUpperCase()} ${pathTemplate} has no operationId.`);
      return {
        base: "api",
        producer: "edge",
        client: clientName(pathTemplate),
        operation: operationName(operationId),
        operationId,
        routeName: `${clientName(pathTemplate)}.${operationName(operationId)}`,
        method: method.toUpperCase(),
        pathTemplate,
        requestType: requestType(operationValue.requestBody),
        responseType: responseType(operationValue.responses),
      };
    }),
  )].sort((left, right) => `${left.pathTemplate} ${left.method} ${left.producer}`.localeCompare(`${right.pathTemplate} ${right.method} ${right.producer}`));
  const manifest = {
    manifestVersion: 2,
    contractVersion: pinnedContractVersion,
    source: {
      repository: "terrabase+ts-proxy",
      bundle: "contracts/v1",
      openapi: "openapi.json",
    },
    routeCount: routes.length,
    routes,
  };
  const json = `${JSON.stringify(manifest, null, 2)}\n`;
  if (checkOnly) {
    if (await readFile(manifestPath, "utf8") !== json) throw new Error("sdk-route-manifest.json is out of date. Run npm run generate:route-manifest.");
    console.log(`sdk-route-manifest.json is current (${routes.length} routes from terrabase ${pinnedContractVersion}).`);
  } else {
    await writeFile(manifestPath, json);
    console.log(`Wrote sdk-route-manifest.json with ${routes.length} routes from terrabase ${pinnedContractVersion}.`);
  }
} finally {
  await edgeBundle.cleanup();
  await bundle.cleanup();
}

type OpenApiDocument = { readonly paths?: Record<string, Record<string, unknown>> };
type OpenApiOperation = { readonly operationId?: string; readonly requestBody?: OpenApiSchemaRef; readonly responses?: Record<string, OpenApiResponse> };
type OpenApiSchemaRef = { readonly $ref?: string; readonly content?: Record<string, { readonly schema?: OpenApiSchemaRef }> };
type OpenApiResponse = { readonly content?: Record<string, { readonly schema?: OpenApiSchemaRef }> };

function operationName(value: string): string {
  return camelCase(value.split(".").at(-1) ?? value);
}

function schemaName(value: OpenApiSchemaRef | undefined): string | undefined {
  const reference = value && "$ref" in value && typeof value.$ref === "string" ? value.$ref : undefined;
  return reference?.split("/").at(-1);
}
function requestType(body: OpenApiSchemaRef | undefined): string { return body === undefined ? "void" : schemaName(body.content?.["application/json"]?.schema) ?? "json"; }
function responseType(responses: Record<string, OpenApiResponse> | undefined): string {
  const successful = Object.entries(responses ?? {}).filter(([status]) => /^2(?:\d\d|xx)$/iu.test(status)).sort(([left], [right]) => left.localeCompare(right));
  if (successful.length === 0) return "never";
  const types = successful.map(([, response]) => {
    if (response.content?.["application/octet-stream"] !== undefined) return "Uint8Array";
    const content = response.content?.["application/json"] ?? Object.values(response.content ?? {})[0];
    return schemaName(content?.schema) ?? (content?.schema === undefined ? "void" : "json");
  });
  return [...new Set(types)].join(" | ");
}
function camelCase(value: string): string { return value.replace(/[-_ ]+(.)/gu, (_, character: string) => character.toUpperCase()); }
function clientName(pathTemplate: string): string {
  if (pathTemplate.startsWith("/api/v1/reactive/")) return "reactive";
  if (pathTemplate === "/api/v1/health") return "health";
  if (pathTemplate === "/api/v1/regions") return "regions";
  return "databases";
}
