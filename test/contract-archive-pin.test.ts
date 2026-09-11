import { strict as assert } from "node:assert";
import { readFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { test as vitestTest } from "vitest";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");

vitestTest("contract archive pin is the producer handoff", async () => {
  const pin = (await readFile(path.join(root, "CONTRACT_ARCHIVE_SHA256"), "utf8")).trim();
  assert.match(pin, /^[0-9a-f]{64}$/u);
  assert.equal(pin, "b0b0297f3facf5c0ed9cc17b86a6cec477db4babad47ac9a62ce7a2e2134502b");
});
