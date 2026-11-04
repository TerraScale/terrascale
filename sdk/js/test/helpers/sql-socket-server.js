import { afterEach } from "vitest";
import { createTerraBaseClient } from "../../src/index.js";
import { startSqlSocketServer } from "./sql-socket-wire.js";
import { project, token } from "./v1-http.js";

/** @typedef {import("./sql-socket-wire.js").SqlSocketHandler} SqlSocketHandler */

/** @type {(() => Promise<void>)[]} */
const running = [];
afterEach(async () => {
  for (const stop of running.splice(0)) await stop();
});

/**
 * @param {SqlSocketHandler} handle
 * @param {{ protocol?: string | null; status?: number }} [options]
 */
export async function sqlSocketFixture(handle, options = {}) {
  const { baseUrl, upgrades, frames, closes, stop } = await startSqlSocketServer(handle, options);
  running.push(stop);
  return { client: createTerraBaseClient({ project, token, baseUrl }), upgrades, frames, closes };
}
