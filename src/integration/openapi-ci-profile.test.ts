import assert from "node:assert/strict";
import { execFile } from "node:child_process";
import { cp, mkdir, mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import { test } from "vitest";
import { fileURLToPath, pathToFileURL } from "node:url";
import { promisify } from "node:util";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../..");
const run = promisify(execFile);

test("native CI runs the Dashboard and Database OpenAPI gates", async () => {
  const workflow = await readFile(path.join(repositoryRoot, ".forgejo", "workflows", "ci.yml"), "utf8");

  assert.match(workflow, /^  push:\r?\n    branches: \[develop, main\]$/mu);
  assert.doesNotMatch(workflow, /^\s+(?:pull_request|workflow_dispatch|schedule|workflow_call):/mu);
  const staticJob = jobDefinition(workflow, "static");
  assert.match(staticJob, /repository: TerraScale\/ts-dashboard/u);
  assert.match(staticJob, /path: Dashboard/u);
  assert.match(staticJob, /token: ""/u);
  assert.match(staticJob, /persist-credentials: false/u);
  assert.match(staticJob, /actions\/setup-node@49933ea5288caeca8642d1e84afbd3f7d6820020/u);
  assert.match(staticJob, /npm run check:openapi-contract-descriptor -- Dashboard\/docs\/product\/openapi\/server\.openapi\.json/u);
  assert.match(staticJob, /npm run check:openapi-drift -- Dashboard\/docs\/product\/openapi\/server\.openapi\.json sdk-route-manifest\.json --exclude-contract=database/u);
  assert.match(staticJob, /TERRASCALE_DATABASE_OPENAPI_SPEC: \$\{\{ vars\.TERRASCALE_DATABASE_OPENAPI_SPEC \}\}/u);
  assert.match(staticJob, /--strict --require-spec-coverage/u);
  assert.match(staticJob, /check-database-openapi-contract-descriptor\.mjs/u);
  assert.doesNotMatch(workflow, /(?:ts-deploy|secrets\.|NPM_TOKEN|TERRASCALE_E2E_)/u);
});

test("configured OpenAPI drift profile runs against the checked-out Dashboard snapshot", async () => {
  const workspace = await mkdtemp(path.join(tmpdir(), "terrascale-sdk-openapi-profile-"));

  try {
    const [configurationJson, workflow] = await Promise.all([
      readFile(path.join(repositoryRoot, ".terrascale", "deploy.json"), "utf8"),
      readFile(path.join(repositoryRoot, ".forgejo", "workflows", "ci.yml"), "utf8"),
    ]);
    const config = JSON.parse(configurationJson) as DeployConfiguration;
    const command = config.prProfiles
      .find((profile) => profile.name === "static")
      ?.commands.find((candidate) => candidate.description === "OpenAPI drift");
    assert.ok(command);
    assert.deepEqual(command.arguments, ["run", "check:openapi-drift"]);
    assert.match(workflow, /repository: TerraScale\/ts-dashboard/u);
    assert.match(workflow, /path: Dashboard/u);
    const dashboardCheckout = workflow.match(/repository: TerraScale\/ts-dashboard[\s\S]*?persist-credentials: false/u)?.[0] ?? "";
    assert.match(dashboardCheckout, /token: ""/u);
    assert.doesNotMatch(dashboardCheckout, /(?:github|forgejo|gitea)\.token|secrets\./u);
    assert.doesNotMatch(workflow, /TerraScale\.Dashboard/u);

    await mkdir(path.join(workspace, "scripts"), { recursive: true });
    await mkdir(path.join(workspace, "Dashboard", "docs", "product", "openapi"), { recursive: true });
    await cp(path.join(repositoryRoot, "package.json"), path.join(workspace, "package.json"));
    await cp(
      path.join(repositoryRoot, "scripts", "check-openapi-drift.mjs"),
      path.join(workspace, "scripts", "check-openapi-drift.mjs"),
    );
    await cp(
      path.join(repositoryRoot, "scripts", "check-openapi-profiles.mjs"),
      path.join(workspace, "scripts", "check-openapi-profiles.mjs"),
    );
    await cp(
      path.join(repositoryRoot, "scripts", "openapi-contract-paths.mjs"),
      path.join(workspace, "scripts", "openapi-contract-paths.mjs"),
    );
    await cp(
      path.join(repositoryRoot, "scripts", "openapi-contract-descriptor.mjs"),
      path.join(workspace, "scripts", "openapi-contract-descriptor.mjs"),
    );
    await cp(
      path.join(repositoryRoot, "scripts", "check-openapi-contract-descriptor.mjs"),
      path.join(workspace, "scripts", "check-openapi-contract-descriptor.mjs"),
    );
    await cp(
      path.join(repositoryRoot, "scripts", "check-database-openapi-contract-descriptor.mjs"),
      path.join(workspace, "scripts", "check-database-openapi-contract-descriptor.mjs"),
    );
    await cp(
      path.join(repositoryRoot, "test", "fixtures", "openapi-drift", "sdk-route-manifest.json"),
      path.join(workspace, "sdk-route-manifest.json"),
    );
    const databaseManifestPath = path.join(workspace, "sdk-database-route-manifest.json");
    const fixtureManifest = JSON.parse(
      await readFile(
        path.join(repositoryRoot, "test", "fixtures", "openapi-drift", "sdk-route-manifest.json"),
        "utf8",
      ),
    ) as {
      readonly routes: readonly {
        readonly method: string;
        readonly [key: string]: unknown;
      }[];
    };
    const capturedFixtureRoutes = fixtureManifest.routes.map((route) => ({
      ...route,
      sourceContract: {
        body: route.method === "POST"
          ? { kind: "json", shape: { name: "Ada" } }
          : { kind: "void" },
        headers: [],
        query: [],
      },
    }));
    await writeFile(
      path.join(workspace, "sdk-route-manifest.json"),
      `${JSON.stringify({ ...fixtureManifest, routes: capturedFixtureRoutes }, null, 2)}\n`,
      "utf8",
    );
    await writeFile(databaseManifestPath, `${JSON.stringify({
      ...fixtureManifest,
      routes: capturedFixtureRoutes,
      openApiPathPrefix: "/api/v1",
      deferredOpenApiRoutes: [{
        method: "GET",
        pathTemplate: "/api/v1/gadgets",
        ownerTask: "SDK-3",
        needs: "INT-4",
        reason: "Fixture deferral for the composed profile test.",
      }],
    }, null, 2)}\n`, "utf8");
    const baselinePath = path.join(workspace, "typed-contract-debt-baseline.json");
    const fixtureBaseline = path.join(repositoryRoot, "test", "fixtures", "openapi-drift", "typed-contract-debt-baseline.json");
    await cp(fixtureBaseline, baselinePath);
    await cp(
      path.join(
        repositoryRoot,
        "test",
        "fixtures",
        "openapi-drift",
        "openapi-covered-with-extra.json",
      ),
      path.join(workspace, "Dashboard", "docs", "product", "openapi", "server.openapi.json"),
    );
    await mkdir(
      path.join(workspace, "ts-internals", "src", "database", "api"),
      { recursive: true },
    );
    await cp(
      path.join(
        repositoryRoot,
        "test",
        "fixtures",
        "openapi-drift",
        "openapi-covered-with-extra.json",
      ),
      path.join(workspace, "ts-internals", "src", "database", "api", "openapi.v1.json"),
    );
    const databaseSpecPath = path.join(
      workspace,
      "ts-internals",
      "src",
      "database",
      "api",
      "openapi.v1.json",
    );
    const databaseSpec = JSON.parse(await readFile(databaseSpecPath, "utf8")) as {
      paths: Record<string, Record<string, { operationId?: string }>>;
    };
    databaseSpec.paths["/api/v1/widgets"]!.post!.operationId = "widgets.create";
    databaseSpec.paths["/api/v1/widgets/{id}"]!.get!.operationId = "widgets.get";
    await writeFile(databaseSpecPath, `${JSON.stringify(databaseSpec, null, 2)}\n`, "utf8");
    const dashboardSpecPath = path.join(
      workspace,
      "Dashboard",
      "docs",
      "product",
      "openapi",
      "server.openapi.json",
    );
    const dashboardSpec = JSON.parse(await readFile(dashboardSpecPath, "utf8")) as {
      paths: Record<string, Record<string, { operationId?: string }>>;
    };
    dashboardSpec.paths["/api/v1/widgets"]!.post!.operationId = "widgets.create";
    dashboardSpec.paths["/api/v1/widgets/{id}"]!.get!.operationId = "widgets.get";
    await writeFile(dashboardSpecPath, `${JSON.stringify(dashboardSpec, null, 2)}\n`, "utf8");
    const descriptorModule = await import(
      pathToFileURL(path.join(workspace, "scripts", "openapi-contract-descriptor.mjs")).href
    ) as {
      fingerprintOpenApiContractDescriptor(descriptor: unknown): unknown;
      generateOpenApiContractDescriptor(input: {
        readonly excludedContracts?: readonly string[];
        readonly includeFullOperation?: boolean;
        readonly manifestPath: string;
        readonly specPath: string;
      }): Promise<unknown>;
    };
    const databaseDescriptor = descriptorModule.fingerprintOpenApiContractDescriptor(
      await descriptorModule.generateOpenApiContractDescriptor({
        includeFullOperation: true,
        manifestPath: databaseManifestPath,
        specPath: databaseSpecPath,
      }),
    );
    const dashboardDescriptor = await descriptorModule.generateOpenApiContractDescriptor({
      manifestPath: path.join(workspace, "sdk-route-manifest.json"),
      specPath: dashboardSpecPath,
      excludedContracts: ["database"],
    });
    await writeFile(
      path.join(workspace, "sdk-openapi-contract-descriptor.json"),
      `${JSON.stringify(dashboardDescriptor, null, 2)}\n`,
      "utf8",
    );
    await writeFile(
      path.join(workspace, "sdk-database-openapi-contract-descriptor.json"),
      `${JSON.stringify(databaseDescriptor, null, 2)}\n`,
      "utf8",
    );

    await rm(baselinePath);
    await assert.rejects(run(command.command, command.arguments, { cwd: workspace }), /typed-contract-debt-baseline\.json/u);
    await cp(fixtureBaseline, baselinePath);
    const result = await run(command.command, command.arguments, { cwd: workspace });

    assert.match(result.stdout, /OpenAPI drift check passed: 2\/2 SDK routes covered/u);
    await writeFile(
      path.join(workspace, "sdk-database-openapi-contract-descriptor.json"),
      "{}\n",
      "utf8",
    );
    await assert.rejects(
      run(command.command, command.arguments, { cwd: workspace }),
      /Database SDK\/OpenAPI contract descriptor is stale/u,
    );
    await rm(path.join(workspace, "ts-internals"), { recursive: true, force: true });
    const descriptorResult = await run(
      "npm",
      [
        "run",
        "check:openapi-contract-descriptor",
        "--",
        "Dashboard/docs/product/openapi/server.openapi.json",
      ],
      { cwd: workspace },
    );
    assert.match(
      descriptorResult.stdout,
      /Comprehensive SDK\/OpenAPI contract descriptor passed/u,
    );
    assert.doesNotMatch(
      descriptorResult.stdout,
      /Comprehensive Database SDK\/OpenAPI contract descriptor passed/u,
    );
  } finally {
    await rm(workspace, { recursive: true, force: true });
  }
});

type DeployConfiguration = {
  readonly prProfiles: readonly {
    readonly name: string;
    readonly commands: readonly {
      readonly description: string;
      readonly command: string;
      readonly arguments: readonly string[];
    }[];
  }[];
};

function jobDefinition(workflow: string, job: string): string {
  const start = workflow.indexOf(`  ${job}:\n`);
  assert.notEqual(start, -1, `Workflow job ${job} is missing.`);
  const remaining = workflow.slice(start);
  const next = remaining.slice(1).search(/\n  [a-z][a-z0-9-]+:\n/u);
  return next === -1 ? remaining : remaining.slice(0, next + 1);
}
