import { readFile } from "node:fs/promises";
import { createHash } from "node:crypto";
import { describe, expect, test } from "vitest";
import { decodeDatabaseAuthority, decodeDatabaseValue, encodeDatabaseCommit } from "../src/database-codec.js";

/**
 * @typedef {object} VectorWrite
 * @property {string} schema_generation
 * @property {{ sequence: string }[]} observed
 * @property {{ kind: "tombstone" } | { kind: "live"; fields: { id: number; value: unknown }[] }} value
 */

const source = process.env.TERRASCALE_CURRENT_OPENAPI;
const vectors = process.env.TERRASCALE_ATOMIC_VECTORS;

describe("current producer OpenAPI parity", () => {
  test.skipIf(source === undefined)("actual owning producer supplies every admitted route and atomic field", async () => {
    const bytes = await readFile(/** @type {string} */ (source));
    const spec = JSON.parse(bytes.toString("utf8"));
    const pin = JSON.parse(await readFile(new URL("../sdk-current-contract.json", import.meta.url), "utf8"));
    expect(createHash("sha256").update(bytes).digest("hex")).toBe(pin.sourceOpenApiSha256);
    for (const route of pin.routes) {
      const operation = spec.paths[route.path]?.[route.method.toLowerCase()];
      expect(operation, route.path).toBeDefined();
      if (route.request !== undefined) expect(operation.requestBody.content["application/json"].schema.$ref).toBe(`#/components/schemas/${route.request}`);
      expect(operation.responses["200"].content["application/json"].schema.$ref).toBe(`#/components/schemas/${route.response}`);
    }
    expect(spec.components.schemas.PointResponse.required).toContain("state_version");
    for (const field of ["effect_digest", "authority"]) {
      expect(spec.components.schemas.CommitRequest.required).toContain(field);
      expect(spec.components.schemas.CommitResponse.required).toContain(field);
    }
    expect(spec.components.schemas.Write.required).toContain("condition");
    expect(spec.components.schemas.Error.properties.error.properties.code.enum).toEqual(expect.arrayContaining(["precondition_failed", "authority_changed", "authority_unavailable"]));
  });

  test.skipIf(vectors === undefined)("matches all producer canonical effect vectors byte-for-byte through SHA-256", async () => {
    const bytes = await readFile(/** @type {string} */ (vectors));
    const corpus = JSON.parse(bytes.toString("utf8"));
    const pin = JSON.parse(await readFile(new URL("../sdk-current-contract.json", import.meta.url), "utf8"));
    expect(createHash("sha256").update(bytes).digest("hex")).toBe(pin.sourceAtomicVectorsSha256);
    expect(corpus.vectors).toHaveLength(3);
    for (const vector of corpus.vectors) {
      const expected = vector.request;
      const commit = {
        transaction_id: { ...expected.transaction_id, expires_at_unix_ms: BigInt(expected.transaction_id.expires_at_unix_ms) },
        authority: decodeDatabaseAuthority(expected.authority),
        writes: expected.writes.map((/** @type {VectorWrite} */ write) => ({
          ...write, schema_generation: BigInt(write.schema_generation),
          observed: write.observed.map(origin => ({ ...origin, sequence: BigInt(origin.sequence) })),
          value: write.value.kind === "tombstone" ? { kind: "tombstone" } : {
            kind: "live", fields: write.value.fields.map(field => ({ id: field.id, value: decodeDatabaseValue(field.value) })),
          },
        })),
      };
      const encoded = await encodeDatabaseCommit({ ...vector.scope, shard: BigInt(vector.scope.shard) }, commit);
      expect(createHash("sha256").update(Buffer.from(vector.canonical_effect_hex, "hex")).digest("hex")).toBe(vector.effect_digest);
      expect(encoded.effect_digest, vector.name).toBe(vector.effect_digest);
      expect(JSON.parse(encoded.body), vector.name).toEqual(expected);
    }
  });
});
