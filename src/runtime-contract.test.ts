import assert from "node:assert/strict";
import { readdir, readFile } from "node:fs/promises";
import path from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");

test("package metadata declares the supported Node and browser matrix", async () => {
  const packageJson = JSON.parse(await readFile(path.join(repositoryRoot, "package.json"), "utf8")) as {
    readonly engines?: { readonly node?: string; readonly npm?: string };
    readonly browserslist?: { readonly production?: readonly string[] };
    readonly exports?: Record<string, {
      readonly types?: string;
      readonly import?: string;
      readonly node?: string;
    }>;
  };

  assert.deepEqual(packageJson.engines, { node: "22.x || 24.x", npm: ">=11.0.0" });
  assert.deepEqual(packageJson.browserslist?.production, [
    "last 1 Chrome major version",
    "last 1 Firefox major version",
    "last 1 Safari major version",
    "last 1 Edge major version",
  ]);
  assert.deepEqual(packageJson.exports?.["."], {
    types: "./dist/index.d.ts",
    import: "./dist/index.js",
  });
  assert.deepEqual(packageJson.exports?.["./admin"], {
    types: "./dist/admin.d.ts",
    import: "./dist/admin.js",
  });
  assert.deepEqual(packageJson.exports?.["./local"], {
    types: "./dist/local/index.d.ts",
    node: "./dist/local/index.js",
  });
});

test("shipped browser paths do not import Node-only modules or the local emulator", async () => {
  const sources = await productionSources(path.join(repositoryRoot, "src"));
  assert.notEqual(sources.length, 0);

  for (const sourcePath of sources) {
    const source = await readFile(sourcePath, "utf8");
    assert.doesNotMatch(source, /(?:from\s+|import\s*\(|require\s*\()\s*["']node:/u, sourcePath);
    assert.doesNotMatch(source, /(?:from\s+|import\s*\()\s*["'][^"']*\/local(?:\/|["'])/u, sourcePath);
  }
});

test("browser automation names every supported browser family", async () => {
  const config = await readFile(path.join(repositoryRoot, "playwright.config.ts"), "utf8");
  for (const project of ["chrome-current", "firefox-current", "safari-current", "edge-current"]) {
    assert.match(config, new RegExp(`name: ["']${project}["']`, "u"));
  }
});

test("public guidance records the runtime and API compatibility policy", async () => {
  const readme = await readFile(path.join(repositoryRoot, "README.md"), "utf8");
  assert.match(readme, /Node\.js 22 and 24/u);
  assert.match(readme, /current stable major versions of Chrome, Firefox, Safari, and Edge/u);
  assert.match(readme, /public API `\/api\/v1`/u);
  assert.match(readme, /exact packed tarball/u);
});

async function productionSources(directory: string): Promise<string[]> {
  const entries = await readdir(directory, { withFileTypes: true });
  const paths = await Promise.all(entries.map(async (entry) => {
    const entryPath = path.join(directory, entry.name);
    if (entry.isDirectory()) {
      return entry.name === "integration" || entry.name === "local" ? [] : productionSources(entryPath);
    }
    if (!entry.name.endsWith(".ts") || entry.name.endsWith(".test.ts") || entry.name.includes("capture")) {
      return [];
    }
    return [entryPath];
  }));
  return paths.flat();
}
