// Public contract fixture gate (Vitest port of
// scripts/check-public-contract-fixture.mjs, D-47). The committed
// saved-queries-and-branches fixture must equal the normalization of the
// owner-published Dashboard OpenAPI snapshot, so owner-side contract
// changes surface as test failures instead of silent SDK drift.
//
// Skips when the Dashboard OpenAPI snapshot is absent.

import assert from "node:assert/strict";
import { access, readFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { describe, test } from "vitest";

import { canonicalDashboardOpenApiSpec } from "../scripts/openapi-contract-paths.mjs";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const fixturePath = path.join(repositoryRoot, "test", "fixtures", "public-contracts", "saved-queries-and-branches.json");

const specAvailable = await access(canonicalDashboardOpenApiSpec)
  .then(() => true)
  .catch(() => false);

describe("public contract fixture", () => {
  test.skipIf(!specAvailable)(
    "fixture matches the owner-published saved-query and branching contract",
    async () => {
      const spec = JSON.parse(await readFile(canonicalDashboardOpenApiSpec, "utf8"));
      const expected = await readFile(fixturePath, "utf8");

      assert.equal(expected, `${JSON.stringify(normalizeContract(spec), null, 2)}\n`);
    },
  );
});

function normalizeContract(spec) {
  const schemas = spec.components.schemas;
  const savedQueryListPath = "/api/v1/projects/{projectId}/saved-queries";
  const savedQueryGetPath = "/api/v1/projects/{projectId}/saved-queries/{savedQueryId}";
  const listSavedQueries = spec.paths[savedQueryListPath]?.get;
  const getSavedQuery = spec.paths[savedQueryGetPath]?.get;
  const createSavedQuery = spec.paths[savedQueryListPath]?.post;
  const updateSavedQuery = spec.paths[savedQueryGetPath]?.put;
  const deleteSavedQuery = spec.paths[savedQueryGetPath]?.delete;
  if (
    listSavedQueries === undefined ||
    getSavedQuery === undefined ||
    createSavedQuery === undefined ||
    updateSavedQuery === undefined ||
    deleteSavedQuery === undefined
  ) {
    throw new Error("The owner OpenAPI must publish the complete v1 saved-query CRUD contract.");
  }
  assertLegacySavedQueryRoutesAbsent(spec.paths);
  const savedQueryPage = schemas.ProjectSavedQueryPageResponse;
  const savedQueryRequest = schemas.ProjectSavedQueryRequest;
  const updateSavedQueryRequest = schemas.UpdateProjectSavedQueryRequest;
  const savedQueryResponse = schemas.ProjectSavedQueryResponse;
  if (
    savedQueryPage === undefined ||
    savedQueryRequest === undefined ||
    updateSavedQueryRequest === undefined ||
    savedQueryResponse === undefined
  ) {
    throw new Error("The owner OpenAPI must publish the saved-query page, request, update, and response schemas.");
  }
  const capabilityPath = "/api/v1/projects/{projectId}/database-branches/capability";
  const clonePath = "/api/v1/projects/{projectId}/database-branches";
  const capability = spec.paths[capabilityPath].get;
  const clone = spec.paths[clonePath].post;
  const branchCapability = schemas.DatabaseBranchCapabilityResponse;
  const createBranch = schemas.CreateDatabaseBranchRequest;

  return {
    contractVersion: 2,
    savedQueriesV1: {
      list: {
        method: "get",
        operationId: listSavedQueries.operationId,
        path: savedQueryListPath,
        parameters: listSavedQueries.parameters.map(normalizeParameter),
        responseRef: listSavedQueries.responses["200"].content["application/json"].schema.$ref,
        response: {
          required: savedQueryPage.required,
          itemsRef: savedQueryPage.properties.items.items.$ref,
          totalCountTypes: types(savedQueryPage.properties.totalCount),
          nextCursorTypes: types(savedQueryPage.properties.nextCursor),
        },
        errorStatuses: errorStatuses(listSavedQueries),
      },
      get: {
        method: "get",
        operationId: getSavedQuery.operationId,
        path: savedQueryGetPath,
        responseRef: getSavedQuery.responses["200"].content["application/json"].schema.$ref,
        errorStatuses: errorStatuses(getSavedQuery),
      },
      create: {
        method: "post",
        operationId: createSavedQuery.operationId,
        path: savedQueryListPath,
        requestRef: createSavedQuery.requestBody.content["application/json"].schema.$ref,
        responseRef: createSavedQuery.responses["201"].content["application/json"].schema.$ref,
        errorStatuses: errorStatuses(createSavedQuery),
      },
      update: {
        method: "put",
        operationId: updateSavedQuery.operationId,
        path: savedQueryGetPath,
        requestRef: updateSavedQuery.requestBody.content["application/json"].schema.$ref,
        responseRef: updateSavedQuery.responses["200"].content["application/json"].schema.$ref,
        errorStatuses: errorStatuses(updateSavedQuery),
      },
      delete: {
        method: "delete",
        operationId: deleteSavedQuery.operationId,
        path: savedQueryGetPath,
        responseRef: deleteSavedQuery.responses["200"].content["application/json"].schema.$ref,
        errorStatuses: errorStatuses(deleteSavedQuery),
      },
      models: {
        createRequired: savedQueryRequest.required,
        updateRequired: updateSavedQueryRequest.required,
        responseRequired: savedQueryResponse.required,
        revisionTypes: types(savedQueryResponse.properties.revision),
      },
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

function assertLegacySavedQueryRoutesAbsent(paths) {
  const legacyPaths = Object.keys(paths).filter((route) =>
    /^\/api\/v[23]\/projects\/\{projectId\}\/saved-queries(?:\/|$)/u.test(route),
  );
  if (legacyPaths.length > 0) {
    throw new Error(`Legacy saved-query routes must remain absent: ${legacyPaths.sort().join(", ")}.`);
  }
}

function normalizeParameter(parameter) {
  return {
    name: parameter.name,
    required: parameter.required === true,
    types: types(parameter.schema),
    ...(parameter.schema.minimum === undefined ? {} : { minimum: parameter.schema.minimum }),
    ...(parameter.schema.maximum === undefined ? {} : { maximum: parameter.schema.maximum }),
    ...(parameter.schema.default === undefined ? {} : { default: parameter.schema.default }),
    ...(parameter.schema.pattern === undefined ? {} : { pattern: parameter.schema.pattern }),
  };
}

function errorStatuses(operation) {
  return Object.keys(operation.responses).filter((status) => !/^2\d\d$/u.test(status)).map(Number);
}

function types(schema) {
  return Array.isArray(schema.type) ? schema.type : [schema.type];
}
