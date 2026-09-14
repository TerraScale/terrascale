import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { test } from "vitest";

test("CI runs on main pushes, pull requests, and manual dispatch", async () => {
  const workflow = await readFile(
    new URL("../.forgejo/workflows/ci.yml", import.meta.url),
    "utf8",
  );

  assert.match(workflow, /^  push:\r?\n    branches: \[main\]$/mu);
  assert.match(workflow, /^\s+pull_request:\r?\n    branches: \[main\]$/mu);
  assert.match(workflow, /^\s+workflow_dispatch:/mu);
});

test("CI delegates to the central ts-deploy reusable workflow", async () => {
  const workflow = await readFile(
    new URL("../.forgejo/workflows/ci.yml", import.meta.url),
    "utf8",
  );

  // The whole pipeline — checkout, ts-deploy install, and
  // verification — lives in the reusable workflow this repository calls. The
  // caller owns only the job mapping, permissions, and secrets passthrough.
  assert.match(
    workflow,
    /uses:\s+TerraScale\/ts-deploy\/\.forgejo\/workflows\/deploy\.yml@main/u,
  );
  assert.match(workflow, /repo:\s+ts-typescript/u);
  assert.match(workflow, /secrets:\s+inherit/u);
  assert.doesNotMatch(workflow, /^\s*(?:container|strategy|matrix):/mu);
  assert.doesNotMatch(
    workflow,
    /^(?:\s*-?\s*run:.*\b(?:npm|make contracts|git diff --exit-code)\b|.*\b(?:check:(?:openapi|forbidden-surface|route-manifest|public-contract-fixture)|node --test|generate-(?:route-manifest|openapi))\b)/mu,
  );
  // The install step moved into the reusable
  // workflow; nothing vendored remains in this repository.
  assert.doesNotMatch(workflow, /actions\/ts-deploy/u);
});
