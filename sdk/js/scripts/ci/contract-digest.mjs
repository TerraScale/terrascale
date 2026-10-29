import { createHash } from "node:crypto";
// TerraBase generates servers from each request's ephemeral admin origin.
// Exclude only that environment field; preserve every other contract field.
/** @param {Buffer | string} bytes @returns {string} */
export function openapiDigest(bytes) {
  const document = JSON.parse(bytes.toString());
  delete document.servers;
  return createHash("sha256").update(JSON.stringify(document)).digest("hex");
}
