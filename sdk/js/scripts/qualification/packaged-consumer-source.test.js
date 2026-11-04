import { test } from "vitest";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
test("qualification runner imports the public installed native SDK", async () => {
  const source = await readFile(new URL("./current-producer-consumer.js", import.meta.url), "utf8");
  assert.match(source, /from "@terrascale\/sdk"/u);
  assert.doesNotMatch(source, /createTerraScaleClient|TERRASCALE_API_URL|sqlite/u);
});
