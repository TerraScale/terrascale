import { expect, test } from "vitest";
import { currentSourcePin, verifyCurrentSourceRoutes } from "../scripts/terrabase-current-source.mjs";
import { v1Routes } from "../src/v1/routes.js";

test("25 current APIs match exact producer handlers without claiming historical shape support", () => {
  const current = v1Routes.filter(route => route.operation !== "readShape");
  expect(currentSourcePin.revision).toBe("e30ffdc287530551b161b0130220be53d3c0475e");
  expect(Object.keys(currentSourcePin.routes).sort()).toEqual(current.map(route => route.operation).sort());
  const evidence = verifyCurrentSourceRoutes(current);
  expect(evidence).toHaveLength(25);
  expect(evidence.find(route => route.operation === "explainSql")).toMatchObject({
    authorizationPolicy: "UserAuthExtensions.PolicyName", permission: "databases:read",
  });
  expect(evidence.find(route => route.operation === "deleteDatabase")).toMatchObject({ successStatuses: [204], responseMediaType: "" });
  expect(() => verifyCurrentSourceRoutes(v1Routes)).toThrow(/historical shape/u);
});

test.each(v1Routes.filter(route => route.operation !== "readShape"))("current $operation detects method, path, scope, media, status and retry drift", route => {
  for (const change of [{ method: "HEAD" }, { pathTemplate: "/v1/invented" }, { permission: "admin" },
    { requestMediaTypes: ["application/xml"] }, { responseMediaType: "application/problem+json" },
    { successStatuses: [202] }, { idempotencyKey: "optional" }])
    expect(() => verifyCurrentSourceRoutes(/** @type {any} */ ([{ ...route, ...change }]))).toThrow();
});
