import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { test } from "vitest";

test("develop and main pushes run the current verification and promotion workflow", async () => {
  const workflow = await readFile(
    new URL("../.forgejo/workflows/ci.yml", import.meta.url),
    "utf8",
  );

  assert.match(workflow, /  push:\r?\n    branches: \[develop, main\]/);
  assert.doesNotMatch(workflow, /pull_request|dotnet|nuget|image:.*terrascale-builder:[^\s]+/iu);
  assert.match(workflow, /repository: TerraScale-Old\/TerraScale\.Deploy/u);
  assert.match(workflow, /ref: 9a39633f88b085a6d40090ab451b1d71c89e3712/u);
  assert.match(workflow, /TERRASCALE_SOURCE_READ_TOKEN/u);
  for (const job of ["static", "build", "tests", "browser"]) {
    assert.match(workflow, new RegExp(`  ${job}:\\n`, "u"));
    assert.match(jobDefinition(workflow, job), /needs: bootstrap/u);
    assert.match(jobDefinition(workflow, job), /runs-on: terrascale-untrusted/u);
    assert.match(jobDefinition(workflow, job), /image: mcr\.microsoft\.com\/playwright@sha256:dcc5531e97840b9b5e794f2814476b21571c5124a3fca2267d73041f56e7580e/u);
    assert.match(jobDefinition(workflow, job), /node scripts\/provision-reviewed-npm\.mjs/u);
    assert.match(jobDefinition(workflow, job), /npm install/u);
  }
  assert.match(jobDefinition(workflow, "static"), /repository: TerraScale\/ts-dashboard/u);
  assert.match(jobDefinition(workflow, "static"), /ref: develop/u);
  assert.match(jobDefinition(workflow, "static"), /path: Dashboard/u);
  assert.match(jobDefinition(workflow, "static"), /npm run typecheck/u);
  assert.match(jobDefinition(workflow, "static"), /npm run build/u);
  assert.match(jobDefinition(workflow, "static"), /npm run check:openapi-drift/u);
  assert.match(jobDefinition(workflow, "static"), /npm run check:route-manifest/u);
  assert.match(jobDefinition(workflow, "static"), /npm run check:forbidden-surface/u);
  assert.match(jobDefinition(workflow, "tests"), /npm test/u);
  assert.match(jobDefinition(workflow, "browser"), /playwright@sha256:dcc5531e97840b9b5e794f2814476b21571c5124a3fca2267d73041f56e7580e/u);
  assert.match(jobDefinition(workflow, "browser"), /npm run test:browser/u);

  assert.match(jobDefinition(workflow, "candidate"), /if: \$\{\{ forgejo\.event_name == 'push' && forgejo\.ref == 'refs\/heads\/main' \}\}/u);
  assert.match(jobDefinition(workflow, "candidate"), /needs: \[bootstrap, static, build, tests, browser\]/u);
  assert.match(jobDefinition(workflow, "candidate"), /ci build-candidate[\s\S]*--target npm/u);
  assert.match(jobDefinition(workflow, "candidate"), /name: candidate-npm/u);
  assert.match(jobDefinition(workflow, "browser-candidate"), /TERRASCALE_BROWSER_CANDIDATE_TARBALL/u);
  assert.match(jobDefinition(workflow, "browser-candidate"), /npm run test:browser/u);
  const publish = jobDefinition(workflow, "publish");
  assert.match(publish, /needs: \[browser-candidate\]/u);
  assert.match(publish, /NPM_TOKEN: \$\{\{ secrets\.NPM_TOKEN \}\}/u);
  assert.equal((workflow.match(/NPM_TOKEN/gu) ?? []).length, 2);
  for (const job of ["static", "build", "tests", "browser", "candidate", "browser-candidate"]) {
    assert.match(jobDefinition(workflow, job), /image: mcr\.microsoft\.com\/playwright@sha256:dcc5531e97840b9b5e794f2814476b21571c5124a3fca2267d73041f56e7580e/u);
    assert.doesNotMatch(jobDefinition(workflow, job), /(?:NPM_TOKEN|REGISTRY_(?:USER|TOKEN)|TERRASCALE_E2E_\w+)/u);
  }
});

function jobDefinition(workflow: string, job: string): string {
  const start = workflow.indexOf(`  ${job}:\n`);
  assert.notEqual(start, -1, `Workflow job ${job} is missing.`);
  const remaining = workflow.slice(start);
  const next = remaining.slice(1).search(/\n  [a-z][a-z0-9-]+:\n/u);
  return next === -1 ? remaining : remaining.slice(0, next + 1);
}
