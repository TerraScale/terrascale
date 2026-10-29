// Optional HTTP qualification uses an independently configured service; this repo never starts TerraBase.
import { createTerraBaseV1Client } from "../../src/index.js";
import { database } from "./terrabase-v1-fixture.js";
const names = ["TB_V1_URL", "TB_V1_PROJECT", "TB_V1_DATA_KEY"];
export const liveConfigured = names.every(name => Boolean(process.env[name]));
if ((process.env.TB_V1_REQUIRED === "1" || names.some(name => process.env[name])) && !liveConfigured)
  throw new Error(`Real C# integration requires ${names.join(", ")}.`);
if (process.env.TB_V1_REQUIRED === "1" && (!process.env.TB_V1_READ_KEY || !process.env.TB_V1_WRITE_KEY))
  throw new Error("Required real integration needs read-only and write-only keys for denial checks.");
/** @param {string} [token] */
export function liveClient(token = process.env.TB_V1_DATA_KEY) {
  if (!liveConfigured || !token) throw new Error("Real C# integration is not configured.");
  return createTerraBaseV1Client({ baseUrl: /** @type {string} */ (process.env.TB_V1_URL), project: /** @type {string} */ (process.env.TB_V1_PROJECT), token });
}
/** @param {string} [token] */
export function liveDatabase(token) {
  return liveClient(token).database(process.env.TB_V1_DATABASE ?? database);
}
