import { readFile, readdir } from "node:fs/promises";
import { expect, test } from "vitest";
/**
 * @param {URL} directory
 * @returns {Promise<string[]>}
 */
async function sources(directory) {
  const entries = await readdir(directory, { withFileTypes: true });
  const nested = await Promise.all(
    entries.map((entry) =>
      entry.name === "local"
        ? [] // Deprecated emulator is isolated from the current product surface.
        : entry.isDirectory()
        ? sources(new URL(`${entry.name}/`, directory))
        : entry.name.endsWith(".js") && !entry.name.endsWith(".test.js")
          ? readFile(new URL(entry.name, directory), "utf8").then((value) => [
              value,
            ])
          : [],
    ),
  );
  return nested.flat();
}
test("runtime sources contain no obsolete data/product routes or transports", async () => {
  const source = (await sources(new URL("../src/", import.meta.url))).join(
    "\n",
  );
  expect(source).not.toMatch(
    /\/v1\/(?:tenants|databases)\/|\/api\/v1\/(?:databases|documents|reactive|organizations|onboarding|admin)|TerraScaleSyncV1Client|createTerraScaleClient|counter-pn|register-mv|nextUlid|bounded-msgpack/u,
  );
});
