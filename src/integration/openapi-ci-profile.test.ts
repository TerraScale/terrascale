import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import path from "node:path";
import { test } from "vitest";
import { fileURLToPath } from "node:url";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../..");

test("the CI workflow delegates contract and verification gates to ts-deploy", async () => {
  const workflow = await readFile(path.join(repositoryRoot, ".forgejo", "workflows", "ci.yml"), "utf8");

  assert.match(workflow, /^  push:\r?\n    branches: \[develop\]$/mu);
  assert.doesNotMatch(workflow, /^\s+pull_request:/mu);
  assert.match(workflow, /uses:\s+https:\/\/data\.forgejo\.org\/actions\/checkout@[0-9a-f]{40}\b/u);
  assert.match(workflow, /ref:\s+\$\{\{\s*forgejo\.sha\s*\}\}/u);
  assert.match(workflow, /fetch-depth:\s*0\b/u);
  assert.match(workflow, /persist-credentials:\s*false\b/u);
  assert.doesNotMatch(workflow, /^\s*(?:container|strategy|matrix):/mu);
  assert.doesNotMatch(
    workflow,
    /^(?:\s*-?\s*run:.*\b(?:npm|make contracts|git diff --exit-code)\b|.*\b(?:check:(?:openapi|forbidden-surface|route-manifest|public-contract-fixture)|node --test|generate-(?:route-manifest|openapi))\b)/mu,
  );
  assert.doesNotMatch(workflow, /ts-dashboard\.git|path:\s*Dashboard/u);

  assert.match(workflow, /uses:\s+\.\/\.forgejo\/actions\/ts-deploy\b/u);
  assert.match(workflow, /with:\s*\r?\n\s+repo:\s+ts-typescript\b/u);
});
