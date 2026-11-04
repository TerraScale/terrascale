import { readFile } from "node:fs/promises";
import { expect, test } from "vitest";
import { generateV1Manifest } from "../scripts/terrabase-contract.mjs";

test("the route manifest matches the pinned producer handler sources", async () => {
  const expected = JSON.parse(await readFile(new URL("../sdk-v1-route-manifest.json", import.meta.url), "utf8"));
  expect(await generateV1Manifest()).toEqual(expected);
});
