import { readFile } from "node:fs/promises";
import { expect, test } from "vitest";
test("public package omits planned Better Auth and keeps legacy persistence outside browser imports", async () => {
  const manifest = JSON.parse(
    await readFile(new URL("../package.json", import.meta.url), "utf8"),
  );
  expect(manifest.exports["."].import).toBe("./src/index.js");
  expect(manifest.exports["./better-auth"]).toBeUndefined();
  expect(manifest.exports["./local"].browser).toBeNull();
  expect(manifest.exports["./local"].node).toBe("./src/local/index.js");
  expect(manifest.exports["./postgres"]).toEqual({
    types: "./types/postgres.d.ts",
    workerd: null,
    browser: null,
    node: "./src/postgres.js",
  });
  expect(manifest.peerDependencies.pg).toBe("8.23.0");
  expect(manifest.peerDependenciesMeta.pg.optional).toBe(true);
  expect(manifest.peerDependencies["@tanstack/db"]).toBe("0.9.2");
  expect(manifest.peerDependenciesMeta["@tanstack/db"].optional).toBe(true);
  expect(manifest.peerDependencies["@better-auth/core"]).toBe("1.7.2");
  expect(manifest.devDependencies["@better-auth/test-utils"]).toBe("1.7.2");
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
