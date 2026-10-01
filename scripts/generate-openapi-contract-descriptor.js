#!/usr/bin/env node
import { readFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";
import {
  generateOpenApiContractDescriptor,
  writeOpenApiContractDescriptor,
} from "./openapi-contract-descriptor.js";
import { currentProducerOpenApi } from "./openapi-contract-paths.js";
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const manifestPath = path.join(root, "sdk-route-manifest.json");
const outputPath = path.join(root, "sdk-openapi-contract-descriptor.json");
const specPath = await currentProducerOpenApi();
if (process.argv.includes("--check")) {
  const descriptor = await generateOpenApiContractDescriptor({
    manifestPath,
    specPath,
  });
  if (
    (await readFile(outputPath, "utf8")) !==
    `${JSON.stringify(descriptor, null, 2)}\n`
  )
    throw new Error("Native OpenAPI contract descriptor is stale.");
} else
  await writeOpenApiContractDescriptor({ manifestPath, outputPath, specPath });
console.log("Native OpenAPI descriptor verified.");
