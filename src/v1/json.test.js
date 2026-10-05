import { describe, expect, it } from "vitest";
import { assertJsonObject, assertJsonValue, ExactDecimal, parseExactJson, stringifyExactJson } from "./json.js";

describe("JSON input traversal budgets", () => {
  it("stops a shared binary DAG on its first traversal", () => {
    let reads = 0;
    /** @type {unknown} */
    let value = {
      get value() {
        // Bound the regression itself if validation ever expands the entire DAG again.
        if (++reads > 4096) throw new Error("Validation expanded beyond its node budget.");
        return null;
      },
    };
    for (let level = 0; level < 15; level++) value = [value, value];
    expect(() => assertJsonValue(value, "value")).toThrow("value holds at most 4096 values.");
    expect(reads).toBeLessThanOrEqual(4096);
  });

  it("rejects excess depth before reading the over-depth container", () => {
    /** @type {unknown} */
    let value = { get value() { throw new Error("Read an over-depth container."); } };
    for (let level = 0; level < 16; level++) value = { child: value };
    expect(() => assertJsonObject(value, "document")).toThrow("document nests at most 16 levels.");
  });

  it("counts 16 container levels while retaining scalar leaves at the next level", () => {
    for (const leaf of [null, true, "🦄", 1, 1000n, new ExactDecimal("12345678901234567890.123456789")]) {
      /** @type {unknown} */
      let value = leaf;
      for (let level = 0; level < 16; level++) value = level % 2 === 0 ? [value] : { child: value };
      expect(() => assertJsonObject(value, "document")).not.toThrow();
      expect(() => assertJsonValue([value], "value")).toThrow("value nests at most 16 levels.");
      expect(() => assertJsonObject({ child: value }, "document")).toThrow("document nests at most 16 levels.");
    }
  });

  it("counts the root and every serialized occurrence of shared values", () => {
    expect(() => assertJsonValue(Array(4095).fill(null), "value")).not.toThrow();
    expect(() => assertJsonValue(Array(4096).fill(null), "value")).toThrow(/4096/u);
    expect(() => assertJsonObject({ values: Array(4094).fill(null) }, "document")).not.toThrow();
    expect(() => assertJsonObject({ values: Array(4095).fill(null) }, "document")).toThrow(/4096/u);
    /** @type {unknown} */
    let shared = null;
    for (let level = 0; level < 11; level++) shared = [shared, shared];
    expect(() => assertJsonValue([shared], "value")).not.toThrow();
    expect(() => assertJsonValue([shared, null], "value")).toThrow(/4096/u);
  });

  it("keeps document budgets separate from exact-number request serialization", () => {
    const wide = { values: Array(4094).fill(null) };
    /** @type {import('./json.js').JsonInputObject} */
    let deep = { decimal: new ExactDecimal("12345678901234567890.123456789"), integer: 12345678901234567890n, nullable: null };
    for (let level = 1; level < 16; level++) deep = { child: deep };
    assertJsonObject(wide, "document");
    assertJsonObject(deep, "document");
    const body = stringifyExactJson({ ops: [{ document: wide }, { document: deep }] }, "transaction");
    expect(body).toContain('"decimal":12345678901234567890.123456789');
    expect(body).toContain('"integer":12345678901234567890');
    expect(body).toContain('"nullable":null');
    expect(parseExactJson(body)).toEqual({ ops: [{ document: wide }, { document: parseExactJson(stringifyExactJson(deep, "document")) }] });
  });
});
