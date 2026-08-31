#!/usr/bin/env node

import { readFile, writeFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";
import {
  fingerprintOpenApiContractDescriptor,
  generateOpenApiContractDescriptor,
} from "./openapi-contract-descriptor.mjs";
import { canonicalDatabaseOpenApiSpec } from "./openapi-contract-paths.mjs";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const outputPath = path.join(root, "sdk-database-openapi-contract-descriptor.json");
const manifestPath = path.join(root, "sdk-database-route-manifest.json");
const specPath =
  process.argv.slice(2).find((argument) => !argument.startsWith("-")) ??
  canonicalDatabaseOpenApiSpec;
const check = process.argv.includes("--check");
const descriptor = fingerprintOpenApiContractDescriptor(
  await generateOpenApiContractDescriptor({
    includeFullOperation: true,
    manifestPath,
    specPath,
  }),
);
const json = `${JSON.stringify(descriptor, null, 2)}\n`;

if (check) {
  if (await readFile(outputPath, "utf8") !== json) {
    throw new Error(
      "sdk-database-openapi-contract-descriptor.json is out of date. Run npm run generate:openapi-contract-descriptor.",
    );
  }
  console.log(`Database OpenAPI contract descriptor is current (${descriptor.routeCount} routes).`);
} else {
  await writeFile(outputPath, json);
  console.log(
    `Wrote sdk-database-openapi-contract-descriptor.json with ${descriptor.routeCount} routes.`,
  );
}
