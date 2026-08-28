#!/usr/bin/env node

import { readFile, writeFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { canonicalDashboardOpenApiSpec } from "./openapi-contract-paths.mjs";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const fixturePath = path.join(repositoryRoot, "test", "fixtures", "public-contracts", "v3-billing.json");
const [specReference = canonicalDashboardOpenApiSpec] = process.argv.slice(2).filter((argument) => argument !== "--write");

const spec = JSON.parse(await readFile(path.resolve(process.cwd(), specReference), "utf8"));
const actual = normalizeContract(spec);
const formatted = `${JSON.stringify(actual, null, 2)}\n`;

if (process.argv.includes("--write")) {
  await writeFile(fixturePath, formatted);
  console.log(`Wrote ${path.relative(repositoryRoot, fixturePath)}.`);
} else {
  const expected = await readFile(fixturePath, "utf8");
  if (expected !== formatted) {
    console.error("Public contract fixture drifted. Run check-public-contract-fixture.mjs <spec> --write.");
    process.exitCode = 1;
  } else {
    console.log("Public contract fixture matches the supplied OpenAPI snapshot.");
  }
}

function normalizeContract(spec) {
  const schemas = spec.components.schemas;
  const pricing = schemas.PricingPlanResponse;
  const checkout = schemas.CreateCheckoutSessionRequest;
  const v3 = spec.paths["/api/v3/projects/{projectId}/saved-queries"].get;
  const page = schemas.ProjectSavedQuerySnapshotPageResponse;
  const capabilityPath = "/api/v1/projects/{projectId}/database-branches/capability";
  const clonePath = "/api/v1/projects/{projectId}/database-branches";
  const capability = spec.paths[capabilityPath].get;
  const clone = spec.paths[clonePath].post;
  const branchCapability = schemas.DatabaseBranchCapabilityResponse;
  const createBranch = schemas.CreateDatabaseBranchRequest;

  return {
    contractVersion: 1,
    pricingPlan: {
      required: pricing.required,
      providerType: types(pricing.properties.provider),
      nullableStringFields: Object.entries(pricing.properties)
        .filter(([, field]) => JSON.stringify(types(field)) === JSON.stringify(["null", "string"]))
        .map(([name]) => name),
      checkout: Object.fromEntries(
        Object.entries(checkout.properties).map(([name, field]) => [
          name,
          {
            types: field.type === undefined ? undefined : types(field),
            nullableUlid: field.oneOf?.some((option) => option.$ref === "#/components/schemas/Ulid") === true,
            deprecated: field.deprecated === true,
            minimum: field.minimum,
            maximum: field.maximum,
            default: field.default,
          },
        ]),
      ),
    },
    savedQueriesV3: {
      method: "get",
      operationId: v3.operationId,
      path: "/api/v3/projects/{projectId}/saved-queries",
      parameters: v3.parameters.map((parameter) => ({
        name: parameter.name,
        required: parameter.required === true,
        types: types(parameter.schema),
        ...(parameter.schema.maxLength === undefined ? {} : { maxLength: parameter.schema.maxLength }),
        ...(parameter.schema.minimum === undefined ? {} : { minimum: parameter.schema.minimum }),
        ...(parameter.schema.maximum === undefined ? {} : { maximum: parameter.schema.maximum }),
        ...(parameter.schema.default === undefined ? {} : { default: parameter.schema.default }),
      })),
      response: {
        required: page.required,
        itemsRef: page.properties.items.items.$ref,
        nextCursorTypes: types(page.properties.nextCursor),
      },
      errorStatuses: Object.keys(v3.responses).filter((status) => status !== "200").map(Number),
    },
    databaseBranches: {
      capability: {
        path: capabilityPath,
        method: "get",
        operationId: capability.operationId,
        required: branchCapability.required,
        propertyTypes: Object.fromEntries(
          Object.entries(branchCapability.properties).map(([name, field]) => [
            name,
            types(field.$ref === undefined ? field : schemas[field.$ref.split("/").at(-1)]),
          ]),
        ),
        errorStatuses: Object.keys(capability.responses).filter((status) => status !== "200").map(Number),
      },
      clone: {
        path: clonePath,
        method: "post",
        operationId: clone.operationId,
        sourceDatabaseIdRequired: createBranch.required?.includes("sourceDatabaseId") === true,
        hasSuccessResponse: Object.keys(clone.responses).some((status) => /^2/u.test(status)),
        errorStatuses: Object.keys(clone.responses).filter((status) => !/^2/u.test(status)).map(Number),
      },
    },
  };
}

function types(schema) {
  return Array.isArray(schema.type) ? schema.type : [schema.type];
}
