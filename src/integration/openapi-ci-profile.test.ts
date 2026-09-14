import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import path from "node:path";
import { test } from "vitest";
import { fileURLToPath } from "node:url";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../..");

test("the CI workflow delegates contract and verification gates to ts-deploy", async () => {
  const workflow = await readFile(path.join(repositoryRoot, ".forgejo", "workflows", "ci.yml"), "utf8");

  assert.match(workflow, /^  push:\r?\n    branches: \[main\]$/mu);
  assert.match(workflow, /^\s+pull_request:\r?\n    branches: \[main\]$/mu);
  assert.match(workflow, /^\s+workflow_dispatch:/mu);
  assert.doesNotMatch(workflow, /^\s*(?:container|strategy|matrix):/mu);
  assert.doesNotMatch(
    workflow,
    /^(?:\s*-?\s*run:.*\b(?:npm|make contracts|git diff --exit-code)\b|.*\b(?:check:(?:openapi|forbidden-surface|route-manifest|public-contract-fixture)|node --test|generate-(?:route-manifest|openapi))\b)/mu,
  );
  const databaseSpecPath = path.join(
    workspace,
    "ts-internals",
    "src",
    "database",
    "api",
    "openapi.v1.json",
  );

  try {
    await mkdir(scriptsRoot, { recursive: true });
    await mkdir(path.dirname(specPath), { recursive: true });
    await mkdir(path.dirname(databaseSpecPath), { recursive: true });
    await cp(
      path.join(repositoryRoot, "scripts", "openapi-contract-paths.mjs"),
      path.join(scriptsRoot, "openapi-contract-paths.mjs"),
    );
    await writeFile(specPath, "{}\n", "utf8");
    await writeFile(databaseSpecPath, "{}\n", "utf8");

    const moduleUrl = pathToFileURL(path.join(scriptsRoot, "openapi-contract-paths.mjs")).href;
    const result = await run(
      process.execPath,
      [
        "--input-type=module",
        "--eval",
        `import { canonicalDashboardOpenApiSpec, canonicalDatabaseOpenApiSpec } from ${JSON.stringify(moduleUrl)}; console.log(canonicalDashboardOpenApiSpec); console.log(canonicalDatabaseOpenApiSpec);`,
      ],
      { cwd: sdkRoot },
    );

    assert.deepEqual(result.stdout.trim().split("\n"), [specPath, databaseSpecPath]);
  } finally {
    await rm(workspace, { recursive: true, force: true });
  }
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
    assert.match(workflow, /repository: TerraScale\/ts-dashboard/u);
    assert.match(workflow, /path: ts-typescript\/Dashboard/u);
    assert.doesNotMatch(workflow, /TERRASCALE_SOURCE_READ_TOKEN|TERRASCALE_DEPLOY_REPOSITORY_READ_TOKEN/u);
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
