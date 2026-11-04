import { expect, test } from "vitest";
import { createTerraBaseV1Client } from "../src/index.js";

const client = createTerraBaseV1Client({ project: "01ARZ3NDEKTSV4RRFFQ69G5FA1", token: "fixture.payload.signature", baseUrl: "http://127.0.0.1" });
const db = client.database("app");

test("root export exposes current SQL and SSE without consumer request escapes", () => {
  for (const name of ["sql", "checkSql", "explainSql", "changes"])
    expect(typeof /** @type {any} */ (db)[name]).toBe("function");
  expect("request" in db).toBe(false);
});

