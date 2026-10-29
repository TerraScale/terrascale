import { expect, test } from "vitest";
import { v1Routes } from "../src/v1/routes.js";
import { producerContract, verifyProducerRoutes } from "../scripts/terrabase-contract.mjs";

test("route parity catches method/path/media/idempotency drift", async () => {
  const spec = await producerContract();
  const get = v1Routes.find(route => route.operation === "getDocument");
  expect(get).toBeDefined();
  for (const change of [
    { method: "HEAD" }, { pathTemplate: "/v1/not-a-route" },
    { requestMediaTypes: ["application/xml"] }, { idempotencyKey: "optional" }, { permission: "shapes:read" },
  ]) expect(() => verifyProducerRoutes(/** @type {any} */ ([{ ...get, ...change }]), spec)).toThrow();
});
