import { readFile } from "node:fs/promises";
import { expect, test } from "vitest";

/**
 * @typedef {object} ManifestRoute
 * @property {string} method
 * @property {string} pathTemplate
 * @property {string} base
 * @property {string} producer
 */
test("public native route set retains all five operations and excludes obsolete HTTP SQL/reactive/product routes", async () => {
  /** @type {{ routes: { method: string; path: string }[] }} */
  const pin = JSON.parse(
    await readFile(
      new URL("../sdk-current-contract.json", import.meta.url),
      "utf8",
    ),
  );
  /** @type {{ routeCount: number; routes: ManifestRoute[] }} */
  const manifest = JSON.parse(
    await readFile(
      new URL("../sdk-route-manifest.json", import.meta.url),
      "utf8",
    ),
  );
  expect(manifest.routeCount).toBe(5);
  expect(
    manifest.routes.map((route) => ({
      method: route.method,
      path: route.pathTemplate,
    })),
  ).toEqual(pin.routes.map(({ method, path }) => ({ method, path })));
  expect(
    manifest.routes.every(
      (route) => route.base === "native" && route.producer === "terrabase",
    ),
  ).toBe(true);
  expect(JSON.stringify(manifest)).not.toMatch(
    /\/api\/v1|execute_sql|websocket/u,
  );
});
