#!/usr/bin/env node

import { readFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import {
  fingerprintOpenApiContractDescriptor,
  generateOpenApiContractDescriptor,
} from "./openapi-contract-descriptor.mjs";
import { canonicalDatabaseOpenApiSpec } from "./openapi-contract-paths.mjs";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");

export async function checkDatabaseOpenApiContractDescriptor(
  specPath = canonicalDatabaseOpenApiSpec,
) {
  const expectedPath = path.join(root, "sdk-database-openapi-contract-descriptor.json");
  const actual = `${JSON.stringify(fingerprintOpenApiContractDescriptor(
    await generateOpenApiContractDescriptor({
      includeFullOperation: true,
      manifestPath: path.join(root, "sdk-database-route-manifest.json"),
      specPath,
    }),
  ), null, 2)}\n`;
  const expected = await readFile(expectedPath, "utf8");
  if (actual !== expected) {
    throw new Error(
      "Comprehensive Database SDK/OpenAPI contract drift: operation identity, parameters, request, or successful response schema changed. Refresh and review sdk-database-openapi-contract-descriptor.json.",
    );
  }
  console.log("Comprehensive Database SDK/OpenAPI contract descriptor passed.");
}

if (process.argv[1] !== undefined && pathToFileURL(path.resolve(process.argv[1])).href === import.meta.url) {
  await checkDatabaseOpenApiContractDescriptor(process.argv[2]);
}
