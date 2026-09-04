#!/usr/bin/env node

import { readFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { generateOpenApiContractDescriptor, writeOpenApiContractDescriptor } from "./openapi-contract-descriptor.ts";
import { canonicalDashboardOpenApiSpec } from "./openapi-contract-paths.ts";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const outputPath = path.join(root, "sdk-openapi-contract-descriptor.json");
const manifestPath = path.join(root, "sdk-route-manifest.json");
const specPath = process.argv.slice(2).find((argument) => !argument.startsWith("-")) ?? canonicalDashboardOpenApiSpec;
const check = process.argv.includes("--check");
const descriptor = await generateOpenApiContractDescriptor({ manifestPath, specPath });
const json = `${JSON.stringify(descriptor, null, 2)}\n`;

if (check) {
  if (await readFile(outputPath, "utf8") !== json) throw new Error("sdk-openapi-contract-descriptor.json is out of date. Run npm run generate:openapi-contract-descriptor.");
  console.log(`OpenAPI contract descriptor is current (${descriptor.routeCount} routes).`);
} else {
  await writeOpenApiContractDescriptor({ manifestPath, outputPath, specPath });
  console.log(`Wrote sdk-openapi-contract-descriptor.json with ${descriptor.routeCount} routes.`);
}
