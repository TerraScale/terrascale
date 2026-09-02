import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { test } from "vitest";

test("develop pushes do not trigger automation", async () => {
  const workflow = await readFile(
    new URL("../.forgejo/workflows/ci.yml", import.meta.url),
    "utf8",
  );

  assert.match(workflow, /  push:\r?\n    branches: \[main\]/);
  assert.match(workflow, /release-contract-fixture:[\s\S]*path: Dashboard/);
  assert.match(workflow, /release-contract-fixture:[\s\S]*npm test/);
  assert.match(workflow, /candidate:[\s\S]*release-contract-fixture/);
});
