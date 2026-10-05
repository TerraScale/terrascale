// Test setup only: resolve routes from producerContract()'s verified pinned OpenAPI.
import assert from "node:assert/strict";

// Pinned producer discovery endpoint; fetching OpenAPI cannot depend on that schema.
export const producerBootstrapEndpoints = Object.freeze({ openapi: "/openapi/v1.json" });

/** @param {import("../../scripts/terrabase-contract.mjs").ProducerOpenApi} contract @param {string} operationId @param {Readonly<Record<string, string>>} [params] */
export function provisioningRoute(contract, operationId, params = {}) {
  for (const [template, methods] of Object.entries(contract.paths)) {
    for (const [method, operation] of Object.entries(methods)) {
      if (operation.operationId !== operationId) continue;
      const path = template.replace(/\{([^}]+)\}/gu, (_match, name) => {
        const value = params[name];
        assert.ok(typeof value === "string" && value.trim() !== "", `${operationId}: missing path parameter ${name}.`);
        return encodeURIComponent(value);
      });
      return { method: method.toUpperCase(), path };
    }
  }
  assert.fail(`Pinned OpenAPI has no provisioning operation ${operationId}.`);
}
