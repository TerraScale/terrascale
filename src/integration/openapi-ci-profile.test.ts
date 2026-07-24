import assert from "node:assert/strict";
import { execFile } from "node:child_process";
import { cp, mkdir, mkdtemp, readFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";
import { promisify } from "node:util";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../..");
const run = promisify(execFile);

test("configured OpenAPI drift profile runs against the checked-out Documentation snapshot", async () => {
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
    assert.match(workflow, /repository: TerraScale\/TerraScale\.Documentation/u);
    assert.match(workflow, /path: Documentation/u);

    await mkdir(path.join(workspace, "scripts"), { recursive: true });
    await mkdir(path.join(workspace, "Documentation", "public", "openapi"), { recursive: true });
    await cp(path.join(repositoryRoot, "package.json"), path.join(workspace, "package.json"));
    await cp(
      path.join(repositoryRoot, "scripts", "check-openapi-drift.mjs"),
      path.join(workspace, "scripts", "check-openapi-drift.mjs"),
    );
    await cp(
      path.join(repositoryRoot, "test", "fixtures", "openapi-drift", "sdk-route-manifest.json"),
      path.join(workspace, "sdk-route-manifest.json"),
    );
    await cp(
      path.join(
        repositoryRoot,
        "test",
        "fixtures",
        "openapi-drift",
        "openapi-covered-with-extra.json",
      ),
      path.join(workspace, "Documentation", "public", "openapi", "server.openapi.json"),
    );

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
