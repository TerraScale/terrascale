import { readFile } from "node:fs/promises";
import { expect, test } from "vitest";
test("public native route set retains all five operations and excludes obsolete HTTP SQL/reactive/product routes", async () => {
  const pin = JSON.parse(
    await readFile(
      new URL("../sdk-current-contract.json", import.meta.url),
      "utf8",
    ),
  );
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
