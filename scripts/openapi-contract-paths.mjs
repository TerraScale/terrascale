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
  "ts-dashboard",
  "docs",
  "product",
  "openapi",
  "server.openapi.json",
);

const worktreeDashboardOpenApiSpec = path.resolve(
  repositoryRoot,
  "..",
  "..",
  "ts-dashboard",
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
    : existsSync(siblingDashboardOpenApiSpec)
      ? siblingDashboardOpenApiSpec
      : existsSync(worktreeDashboardOpenApiSpec)
      ? worktreeDashboardOpenApiSpec
      : siblingDashboardOpenApiSpec);

const siblingDatabaseOpenApiSpec = path.resolve(
  repositoryRoot,
  "..",
  "ts-internals",
  "src",
  "database",
  "api",
  "openapi.v1.json",
);

const worktreeDatabaseOpenApiSpec = path.resolve(
  repositoryRoot,
  "..",
  "..",
  "ts-internals",
  "src",
  "database",
  "api",
  "openapi.v1.json",
);

const checkedOutDatabaseOpenApiSpec = path.resolve(
  process.cwd(),
  "ts-internals",
  "src",
  "database",
  "api",
  "openapi.v1.json",
);

export const canonicalDatabaseOpenApiSpec =
  process.env.TERRASCALE_DATABASE_OPENAPI_SPEC ??
  (existsSync(checkedOutDatabaseOpenApiSpec)
    ? checkedOutDatabaseOpenApiSpec
    : existsSync(siblingDatabaseOpenApiSpec)
      ? siblingDatabaseOpenApiSpec
      : existsSync(worktreeDatabaseOpenApiSpec)
        ? worktreeDatabaseOpenApiSpec
        : siblingDatabaseOpenApiSpec);
