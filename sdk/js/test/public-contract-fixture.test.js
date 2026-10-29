import { expect, test } from "vitest";
import { v1Routes } from "../src/v1/routes.js";
import { producerContract, verifyProducerRoutes } from "../scripts/terrabase-contract.mjs";

test("public SDK routes preserve current source parity and historical shape evidence", async () => {
  const spec = await producerContract();
  verifyProducerRoutes(v1Routes, spec);
  expect(v1Routes.every(route => route.pathTemplate.startsWith("/v1/projects/{project}/databases"))).toBe(true);
  expect(v1Routes.some(route => route.method === "GET" && route.pathTemplate.endsWith("/{collection}/shape"))).toBe(true);
  expect(v1Routes.some(route => /\/sse|\/shapes\//u.test(route.pathTemplate))).toBe(false);
});
