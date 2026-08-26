// Guards the committed typed client against drift from the contract: the
// generator's --check mode regenerates in memory and compares.
//
// Run with: vp run test:unit

import assert from "node:assert/strict";
import test from "node:test";
import { spawnSync } from "node:child_process";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const studioRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..");

test("src/api/generated/client.ts is in sync with the contract", () => {
  const result = spawnSync(process.execPath, ["scripts/generate-client.mjs", "--check"], {
    cwd: studioRoot,
    encoding: "utf8",
  });
  assert.equal(
    result.status,
    0,
    `client out of sync or check failed:\n${result.stdout}\n${result.stderr}`,
  );
});
