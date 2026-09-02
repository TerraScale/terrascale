import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import path from "node:path";
import { test } from "vitest";
import { fileURLToPath } from "node:url";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../..");

test("the static CI profile runs the Vitest suite against the cloned Dashboard snapshot", async () => {
  const [configurationJson, workflow] = await Promise.all([
    readFile(path.join(repositoryRoot, ".terrascale", "deploy.json"), "utf8"),
    readFile(path.join(repositoryRoot, ".forgejo", "workflows", "ci.yml"), "utf8"),
  ]);
  const config = JSON.parse(configurationJson) as DeployConfiguration;
  const staticProfile = config.prProfiles.find((profile) => profile.name === "static");

  assert.ok(staticProfile);
  assert.deepEqual(
    staticProfile.commands.map((command) => `${command.command} ${command.arguments.join(" ")}`),
    ["npm ci", "npm run typecheck", "npm run build", "npm test"],
  );

  // The OpenAPI drift and public-contract-fixture Vitest gates resolve the
  // Dashboard OpenAPI snapshot from the working directory first, so CI must
  // clone it before npm test.
  assert.match(workflow, /repository: TerraScale\/ts-dashboard/u);
  assert.doesNotMatch(workflow, /TerraScale\.Dashboard/u);
  assert.match(workflow, /path: Dashboard/u);
  const staticJob = workflow.slice(
    workflow.indexOf("  pr-static:"),
    workflow.indexOf("  pr-build:"),
  );
  assert.ok(staticJob.indexOf("ts-dashboard.git Dashboard") < staticJob.indexOf("npm test"));
  assert.doesNotMatch(workflow, /(?:check:openapi|check:forbidden-surface|check:route-manifest|check:public-contract-fixture|node --test)/u);
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
