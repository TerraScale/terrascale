import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import path from "node:path";
import { test } from "vitest";
import { fileURLToPath } from "node:url";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../..");

test("the static CI profile runs the Vitest suite against the cloned Dashboard snapshot", async () => {
  const workflow = await readFile(path.join(repositoryRoot, ".forgejo", "workflows", "ci.yml"), "utf8");

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
