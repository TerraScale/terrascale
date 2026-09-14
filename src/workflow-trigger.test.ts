import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { test } from "vitest";

test("develop and main pushes run trunk verification", async () => {
  const workflow = await readFile(
    new URL("../.forgejo/workflows/ci.yml", import.meta.url),
    "utf8",
  );

  assert.match(workflow, /  push:\r?\n    branches: \[develop, main\]/u);
  assert.doesNotMatch(workflow, /pull_request/u);
  assert.doesNotMatch(workflow, /TerraScale-Old\/TerraScale\.Deploy/u);
  assert.doesNotMatch(workflow, /repository:\s*TerraScale\/ts-deploy/u);
  assert.doesNotMatch(workflow, /TERRASCALE_SOURCE_READ_TOKEN|TERRASCALE_DEPLOY_REPOSITORY_READ_TOKEN/u);
  assert.doesNotMatch(workflow, /(?:bf9703ffca63|ce0183692c02|EXPECTED_DEPLOY_CLI_SHA|go-version-file|go build|go install|ts-deploy\/cmd\/terrascale-deploy@v)/u);
  assert.match(workflow, /X-10 fail-closed: DEP-1 is reopened and no ts-deploy commit is authorized for consumers\./u);
  assert.match(workflow, /Block until the replacement Deploy CLI passes DEP-1[\s\S]*exit 1/u);
  assert.match(workflow, /playwright@sha256:dcc5531e97840b9b5e794f2814476b21571c5124a3fca2267d73041f56e7580e/u);
  assert.doesNotMatch(workflow, /git\.terrascale\.tech\/terrascale\/terrascale-builder/u);
  assert.match(workflow, /verify-static:[\s\S]*--profile static/u);
  assert.match(workflow, /verify-node-22:[\s\S]*--profile tests/u);
  assert.match(workflow, /verify-node-24:[\s\S]*--profile tests/u);
  assert.match(workflow, /verify-browser:[\s\S]*--profile browser/u);
  assert.match(workflow, /candidate-build:[\s\S]*forgejo\.ref == 'refs\/heads\/main'/u);
});

function jobDefinition(workflow: string, job: string): string {
  const start = workflow.indexOf(`  ${job}:\n`);
  assert.notEqual(start, -1, `Workflow job ${job} is missing.`);
  const remaining = workflow.slice(start);
  const next = remaining.slice(1).search(/\n  [a-z][a-z0-9-]+:\n/u);
  return next === -1 ? remaining : remaining.slice(0, next + 1);
}
