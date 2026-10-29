import { expect, test } from "vitest";
import { readFileSync } from "node:fs";
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

test.each(v1Routes.filter(route => route.operation !== "readShape"))("current $operation detects method, path, scope, media, status and retry drift", route => {
  for (const change of [{ method: "HEAD" }, { pathTemplate: "/v1/invented" }, { permission: "admin" },
    { requestMediaTypes: ["application/xml"] }, { responseMediaType: "application/problem+json" },
    { successStatuses: [202] }, { idempotencyKey: "optional" }])
    expect(() => verifyCurrentSourceRoutes(/** @type {any} */ ([{ ...route, ...change }]))).toThrow();
});

test("document IDs use the current producer canonical ULID boundary", () => {
  const directory = new URL("../contracts/terrabase-current-source/", import.meta.url);
  const route = readFileSync(new URL("src/TerraBase/Features/Documents/DocumentRoute.cs", directory), "utf8");
  const transaction = readFileSync(new URL("src/TerraBase/Features/Documents/TransactionEndpoint.cs", directory), "utf8");
  const ids = readFileSync(new URL("src/TerraBase/Primitives/EntityIds.cs", directory), "utf8");
  expect(route).toContain("EntityIds.IsValid(id)");
  expect(route).toContain('"invalid_document_id"');
  expect(transaction).toContain("DocumentRoute.CheckDocumentId(op.Id)");
  expect(ids).toContain("parsed.ToString() == id");
});
