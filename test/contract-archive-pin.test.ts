import { strict as assert } from "node:assert";
import { readFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { test as vitestTest } from "vitest";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");

vitestTest("contract archive pin is the producer handoff", async () => {
  const pin = (await readFile(path.join(root, "CONTRACT_ARCHIVE_SHA256"), "utf8")).trim();
  assert.match(pin, /^[0-9a-f]{64}$/u);
  assert.equal(pin, "e7a8bcbe752d814006c52fd40df055d54ac9adb7e3c1e5050630951d8f3498af");
});
