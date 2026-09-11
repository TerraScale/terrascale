import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { test } from "vitest";

test("D-68 CI runs only on develop pushes", async () => {
  const workflow = await readFile(
    new URL("../.forgejo/workflows/ci.yml", import.meta.url),
    "utf8",
  );

  assert.match(workflow, /^  push:\r?\n    branches: \[develop\]$/mu);
  assert.doesNotMatch(workflow, /^\s+pull_request:/mu);
  assert.doesNotMatch(workflow, /^\s+(?:workflow_dispatch|schedule|workflow_call):/mu);
});

test("D-68 CI checks out the event revision and delegates to ts-deploy", async () => {
  const workflow = await readFile(
    new URL("../.forgejo/workflows/ci.yml", import.meta.url),
    "utf8",
  );
  const deployAction = await readFile(
    new URL("../.forgejo/actions/ts-deploy/action.yml", import.meta.url),
    "utf8",
  );

  assert.match(workflow, /uses:\s+https:\/\/data\.forgejo\.org\/actions\/checkout@[0-9a-f]{40}\b/u);
  assert.match(workflow, /ref:\s+\$\{\{\s*forgejo\.sha\s*\}\}/u);
  assert.match(workflow, /fetch-depth:\s*0\b/u);
  assert.match(workflow, /persist-credentials:\s*false\b/u);
  assert.match(workflow, /runs-on:\s*terrascale-builder\b/u);
  assert.match(workflow, /TS_DEPLOY_VERSION:\s*\$\{\{\s*vars\.TS_DEPLOY_VERSION\s*\}\}/u);
  assert.match(workflow, /uses:\s+\.\/\.forgejo\/actions\/ts-deploy\b/u);
  assert.match(workflow, /TS_DEPLOY_SHA256:\s*\$\{\{\s*vars\.TS_DEPLOY_SHA256\s*\}\}/u);
  assert.match(workflow, /TS_DEPLOY_SIGNATURE_SHA256:\s*\$\{\{\s*vars\.TS_DEPLOY_SIGNATURE_SHA256\s*\}\}/u);
  assert.match(deployAction, /case "\$\(uname -m\)" in[\s\S]*aarch64\|arm64\) asset=ts-deploy-linux-arm64[\s\S]*x86_64\|amd64\) asset=ts-deploy-linux-amd64/u);
  assert.match(deployAction, /release="\$\{TS_DEPLOY_VERSION:\?TS_DEPLOY_VERSION is required\}"/u);
  assert.match(deployAction, /case "\$\{release\}" in[\s\S]*v\[0-9\]\*\.\[0-9\]\*\.\[0-9\]\*\)/u);
  assert.match(deployAction, /fj release view "\$\{release\}" --repo TerraScale\/ts-deploy/u);
  assert.match(deployAction, /fj release asset download "\$\{release\}" "\$\{asset\}" --repo TerraScale\/ts-deploy --output/u);
  assert.match(deployAction, /fj release asset download "\$\{release\}" "\$\{asset\}\.sig" --repo TerraScale\/ts-deploy --output/u);
  assert.match(deployAction, /TS_DEPLOY_SHA256.*sha256sum --check --strict/u);
  assert.match(deployAction, /TS_DEPLOY_SIGNATURE_SHA256.*sha256sum --check --strict/u);
  assert.match(deployAction, /chmod \+x/u);

  assert.match(deployAction, /exec "\$\{TS_DEPLOY_CLI\}" ci --repo "\$\{TS_DEPLOY_REPO\}"/u);
  assert.doesNotMatch(workflow, /^\s*(?:container|strategy|matrix):/mu);
  assert.doesNotMatch(
    workflow,
    /^(?:\s*-?\s*run:.*\b(?:npm|make contracts|git diff --exit-code)\b|.*\b(?:check:(?:openapi|forbidden-surface|route-manifest|public-contract-fixture)|node --test|generate-(?:route-manifest|openapi))\b)/mu,
  );
});
