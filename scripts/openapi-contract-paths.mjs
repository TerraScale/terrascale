import { existsSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

export const repositoryRoot = path.resolve(
  path.dirname(fileURLToPath(import.meta.url)),
  "..",
);

const siblingDashboardOpenApiSpec = path.resolve(
  repositoryRoot,
  "..",
  "TerraScale.Dashboard",
  "docs",
  "product",
  "openapi",
  "server.openapi.json",
);

const checkedOutDashboardOpenApiSpec = path.resolve(
  process.cwd(),
  "Dashboard",
  "docs",
  "product",
  "openapi",
  "server.openapi.json",
);

export const canonicalDashboardOpenApiSpec =
  process.env.TERRASCALE_DASHBOARD_OPENAPI_SPEC ??
  (existsSync(checkedOutDashboardOpenApiSpec)
    ? checkedOutDashboardOpenApiSpec
    : siblingDashboardOpenApiSpec);
