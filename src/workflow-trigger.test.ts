import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { test } from "node:test";

test("develop pushes do not trigger automation", async () => {
  const workflow = await readFile(
    new URL("../.forgejo/workflows/ci.yml", import.meta.url),
    "utf8",
  );

  assert.match(workflow, /  push:\r?\n    branches: \[main\]/);
});
