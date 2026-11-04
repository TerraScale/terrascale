import { expect, test } from "vitest";
import { createTerraBaseV1Client, V1Databases } from "../src/index.js";

const client = createTerraBaseV1Client({ project: "01ARZ3NDEKTSV4RRFFQ69G5FA1", token: "fixture.payload.signature", baseUrl: "http://127.0.0.1" });
const db = client.database("app");
const docs = db.collection("users");

test("root export exposes current SQL and SSE without consumer request escapes", () => {
  for (const name of ["sql", "checkSql", "explainSql", "changes"])
    expect(typeof /** @type {any} */ (db)[name]).toBe("function");
  expect("request" in db).toBe(false);
});

test("root export exposes typed project databases and document/schema operations", () => {
  expect(client.databases).toBeInstanceOf(V1Databases);
  for (const name of ["create", "list", "get", "update", "delete", "branch"])
    expect(typeof /** @type {any} */ (client.databases)[name]).toBe("function");
  for (const name of ["list", "getSchema", "putSchema", "deleteSchema"])
    expect(typeof /** @type {any} */ (docs)[name]).toBe("function");
  expect(JSON.stringify(client)).toBe("{}");
});
