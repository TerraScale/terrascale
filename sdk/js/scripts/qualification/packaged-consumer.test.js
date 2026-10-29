import { readFile } from "node:fs/promises";
import { expect, test } from "vitest";
test("packaged qualification uses the actual native installed-package consumer", async () => {
  const source = await readFile(
    new URL("./packaged-consumer.js", import.meta.url),
    "utf8",
  );
  expect(source).toContain("current-producer-consumer.js");
  expect(source).not.toMatch(
    /createTerraScaleClient|TERRASCALE_API_URL|sqlite|mock/u,
  );
});
