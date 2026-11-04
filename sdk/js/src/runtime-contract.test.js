import { readFile } from "node:fs/promises";
import { expect, test } from "vitest";
test("public package omits planned Better Auth and retired entrypoints", async () => {
  const manifest = JSON.parse(
    await readFile(new URL("../package.json", import.meta.url), "utf8"),
  );
  expect(manifest.exports["."].import).toBe("./src/index.js");
  for (const name of ["better-auth", "local", "postgres", "schema", "sql", "tanstack"])
    expect(manifest.exports[`./${name}`]).toBeUndefined();
  expect(manifest.bin["terrascale-local"]).toBeUndefined();
  for (const name of ["pg", "@tanstack/db", "@better-auth/core"])
    expect(manifest.peerDependencies[name]).toBeUndefined();
  expect(manifest.exports["./codegen"]).toEqual({ types: "./types/codegen.d.ts", browser: null, node: "./src/codegen.js" });
  expect(manifest.peerDependencies["json-schema-to-typescript"]).toBe("16.0.0");
  expect(manifest.peerDependenciesMeta["json-schema-to-typescript"].optional).toBe(true);
  expect(manifest.version).toMatch(/^0\.0\.[0-9]+$/u);
  const lockfile = JSON.parse(
    await readFile(new URL("../package-lock.json", import.meta.url), "utf8"),
  );
  expect(lockfile.version).toBe(manifest.version);
  expect(lockfile.packages[""].version).toBe(manifest.version);
});
