import { readFile } from "node:fs/promises";
import { expect, test } from "vitest";
test("public package keeps Node persistence/SQL and Better Auth server conditions separate from browser imports", async () => {
  const manifest = JSON.parse(
    await readFile(new URL("../package.json", import.meta.url), "utf8"),
  );
  expect(manifest.exports["."].import).toBe("./src/index.js");
  expect(manifest.exports["./better-auth"].browser).toBeNull();
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
  expect(manifest.version).toBe("0.0.2");
  const lockfile = JSON.parse(
    await readFile(new URL("../package-lock.json", import.meta.url), "utf8"),
  );
  expect(lockfile.version).toBe(manifest.version);
  expect(lockfile.packages[""].version).toBe(manifest.version);
});
