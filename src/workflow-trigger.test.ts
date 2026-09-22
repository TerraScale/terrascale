import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { test } from "vitest";

test("develop and main pushes run native trunk verification", async () => {
  const workflow = await readFile(
    new URL("../.forgejo/workflows/ci.yml", import.meta.url),
    "utf8",
  );

  assert.match(workflow, /^  push:\r?\n    branches: \[develop, main\]$/mu);
  assert.match(workflow, /^  workflow_dispatch:\r?$/mu);
  assert.match(workflow, /^permissions:\r?\n  contents: read$/mu);
  assert.doesNotMatch(
    workflow,
    /pull_request|schedule|workflow_call/u,
  );
  assert.doesNotMatch(workflow, /(?:ts-deploy|TERRASCALE_DEPLOY|NPM_TOKEN|TERRASCALE_E2E_)/u);
  assert.match(workflow, /^  CONTRACT_SOURCE: contracts\/terrabase$/mu);
  assert.match(workflow, /^  EDGE_CONTRACT_SOURCE: contracts\/edge$/mu);

  const jobs = ["static", "node", "browser"];
  for (const job of jobs) {
    const definition = jobDefinition(workflow, job);
    assert.match(definition, /runs-on: terrascale-builder/u);
    assert.match(definition, /timeout-minutes: (?:45|60)/u);
    assert.doesNotMatch(definition, /container:\s*\n\s+image:/u);
    assert.match(definition, /actions\/checkout@v6\.1\.0/u);
    assert.match(definition, /ref: \$\{\{ forgejo\.sha \}\}/u);
    assert.match(definition, /token: "\$\{\{ secrets\.REGISTRY_TOKEN \}\}"/u);
    assert.match(definition, /persist-credentials: false/u);
  }

  const staticJob = jobDefinition(workflow, "static");
  for (const command of [
    "npm run typecheck",
    "npm run build",
    "npm run check:public-contract-fixture",
    "npm run check:openapi-contract-descriptor",
    "npm run check:route-manifest",
    "npm run check:forbidden-surface",
  ]) {
    assert.match(staticJob, new RegExp(escapeRegExp(command), "u"));
  }
  for (const job of [staticJob, jobDefinition(workflow, "node")]) {
    assert.match(job, /repository: TerraScale\/ts-rust/u);
    assert.match(job, /repository: TerraScale\/ts-atlas/u);
  }

  const nodeJob = jobDefinition(workflow, "node");
  assert.match(
    nodeJob,
    /strategy:\r?\n      fail-fast: false\r?\n      matrix:/u,
  );
  assert.match(nodeJob, /node: \["22\.23\.2", "24\.20\.0"\]/u);
  assert.match(nodeJob, /Install Node\.js \$\{\{ matrix\.node \}\}/u);
  assert.match(nodeJob, /sha256sum --check --strict/u);
  assert.match(
    nodeJob,
    /test "\$\(node --version\)" = "v\$\{\{ matrix\.node \}\}"/u,
  );
  assert.match(nodeJob, /architecture: process\.arch/u);
  assert.match(nodeJob, /uname -m/u);
  assert.match(nodeJob, /npm run build/u);
  assert.match(nodeJob, /npm test/u);

  const browserJob = jobDefinition(workflow, "browser");
  assert.match(browserJob, /Install the repository Node\.js version/u);
  assert.match(browserJob, /test "\$\(node --version\)" = v24\.20\.0/u);
  assert.match(browserJob, /architecture: process\.arch/u);
  assert.match(browserJob, /uname -m/u);
  assert.match(browserJob, /npm run build/u);
  assert.match(browserJob, /npx playwright install chromium firefox webkit/u);
  assert.match(browserJob, /npm run test:browser/u);

  assert.doesNotMatch(
    workflow,
    /(?:candidate|admit|publish|promote-npm-candidate)/iu,
  );
});

function jobDefinition(workflow: string, job: string): string {
  const start = workflow.indexOf(`  ${job}:\n`);
  assert.notEqual(start, -1, `Workflow job ${job} is missing.`);
  const remaining = workflow.slice(start);
  const next = remaining.slice(1).search(/\n  [a-z][a-z0-9-]+:\n/u);
  return next === -1 ? remaining : remaining.slice(0, next + 1);
}

function escapeRegExp(value: string): string {
  return value.replace(/[.*+?^${}()|[\]\\]/gu, "\\$&");
}
