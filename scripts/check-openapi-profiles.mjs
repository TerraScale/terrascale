#!/usr/bin/env node

import { readFile } from "node:fs/promises";
import path from "node:path";
import { runOpenApiDriftCli } from "./check-openapi-drift.mjs";
import {
  fingerprintOpenApiContractDescriptor,
  generateOpenApiContractDescriptor,
} from "./openapi-contract-descriptor.mjs";
import {
  canonicalDashboardOpenApiSpec,
  canonicalDatabaseOpenApiSpec,
  repositoryRoot,
} from "./openapi-contract-paths.mjs";

const explicitArguments = process.argv.slice(2);

if (explicitArguments.length > 0) {
  process.exitCode = await runOpenApiDriftCli(explicitArguments);
} else {
  const dashboardStatus = await runOpenApiDriftCli([
    canonicalDashboardOpenApiSpec,
    path.join(repositoryRoot, "sdk-route-manifest.json"),
    "--exclude-contract=database",
  ]);
  const databaseStatus = await runOpenApiDriftCli([
    canonicalDatabaseOpenApiSpec,
    path.join(repositoryRoot, "sdk-database-route-manifest.json"),
    "--strict",
    "--require-spec-coverage",
  ]);
  const databaseDescriptorStatus = await checkDatabaseDescriptor();
  process.exitCode = Math.max(
    dashboardStatus,
    databaseStatus,
    databaseDescriptorStatus,
  );
}

async function checkDatabaseDescriptor() {
  const descriptorPath = path.join(
    repositoryRoot,
    "sdk-database-openapi-contract-descriptor.json",
  );
  try {
    const descriptor = fingerprintOpenApiContractDescriptor(
      await generateOpenApiContractDescriptor({
        includeFullOperation: true,
        manifestPath: path.join(repositoryRoot, "sdk-database-route-manifest.json"),
        specPath: canonicalDatabaseOpenApiSpec,
      }),
    );
    const actual = `${JSON.stringify(descriptor, null, 2)}\n`;
    const expected = await readFile(descriptorPath, "utf8");
    if (actual !== expected) {
      throw new Error(
        "Database SDK/OpenAPI contract descriptor is stale. Run npm run generate:openapi-contract-descriptor and review the complete contract diff.",
      );
    }
    console.log(
      `Database OpenAPI contract descriptor passed (${descriptor.routeCount} routes).`,
    );
    return 0;
  } catch (error) {
    console.error(error instanceof Error ? error.message : String(error));
    return 1;
  }
}
