import { expect, test } from "vitest";
import { currentSourcePin, verifyCurrentSourceRoutes } from "../scripts/terrabase-current-source.mjs";
import { v1Routes } from "../src/v1/routes.js";

test("25 current APIs match exact producer handlers without claiming historical shape support", () => {
  const current = v1Routes.filter(route => route.operation !== "readShape");
  expect(currentSourcePin.revision).toBe("0d2582d1d6cc73972b49f6b6089b6f9ed0848da9");
  expect(Object.keys(currentSourcePin.routes).sort()).toEqual(current.map(route => route.operation).sort());
  const evidence = verifyCurrentSourceRoutes(current);
  expect(evidence).toHaveLength(25);
  expect(evidence.find(route => route.operation === "explainSql")).toMatchObject({
    authorizationPolicy: "UserAuthExtensions.PolicyName", permission: "databases:read",
  });
  expect(evidence.find(route => route.operation === "deleteDatabase")).toMatchObject({ successStatuses: [204], responseMediaType: "" });
  expect(() => verifyCurrentSourceRoutes(v1Routes)).toThrow(/historical shape/u);
});

