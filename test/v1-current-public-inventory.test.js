import { readFileSync } from "node:fs";
import { expect, test } from "vitest";
import { v1Routes, shapeV1Routes } from "../src/v1/routes.js";
import { managementV1Routes } from "../src/v1/management-routes.js";
import { fixture, json } from "./helpers/v1-http.js";
const contract = JSON.parse(readFileSync(new URL("../sdk-public-v1-route-manifest.json", import.meta.url), "utf8"));
const current = [...v1Routes.filter(r => r.operation !== "readShape"), ...shapeV1Routes, ...managementV1Routes];
/** @param {{method: string; path: string}} route */
const key = route => `${route.method} ${route.path}`;

test("all 84 inventoried public routes are inventoried once, including routes absent from the baseline SDK", () => {
  expect(contract.producerRevision).toBe("e7fa366ddb289f5612c93d316b316fb92c568151");
  expect(contract.routes).toHaveLength(84);
  expect(current).toHaveLength(84);
  expect(new Set(current.map(r => `${r.method} ${r.pathTemplate}`)).size).toBe(84);
  expect(current.map(r => `${r.method} ${r.pathTemplate}`).sort()).toEqual(contract.routes.map(key).sort());
  for (const route of current) {
    const expected = contract.routes.find(/** @param {{method: string; path: string}} r */ r => key(r) === `${route.method} ${route.pathTemplate}`);
    expect(expected, route.operation).toBeDefined();
    expect([route.permission, ...(route.additionalPermissions ?? [])].sort(), route.operation).toEqual([...expected.scopes].sort());
    expect([...route.requestMediaTypes].sort(), route.operation).toEqual([...expected.requestMediaTypes].sort());
    expect([...route.successStatuses].sort(), route.operation).toEqual([...expected.statuses].sort());
    expect(route.responseMediaType, route.operation).toBe(expected.responseMediaType);
  }
});

test("public scope excludes hidden aliases, internal listeners and unmapped documentation routes", () => {
  for (const route of current) {
    expect(route.pathTemplate).not.toMatch(/\/management\/|\/admin\/|^\/metrics$/u);
    expect(`${route.method} ${route.pathTemplate}`).not.toBe("PUT /v1/projects/{project}/databases/{database}");
    expect(route.pathTemplate).not.toMatch(/\/download$/u);
  }
});

test.each(["local_durable", "remote_durable", "unchanged"])("current durability label %s passes through", async durability => {
  const { client } = await fixture((_r, response) => json(response, { id: "01ARZ3NDEKTSV4RRFFQ69G5FA2", version: "tbv1-1", offset: "1", durability }));
  expect((await client.database("app").collection("users").put("01ARZ3NDEKTSV4RRFFQ69G5FA2", { name: "A" })).durability).toBe(durability);
});

test("unknown write outcomes retain region/machine metadata and are never replayed", async () => {
  const { client, requests } = await fixture((_r, response) => json(response, {
    error: { code: "write_outcome_unknown", message: "Confirmation not received", region: "gru", machine: "fixture" },
  }, 503));
  const error = await client.database("app").collection("users").put("01ARZ3NDEKTSV4RRFFQ69G5FA2", {}).catch(error => error);
  expect(error).toMatchObject({ status: 503, code: "write_outcome_unknown", retryAfter: undefined, details: { region: "gru", machine: "fixture" } });
  expect(requests).toHaveLength(1);
});
