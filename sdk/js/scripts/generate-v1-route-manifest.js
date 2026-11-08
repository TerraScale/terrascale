#!/usr/bin/env node
import assert from "node:assert/strict";
import { v1Route } from "../src/v1/routes.js";
import { readFile, writeFile } from "node:fs/promises";
import { generateV1Manifest } from "./terrabase-contract.mjs";
import { fileStorageRoutes } from "../src/v1/file-storage-routes.js";

const filePin = JSON.parse(await readFile(new URL("../contracts/file-storage/pin.json", import.meta.url), "utf8"));
/** @type {{ contract: string; revision: string; evidence: string; operations: { operation: import("../src/v1/routes.js").V1Operation | "forkDatabase" | "createScopedKey"; request: { path: string; method: string }; response: { status: number } }[] }} */
const agent = JSON.parse(await readFile(new URL("../contracts/terrabase/agent/lifecycle.json", import.meta.url), "utf8"));
const agentManifest = {
  contract: agent.contract, revision: agent.revision, evidence: agent.evidence,
  routes: agent.operations.map(({ operation, request, response }) => {
    const pathTemplate = request.path.replace(/\/projects\/[^/]+/u, "/projects/{project}")
      .replace(/\/databases\/[^/]+/u, "/databases/{database}")
      .replace(/\/restores\/[^/]+/u, "/restores/{operation}");
    const route = operation === "createScopedKey" ? undefined : v1Route(operation === "forkDatabase"
      ? pathTemplate.endsWith("/branches") ? "branchDatabase" : "restorePointInTime" : operation);
    if (route !== undefined) {
      assert.equal(route.pathTemplate, pathTemplate);
      assert.equal(route.method, request.method);
      assert.ok(route.successStatuses.includes(response.status));
      assert.equal(route.permission, request.method === "GET" ? "databases:read" : "databases:write");
    }
    return { operation, method: request.method, pathTemplate, successStatus: response.status,
      authorization: operation === "createScopedKey" ? "Basic keyId:key" : "Bearer project access token" };
  }),
};
for (const [name, manifest] of [
  ["sdk-v1-route-manifest.json", await generateV1Manifest()],
  ["sdk-agent-route-manifest.json", agentManifest],
  ["sdk-file-storage-route-manifest.json", { producerRevision: filePin.revision, routes: fileStorageRoutes }],
]) {
  const target = new URL(`../${name}`, import.meta.url);
  const output = `${JSON.stringify(manifest, null, 2)}\n`;
  if (process.argv.includes("--check")) {
    if (await readFile(target, "utf8") !== output)
      throw new Error(`${name} differs from the producer and client; run npm run generate:v1-route-manifest.`);
  } else await writeFile(target, output);
}
console.log("Route manifests match the pinned C# producer, agent contract fixtures and SDK routes.");
