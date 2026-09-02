import assert from "node:assert/strict";
import { execFile } from "node:child_process";
import { cp, mkdir, mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import { test } from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";
import { promisify } from "node:util";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../..");
const run = promisify(execFile);

test("default OpenAPI path resolves the current ts-dashboard checkout from a worktree", async () => {
  const workspace = await mkdtemp(path.join(tmpdir(), "terrascale-sdk-openapi-worktree-"));
  const sdkRoot = path.join(workspace, ".codex-worktrees", "sdk");
  const scriptsRoot = path.join(sdkRoot, "scripts");
  const specPath = path.join(
    workspace,
    "ts-dashboard",
    "docs",
    "product",
    "openapi",
    "server.openapi.json",
  );

  try {
    await mkdir(scriptsRoot, { recursive: true });
    await mkdir(path.dirname(specPath), { recursive: true });
    await cp(
      path.join(repositoryRoot, "scripts", "openapi-contract-paths.mjs"),
      path.join(scriptsRoot, "openapi-contract-paths.mjs"),
    );
    await writeFile(specPath, "{}\n", "utf8");

    const moduleUrl = pathToFileURL(path.join(scriptsRoot, "openapi-contract-paths.mjs")).href;
    const result = await run(
      process.execPath,
      [
        "--input-type=module",
        "--eval",
        `import { canonicalDashboardOpenApiSpec } from ${JSON.stringify(moduleUrl)}; console.log(canonicalDashboardOpenApiSpec);`,
      ],
      { cwd: sdkRoot },
    );

    assert.equal(result.stdout.trim(), specPath);
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
      path.join(repositoryRoot, "scripts", "openapi-contract-paths.mjs"),
      path.join(workspace, "scripts", "openapi-contract-paths.mjs"),
    );
    await cp(
      path.join(repositoryRoot, "test", "fixtures", "openapi-drift", "sdk-route-manifest.json"),
      path.join(workspace, "sdk-route-manifest.json"),
    );
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

    await rm(baselinePath);
    await assert.rejects(run(command.command, command.arguments, { cwd: workspace }), /typed-contract-debt-baseline\.json/u);
    await cp(fixtureBaseline, baselinePath);
    const result = await run(command.command, command.arguments, { cwd: workspace });

    assert.match(result.stdout, /OpenAPI drift check passed: 2\/2 SDK routes covered/u);
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
