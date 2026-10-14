import { readFileSync } from "node:fs";
import { createHash } from "node:crypto";
/** @type {any} Self-contained upstream conformance data, not product-boundary qualification. */
export const shapeVectors = JSON.parse(readFileSync(new URL("../fixtures/shape-vectors.json", import.meta.url), "utf8"));
export const skippedShapeVectorIds = new Set([
  "presigned-manifest", // Owner deferred manifest/302 delivery to the FR-22 follow-up.
]);
/** @param {string} group @returns {any[]} */
export function vectors(group) {
  return shapeVectors[group].filter(/** @param {any} vector */ vector => !skippedShapeVectorIds.has(vector.id));
}
export function shapeVectorDigest() {
  return createHash("sha256").update(readFileSync(new URL("../fixtures/shape-vectors.json", import.meta.url))).digest("hex");
}
