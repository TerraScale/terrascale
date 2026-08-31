#!/usr/bin/env node

import { readFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { generateOpenApiContractDescriptor } from "./openapi-contract-descriptor.mjs";
import { canonicalDashboardOpenApiSpec } from "./openapi-contract-paths.mjs";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const specPath = process.argv[2] ?? canonicalDashboardOpenApiSpec;
const expectedPath = path.join(root, "sdk-openapi-contract-descriptor.json");
const actual = `${JSON.stringify(await generateOpenApiContractDescriptor({
  manifestPath: path.join(root, "sdk-route-manifest.json"),
  specPath,
  excludedContracts: ["database"],
}), null, 2)}\n`;
const expected = await readFile(expectedPath, "utf8");
if (actual !== expected) {
  throw new Error("Comprehensive SDK/OpenAPI contract drift: operation identity, parameters, request, or successful response schema changed. Refresh and review sdk-openapi-contract-descriptor.json.");
}
console.log("Comprehensive SDK/OpenAPI contract descriptor passed.");
