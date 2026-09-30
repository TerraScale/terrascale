import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { test } from "vitest";

test("native CI preserves event identity, exact source gates and actual framework/browser checks", async () => {
  const workflow = await readFile(
    new URL("../.forgejo/workflows/ci.yml", import.meta.url),
    "utf8",
  );
  assert.match(workflow, /push:\n    branches: \[develop, main\]/u);
  assert.match(workflow, /pull_request:\n    branches: \[main\]/u);
  assert.match(workflow, /permissions:\n  contents: read/u);
  assert.doesNotMatch(
    workflow,
    /pull_request_target|CONTRACT_ARCHIVE|EDGE_CONTRACT_SOURCE|contracts\/v1|TERRASCALE_DEPLOY|NPM_TOKEN/u,
  );
  for (const job of ["static", "node", "browser"]) {
    const definition = jobDefinition(workflow, job);
    assert.match(definition, /runs-on: terrascale-builder/u);
    assert.match(definition, /actions\/checkout@[0-9a-f]{40}/u);
    assert.match(definition, /ref: \$\{\{ forgejo\.sha \}\}/u);
    assert.match(definition, /persist-credentials: false/u);
    assert.match(definition, /node scripts\/provision-reviewed-npm\.ts/u);
    assert.match(definition, /npm ci/u);
    assert.match(definition, /npm run build/u);
  }
  for (const job of ["static", "node"]) {
    const definition = jobDefinition(workflow, job);
    assert.match(
      definition,
      /bash scripts\/ci\/provision-current-contract-inputs\.sh/u,
    );
    assert.ok(
      definition.indexOf("bash scripts/ci/provision-current-contract-inputs.sh") <
        definition.indexOf("npm run check:current-contract-inputs"),
      "Producer inputs must be provisioned before the unchanged digest guard.",
    );
    assert.match(definition, /npm run check:current-contract-inputs/u);
    assert.match(definition, /npm run check:current-contract/u);
  }
  assert.match(jobDefinition(workflow, "static"), /npm run typecheck/u);
  assert.match(jobDefinition(workflow, "node"), /npm run check:frameworks/u);
  assert.match(jobDefinition(workflow, "node"), /sha256sum --check --strict/u);
  assert.match(jobDefinition(workflow, "node"), /node: "22\.23\.2"/u);
  assert.match(jobDefinition(workflow, "node"), /node: "24\.21\.0"/u);
  assert.match(jobDefinition(workflow, "browser"), /npm run test:browser/u);
  assert.match(
    jobDefinition(workflow, "browser"),
    /bash scripts\/ci\/provision-branded-browsers\.sh/u,
  );
});

function jobDefinition(workflow: string, job: string): string {
  const start = workflow.indexOf(`  ${job}:\n`);
  assert.notEqual(start, -1);
  const remaining = workflow.slice(start);
  const next = remaining.slice(1).search(/\n  [a-z][a-z0-9-]+:\n/u);
  return next === -1 ? remaining : remaining.slice(0, next + 1);
}
