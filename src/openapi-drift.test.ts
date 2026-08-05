import assert from "node:assert/strict";
import path from "node:path";
import { describe, test } from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const checkerScript = path.join(repositoryRoot, "scripts", "check-openapi-drift.mjs");
const fixtureDirectory = path.join(repositoryRoot, "test", "fixtures", "openapi-drift");
const fixtureManifest = path.join(fixtureDirectory, "sdk-route-manifest.json");
const checkerModule = import(pathToFileURL(checkerScript).href) as Promise<OpenApiDriftModule>;

describe("OpenAPI drift checker", () => {
  test("rejects typed SDK declarations when OpenAPI publishes no typed body", async () => {
    const checker = await checkerModule;
    const responseOnly = checker.typedContractViolation(
      { method: "GET", pathTemplate: "/api/v1/account/export", responseType: "AccountExportData" },
      { responses: { 200: { content: { "application/json": { schema: { $ref: "#/components/schemas/JsonElement" } } } } } },
    );
    const requestOnly = checker.typedContractViolation(
      { method: "POST", pathTemplate: "/api/v1/users/me/delete", requestType: "InitiateAccountDeletionRequest" },
      { responses: { 200: { content: { "application/json": { schema: { $ref: "#/components/schemas/JsonElement" } } } } } },
    );

    assert.match(responseOnly ?? "", /only publishes JsonElement/u);
    assert.match(requestOnly ?? "", /without a published JSON request schema/u);
  });

  test("exits nonzero and reports drift when an SDK route is missing", async () => {
    const checker = await checkerModule;
    const specPath = path.join(fixtureDirectory, "openapi-missing-route.json");
    const result = await checker.checkOpenApiDrift(specPath, fixtureManifest);
    const formatted = checker.formatOpenApiDriftResult(result, specPath, fixtureManifest);

    assert.equal(checker.exitCodeForOpenApiDriftResult(result), 1, formatted.stderr);
    assert.match(formatted.stderr, /SDK routes missing from OpenAPI \(drift\): 1/u);
    assert.match(formatted.stderr, /GET \/api\/v1\/widgets\/\{widgetId\} \[widgets\.get/u);
    assert.match(formatted.stderr, /OpenAPI snapshot provenance:/u);
    assert.match(formatted.stderr, /Snapshot file: .*openapi-missing-route\.json \(mtime /u);
    assert.match(formatted.stderr, /Snapshot last commit: (unavailable|[0-9a-f]{40} )/u);
    assert.match(
      formatted.stderr,
      /Triage: SDK route not in spec - either the SDK is wrong OR the snapshot lags the API; refresh via Documentation openapi-sync and re-run\./u,
    );
    assert.match(formatted.stdout, /Spec routes not covered by SDK \(informational\): 1/u);
    assert.match(formatted.stdout, /GET \/api\/v1\/gadgets/u);
  });

  test("keeps uncovered spec routes informational when SDK routes are covered", async () => {
    const checker = await checkerModule;
    const specPath = path.join(fixtureDirectory, "openapi-covered-with-extra.json");
    const result = await checker.checkOpenApiDrift(specPath, fixtureManifest);
    const formatted = checker.formatOpenApiDriftResult(result, specPath, fixtureManifest);

    assert.equal(checker.exitCodeForOpenApiDriftResult(result), 0, formatted.stderr);
    assert.match(formatted.stdout, /OpenAPI drift check passed: 2\/2 SDK routes covered/u);
    assert.match(formatted.stdout, /Spec routes not covered by SDK \(informational\): 1/u);
    assert.match(formatted.stdout, /GET \/api\/v1\/gadgets/u);
  });
});

type OpenApiDriftResult = {
  readonly missingSdkRoutes: readonly unknown[];
};

type FormattedOpenApiDriftResult = {
  readonly stderr: string;
  readonly stdout: string;
};

type OpenApiDriftModule = {
  readonly typedContractViolation: (
    route: { readonly method: string; readonly pathTemplate: string; readonly requestType?: string; readonly responseType?: string },
    operation: { readonly requestBody?: unknown; readonly responses?: unknown },
  ) => string | undefined;
  readonly checkOpenApiDrift: (
    specReference: string,
    manifestReference: string,
  ) => Promise<OpenApiDriftResult>;
  readonly exitCodeForOpenApiDriftResult: (result: OpenApiDriftResult) => number;
  readonly formatOpenApiDriftResult: (
    result: OpenApiDriftResult,
    specReference: string,
    manifestReference: string,
  ) => FormattedOpenApiDriftResult;
};
