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
  test("fails checker and formatter for untyped request and every 2xx typed response", async () => {
    const checker = await checkerModule;
    for (const operation of [
      { requestBody: undefined, responses: { 200: typedSchema() } },
      { requestBody: { content: { "application/json": { schema: { $ref: "#/components/schemas/JsonElement" } } } }, responses: { 200: typedSchema() } },
      { requestBody: { content: { "application/json": { schema: { type: "object" } } } }, responses: { 200: {} } },
      { requestBody: { content: { "application/json": { schema: { type: "object" } } } }, responses: { 201: jsonElementSchema() } },
      { requestBody: { content: { "application/json": { schema: { type: "object" } } } }, responses: { 202: {} } },
    ]) {
      const result = checker.compareOpenApiDrift(manifestFor(operation), specFor(operation));
      const formatted = checker.formatOpenApiDriftResult(result, "fixture", "fixture");
      assert.equal(checker.exitCodeForOpenApiDriftResult(result), 1, formatted.stderr);
      assert.match(formatted.stderr, /missing published typed contracts/u);
    }

    const valid = checker.compareOpenApiDrift(manifestFor({ requestBody: { content: { "application/json": { schema: { type: "object" } } } }, responses: { 200: typedSchema(), 201: typedSchema(), 202: typedSchema() } }), specFor({ requestBody: { content: { "application/json": { schema: { type: "object" } } } }, responses: { 200: typedSchema(), 201: typedSchema(), 202: typedSchema() } }));
    assert.equal(checker.exitCodeForOpenApiDriftResult(valid), 0);
  });

  test("CLI fails closed for an untyped matching route", async () => {
    const checker = await checkerModule;
    const lines: string[] = [];
    const status = await checker.runOpenApiDriftCli([
      path.join(fixtureDirectory, "openapi-untyped.json"),
      path.join(fixtureDirectory, "sdk-route-manifest-untyped.json"),
    ], { log: (line: string) => lines.push(line), error: (line: string) => lines.push(line) });

    assert.equal(status, 1);
    assert.match(lines.join("\n"), /missing published typed contracts/u);
  });

  test("allows only the exact reviewed debt baseline outside strict mode", async () => {
    const checker = await checkerModule;
    const untyped = { requestBody: undefined, responses: { 200: typedSchema() } };
    const baseline = [{ method: "POST", pathTemplate: "/api/v1/widgets", missingSide: "request" }];
    const accepted = checker.compareOpenApiDrift(manifestFor(untyped), specFor(untyped), baseline);
    const newDebt = checker.compareOpenApiDrift(manifestFor(untyped), specFor(untyped), []);
    const stale = checker.compareOpenApiDrift(manifestFor({ requestBody: { content: { "application/json": { schema: { type: "object" } } } }, responses: { 200: typedSchema() } }), specFor({ requestBody: { content: { "application/json": { schema: { type: "object" } } } }, responses: { 200: typedSchema() } }), baseline);

    assert.equal(checker.exitCodeForOpenApiDriftResult(accepted), 0);
    assert.equal(checker.exitCodeForOpenApiDriftResult(newDebt), 1);
    assert.equal(checker.exitCodeForOpenApiDriftResult(stale), 1);
    assert.equal(checker.exitCodeForOpenApiDriftResult({ ...accepted, strict: true }), 1);
  });

  test("rejects malformed and duplicate typed-contract baseline entries", async () => {
    const checker = await checkerModule;
    assert.throws(() => checker.validateTypedContractBaseline([{ method: "GET" }]), /must contain/u);
    assert.throws(() => checker.validateTypedContractBaseline([
      { method: "GET", pathTemplate: "/api/v1/widgets", missingSide: "response:200" },
      { method: "GET", pathTemplate: "/api/v1/widgets", missingSide: "response:200" },
    ]), /duplicate/u);
  });

  test("strict CLI succeeds with an empty reviewed baseline and covered typed fixture", async () => {
    const checker = await checkerModule;
    const lines: string[] = [];
    const status = await checker.runOpenApiDriftCli([
      path.join(fixtureDirectory, "openapi-covered-with-extra.json"),
      fixtureManifest,
      "--strict",
    ], { log: line => lines.push(line), error: line => lines.push(line) });
    assert.equal(status, 0);
    assert.match(lines.join("\n"), /OpenAPI drift check passed: 2\/2 SDK routes covered/u);
    assert.match(lines.join("\n"), /Typed contract debt baseline: 0 entries/u);
  });

  test("exits nonzero and reports drift when an SDK route is missing", async () => {
    const checker = await checkerModule;
    const specPath = path.join(fixtureDirectory, "openapi-missing-route.json");
    const result = await checker.checkOpenApiDrift(specPath, fixtureManifest, []);
    const formatted = checker.formatOpenApiDriftResult(result, specPath, fixtureManifest);

    assert.equal(checker.exitCodeForOpenApiDriftResult(result), 1, formatted.stderr);
    assert.match(formatted.stderr, /SDK routes missing from OpenAPI \(drift\): 1/u);
    assert.match(formatted.stderr, /GET \/api\/v1\/widgets\/\{widgetId\} \[widgets\.get/u);
    assert.match(formatted.stderr, /OpenAPI snapshot provenance:/u);
    assert.match(formatted.stderr, /Snapshot file: .*openapi-missing-route\.json \(mtime /u);
    assert.match(formatted.stderr, /Snapshot last commit: (unavailable|[0-9a-f]{40} )/u);
    assert.match(
      formatted.stderr,
      /Triage: SDK route not in spec - either the SDK is wrong OR the snapshot lags the API; refresh the Dashboard canonical snapshot via Documentation openapi-sync and re-run\./u,
    );
    assert.match(formatted.stdout, /Spec routes not covered by SDK \(informational\): 1/u);
    assert.match(formatted.stdout, /GET \/api\/v1\/gadgets/u);
  });

  test("keeps uncovered spec routes informational when SDK routes are covered", async () => {
    const checker = await checkerModule;
    const specPath = path.join(fixtureDirectory, "openapi-covered-with-extra.json");
    const result = await checker.checkOpenApiDrift(specPath, fixtureManifest, []);
    const formatted = checker.formatOpenApiDriftResult(result, specPath, fixtureManifest);

    assert.equal(checker.exitCodeForOpenApiDriftResult(result), 0, formatted.stderr);
    assert.match(formatted.stdout, /OpenAPI drift check passed: 2\/2 SDK routes covered/u);
    assert.match(formatted.stdout, /Spec routes not covered by SDK \(informational\): 1/u);
    assert.match(formatted.stdout, /GET \/api\/v1\/gadgets/u);
  });

  test("requires complete Database API coverage while keeping SDK-3 deferrals explicit", async () => {
    const checker = await checkerModule;
    const operation = {
      requestBody: { content: { "application/json": { schema: { type: "object" } } } },
      responses: { 200: typedSchema() },
    };
    const manifest = {
      openApiPathPrefix: "/api/v1",
      routes: [{
        method: "POST",
        pathTemplate: "/api/v1/widgets",
        routeName: "widgets.create",
        requestType: "CreateWidgetRequest",
        responseType: "Widget",
      }],
      deferredOpenApiRoutes: [{
        method: "POST",
        pathTemplate: "/api/v1/auth/storage/insert-unique",
        ownerTask: "SDK-3",
        needs: "INT-4",
        reason: "The durable Better Auth storage contract is not published yet.",
      }],
    };
    const spec = {
      paths: {
        "/health/live": { get: { responses: { 200: typedSchema() } } },
        "/api/v1/widgets": { post: operation },
        "/api/v1/auth/storage/insert-unique": { post: operation },
      },
    };
    const covered = {
      ...checker.compareOpenApiDrift(manifest, spec),
      strict: true,
      requireSpecCoverage: true,
    };
    const formatted = checker.formatOpenApiDriftResult(covered, "fixture", "fixture");

    assert.equal(checker.exitCodeForOpenApiDriftResult(covered), 0, formatted.stderr);
    assert.match(formatted.stdout, /Explicitly deferred OpenAPI routes: 1/u);
    assert.match(formatted.stdout, /SDK-3; needs INT-4/u);
    assert.match(formatted.stdout, /missing SDK coverage or an explicit deferral: 0/u);

    const missing = {
      ...checker.compareOpenApiDrift(
        manifest,
        { paths: { ...spec.paths, "/api/v1/new-route": { get: operation } } },
      ),
      requireSpecCoverage: true,
    };
    assert.equal(checker.exitCodeForOpenApiDriftResult(missing), 1);
    assert.match(
      checker.formatOpenApiDriftResult(missing, "fixture", "fixture").stderr,
      /GET \/api\/v1\/new-route/u,
    );

    const wrongOperationId = checker.compareOpenApiDrift(
      {
        routes: [{
          method: "POST",
          pathTemplate: "/api/v1/widgets",
          routeName: "databaseDocuments.create",
          openApiOperationId: "documents.create",
          requestType: "CreateWidgetRequest",
          responseType: "Widget",
        }],
      },
      { paths: { "/api/v1/widgets": { post: { ...operation, operationId: "widgets.create" } } } },
    );
    assert.equal(checker.exitCodeForOpenApiDriftResult(wrongOperationId), 1);
    assert.match(
      checker.formatOpenApiDriftResult(wrongOperationId, "fixture", "fixture").stderr,
      /expected documents\.create, published widgets\.create/u,
    );
  });
});

type OpenApiDriftResult = {
  readonly missingSdkRoutes: readonly unknown[];
  readonly typedContractViolations: readonly unknown[];
  readonly unexpectedTypedContractViolations: readonly unknown[];
  readonly staleTypedContractBaseline: readonly unknown[];
  readonly staleDeferredOpenApiRoutes: readonly unknown[];
  readonly uncoveredSpecRoutes: readonly unknown[];
  readonly deferredOpenApiRoutes: readonly unknown[];
  readonly operationIdentityViolations: readonly unknown[];
  readonly strict?: boolean;
  readonly requireSpecCoverage?: boolean;
};

type FormattedOpenApiDriftResult = {
  readonly stderr: string;
  readonly stdout: string;
};

type OpenApiDriftModule = {
  readonly validateTypedContractBaseline: (baseline: unknown) => void;
  readonly runOpenApiDriftCli: (args: readonly string[], output: { readonly log: (line: string) => void; readonly error: (line: string) => void }) => Promise<number>;
  readonly compareOpenApiDrift: (manifest: unknown, spec: unknown, baseline?: unknown) => OpenApiDriftResult;
  readonly typedContractViolation: (
    route: { readonly method: string; readonly pathTemplate: string; readonly requestType?: string; readonly responseType?: string },
    operation: { readonly requestBody?: unknown; readonly responses?: unknown },
  ) => { readonly missingSide: string; readonly message: string } | undefined;
  readonly checkOpenApiDrift: (
    specReference: string,
    manifestReference: string,
    baselineReference?: string | readonly unknown[], strict?: boolean,
  ) => Promise<OpenApiDriftResult>;
  readonly exitCodeForOpenApiDriftResult: (result: OpenApiDriftResult) => number;
  readonly formatOpenApiDriftResult: (
    result: OpenApiDriftResult,
    specReference: string,
    manifestReference: string,
  ) => FormattedOpenApiDriftResult;
};

function manifestFor(operation: unknown) {
  return { routes: [{ method: "POST", pathTemplate: "/api/v1/widgets", routeName: "widgets.create", requestType: "CreateWidgetRequest", responseType: "Widget", operation }] };
}

function specFor(operation: unknown) {
  return { paths: { "/api/v1/widgets": { post: operation } } };
}

function typedSchema() {
  return { content: { "application/json": { schema: { type: "object" } } } };
}

function jsonElementSchema() {
  return { content: { "application/json": { schema: { $ref: "#/components/schemas/JsonElement" } } } };
}
