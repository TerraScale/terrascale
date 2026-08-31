#!/usr/bin/env node

import path from "node:path";
import { runOpenApiDriftCli } from "./check-openapi-drift.mjs";
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
  process.exitCode = Math.max(dashboardStatus, databaseStatus);
}
