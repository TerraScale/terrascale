import assert from "node:assert/strict";
import { readdir, readFile } from "node:fs/promises";
import path from "node:path";
import { test } from "vitest";
import { fileURLToPath } from "node:url";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");

test("package metadata declares the supported Node and browser matrix", async () => {
  const packageJson = JSON.parse(await readFile(path.join(repositoryRoot, "package.json"), "utf8")) as {
    readonly engines?: { readonly node?: string; readonly npm?: string };
    readonly peerDependencies?: Record<string, string>;
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
  assert.deepEqual(packageJson.peerDependencies, { react: ">=18.2.0 <20.0.0" });
  assert.deepEqual(packageJson.exports?.["."], {
    types: "./dist/index.d.ts",
    import: "./dist/index.js",
  });
  assert.deepEqual(packageJson.exports?.["./admin"], {
    types: "./dist/admin.d.ts",
    import: "./dist/admin.js",
  });
  for (const [name, target] of [
    ["./schema", "schema"],
    ["./sql", "sql"],
    ["./management", "management"],
    ["./tenants", "tenants"],
  ] as const) {
    assert.deepEqual(packageJson.exports?.[name], {
      types: `./dist/${target}.d.ts`,
      import: `./dist/${target}.js`,
    });
  }
  assert.deepEqual(packageJson.exports?.["./react"], {
    types: "./dist/react/index.d.ts",
    import: "./dist/react/index.js",
  });
  assert.deepEqual(packageJson.exports?.["./tanstack"], {
    types: "./dist/tanstack/index.d.ts",
    import: "./dist/tanstack/index.js",
  });
  assert.deepEqual(packageJson.exports?.["./better-auth"], {
    types: "./dist/better-auth.d.ts",
    node: "./dist/better-auth.js",
  });
  assert.equal("./local" in (packageJson.exports ?? {}), false);
});

test("generated route metadata pins the terrabase contract version", async () => {
  const [version, manifest] = await Promise.all([
    readFile(path.join(repositoryRoot, "CONTRACT_VERSION"), "utf8"),
    readFile(path.join(repositoryRoot, "sdk-route-manifest.json"), "utf8"),
  ]);
  const parsed = JSON.parse(manifest) as {
    readonly manifestVersion: number;
    readonly contractVersion: string;
    readonly source?: { readonly repository?: string; readonly bundle?: string };
    readonly routeCount: number;
    readonly routes: readonly { readonly pathTemplate: string }[];
  };
  assert.equal(version.trim(), "v1");
  assert.equal(parsed.manifestVersion, 2);
  assert.equal(parsed.contractVersion, version.trim());
  assert.deepEqual(parsed.source, { repository: "terrabase", bundle: "contracts", openapi: "openapi.json" });
  assert.equal(parsed.routeCount, parsed.routes.length);
  assert.equal(parsed.routes.some(({ pathTemplate }) => pathTemplate.includes("/documents")), true);
  assert.equal(parsed.routes.some(({ pathTemplate }) => pathTemplate.endsWith("/sql")), true);
  assert.equal(parsed.routes.some(({ pathTemplate }) => pathTemplate.includes("/reactive")), true);
});

test("shipped browser paths do not import Node-only modules", async () => {
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
      return entry.name === "integration" || entry.name === "better-auth" ? [] : productionSources(entryPath);
    }
    if (
      !entry.name.endsWith(".ts") ||
      entry.name.endsWith(".test.ts") ||
      entry.name === "better-auth.ts" ||
      entry.name.includes("capture")
    ) {
      return [];
    }
    return [entryPath];
  }));
  return paths.flat();
}
