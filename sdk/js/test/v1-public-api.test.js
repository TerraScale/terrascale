import { expect, test } from "vitest";
import { createTerraBaseV1Client } from "../src/index.js";

const client = createTerraBaseV1Client({ project: "01ARZ3NDEKTSV4RRFFQ69G5FA1", token: "fixture.payload.signature", baseUrl: "http://127.0.0.1" });
const db = client.database("app");

test("database handles expose SQL only, without document APIs or request escapes", () => {
  for (const name of ["execute", "query", "batch", "connect", "shape"])
    expect(typeof /** @type {any} */ (db)[name]).toBe("function");
  for (const name of ["collection", "collections", "transaction", "changes", "export", "imports", "indexes", "schema", "sql", "request"])
    expect(name in db).toBe(false);
});

