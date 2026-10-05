import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { test } from "vitest";

test("CI preserves event identity, real C# producer and framework/browser checks", async () => {
  const workflow = await readFile(
    new URL("../.forgejo/workflows/ci.yml", import.meta.url),
    "utf8",
  );
  assert.match(workflow, /push:\n    branches: \[develop, main\]/u);
  assert.match(workflow, /pull_request:\n    branches: \[main\]/u);
  assert.match(workflow, /permissions:\n  contents: read/u);
  assert.doesNotMatch(
    workflow,
    /pull_request_target|CONTRACT_ARCHIVE|EDGE_CONTRACT_SOURCE|contracts\/v1|TERRASCALE_DEPLOY/u,
  );
  for (const job of ["static", "node", "browser"]) {
    const definition = jobDefinition(workflow, job);
    assert.doesNotMatch(definition, /NPM_TOKEN/u);
    assert.match(definition, /runs-on: terrascale-builder/u);
    assert.match(definition, /actions\/checkout@[0-9a-f]{40}/u);
    assert.match(definition, /ref: \$\{\{ forgejo\.sha \}\}/u);
    assert.match(definition, /persist-credentials: false/u);
    assert.match(definition, /node scripts\/provision-reviewed-npm\.js/u);
    assert.match(definition, /npm ci/u);
    assert.match(definition, /npm run build/u);
  }
  const node = jobDefinition(workflow, "node");
  assert.match(node, /node scripts\/ci\/provision-terrabase\.mjs/u);
  assert.match(node, /FORGEJO_TOKEN: \$\{\{ secrets\.REGISTRY_TOKEN \}\}/u);
  assert.match(node, /node scripts\/ci\/run-terrabase-integration\.mjs npm test/u);
  assert.ok(node.indexOf("provision-terrabase.mjs") < node.indexOf("run-terrabase-integration.mjs"));
  assert.match(jobDefinition(workflow, "static"), /npm run check:v1-route-manifest/u);
  assert.doesNotMatch(workflow, /provision-current-contract-inputs|canonical effect vectors|check:current-contract|check:route-manifest|check:openapi-contract-descriptor|nice -n|renice/u);
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

test("npm publication runs only after verification on pushes to main", async () => {
  const workflow = await readFile(
    new URL("../.forgejo/workflows/ci.yml", import.meta.url),
    "utf8",
  );
  const publish = jobDefinition(workflow, "publish");
  assert.match(publish, /needs: \[static, node, browser\]/u);
  assert.match(
    publish,
    /if: \$\{\{ forgejo\.event_name == 'push' && forgejo\.ref == 'refs\/heads\/main' \}\}/u,
  );
  assert.match(publish, /NPM_TOKEN: \$\{\{ secrets\.NPM_TOKEN \}\}/u);
  assert.match(publish, /npm publish artifacts\/release\/\*\.tgz --access public --ignore-scripts/u);
  assert.equal(workflow.match(/NPM_TOKEN: \$\{\{ secrets\.NPM_TOKEN \}\}/gu)?.length, 1);
});

/**
 * @param {string} workflow
 * @param {string} job
 * @returns {string}
 */
function jobDefinition(workflow, job) {
  const start = workflow.indexOf(`  ${job}:\n`);
  assert.notEqual(start, -1);
  const remaining = workflow.slice(start);
  const next = remaining.slice(1).search(/\n  [a-z][a-z0-9-]+:\n/u);
  return next === -1 ? remaining : remaining.slice(0, next + 1);
}
