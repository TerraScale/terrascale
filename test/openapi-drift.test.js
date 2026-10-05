import { readFile } from "node:fs/promises";
import { expect, test } from "vitest";
import { generateV1Manifest } from "../scripts/terrabase-contract.mjs";

test("manifest preserves C# methods, media, status codes and complete schema nullability", async () => {
  const expected = JSON.parse(await readFile(new URL("../sdk-v1-route-manifest.json", import.meta.url), "utf8"));
  expect(await generateV1Manifest()).toEqual(expected);
});
