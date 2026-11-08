import { readFileSync } from "node:fs";
import { expect, test } from "vitest";
import { v1Routes } from "../src/v1/routes.js";
import { managementV1Routes } from "../src/v1/management-routes.js";
import { fixture, json } from "./helpers/v1-http.js";
const contract = JSON.parse(readFileSync(new URL("../sdk-public-v1-route-manifest.json", import.meta.url), "utf8"));
const current = [...v1Routes, ...managementV1Routes];
/** @param {{method: string; path: string}} route */
const key = route => `${route.method} ${route.path}`;

test("all 39 inventoried public routes are inventoried once, including routes absent from the baseline SDK", () => {
  expect(contract.producerRevision).toBe("667e7e31c38600b60289a3e82326796ae8cdfb92");
  expect(contract.routes).toHaveLength(39);
  expect(current).toHaveLength(39);
  expect(new Set(current.map(r => `${r.method} ${r.pathTemplate}`)).size).toBe(39);
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

test("unknown write outcomes retain region/machine metadata and are never replayed", async () => {
  const { client, requests } = await fixture((_r, response) => json(response, {
    error: { code: "write_outcome_unknown", message: "Confirmation not received", region: "gru", machine: "fixture" },
  }, 503));
  const error = await client.database("app").execute("INSERT INTO users (id) VALUES (1)").catch(/** @param {unknown} error */ error => error);
  expect(error).toMatchObject({ status: 503, code: "write_outcome_unknown", retryAfter: undefined, details: { region: "gru", machine: "fixture" } });
  expect(requests).toHaveLength(1);
});
