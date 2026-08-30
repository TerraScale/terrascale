#!/usr/bin/env node

import { readFile, writeFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { canonicalDashboardOpenApiSpec } from "./openapi-contract-paths.mjs";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const fixturePath = path.join(repositoryRoot, "test", "fixtures", "public-contracts", "saved-queries-and-branches.json");
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
