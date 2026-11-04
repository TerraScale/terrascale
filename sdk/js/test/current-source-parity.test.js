import { expect, test } from "vitest";
import { currentSourcePin, verifyCurrentSourceRoutes } from "../scripts/terrabase-current-source.mjs";
import { v1Routes } from "../src/v1/routes.js";

test("the six database catalog routes match exact producer handlers", () => {
  expect(currentSourcePin.revision).toBe("0d2582d1d6cc73972b49f6b6089b6f9ed0848da9");
  expect(Object.keys(currentSourcePin.routes).sort()).toEqual(v1Routes.map(route => route.operation).sort());
  const evidence = verifyCurrentSourceRoutes(v1Routes);
  expect(evidence).toHaveLength(6);
  expect(evidence.find(route => route.operation === "deleteDatabase")).toMatchObject({ successStatuses: [204], responseMediaType: "" });
  const get = /** @type {import("../src/v1/routes.js").V1Route} */ (v1Routes.find(route => route.operation === "getDatabase"));
  for (const change of [{ method: "HEAD" }, { pathTemplate: "/v1/not-a-route" }, { requestMediaTypes: ["application/xml"] }, { permission: "databases:write" }])
    expect(() => verifyCurrentSourceRoutes(/** @type {any} */ ([{ ...get, ...change }]))).toThrow();
});
