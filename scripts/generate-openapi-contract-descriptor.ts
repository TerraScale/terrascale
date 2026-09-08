#!/usr/bin/env node

import { readFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { generateOpenApiContractDescriptor, writeOpenApiContractDescriptor } from "./openapi-contract-descriptor.ts";
import { contractOpenApiPath, resolveContractBundle, resolveEdgeContractBundle } from "./openapi-contract-paths.ts";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const outputPath = path.join(root, "sdk-openapi-contract-descriptor.json");
const manifestPath = path.join(root, "sdk-route-manifest.json");
const check = process.argv.includes("--check");
const bundle = await resolveContractBundle();
const edgeBundle = await resolveEdgeContractBundle();
const specPath = await contractOpenApiPath(bundle);
const edgeSpecPath = await contractOpenApiPath(edgeBundle);
const descriptor = await generateOpenApiContractDescriptor({ manifestPath, specPath, specPaths: { edge: edgeSpecPath, terrabase: specPath } });
const json = `${JSON.stringify(descriptor, null, 2)}\n`;

if (check) {
  if (await readFile(outputPath, "utf8") !== json) throw new Error("sdk-openapi-contract-descriptor.json is out of date. Run npm run generate:openapi-contract-descriptor.");
  console.log(`OpenAPI contract descriptor is current (${descriptor.routeCount} routes).`);
} else {
  await writeOpenApiContractDescriptor({ manifestPath, outputPath, specPath, specPaths: { edge: edgeSpecPath, terrabase: specPath } });
  console.log(`Wrote sdk-openapi-contract-descriptor.json with ${descriptor.routeCount} routes.`);
}
await bundle?.cleanup();
await edgeBundle?.cleanup();
