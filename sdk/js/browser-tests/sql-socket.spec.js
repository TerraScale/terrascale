import { expect, test } from "@playwright/test";
import { startSqlSocketServer } from "../test/helpers/sql-socket-wire.js";

const project = "01ARZ3NDEKTSV4RRFFQ69G5FA1";
const token = "fixture.payload.signature";
/** @type {Awaited<ReturnType<typeof startSqlSocketServer>>} */
let server;

test.beforeAll(async () => {
  server = await startSqlSocketServer((frame, peer) => {
    if (frame.type === "auth") {
      peer.send({ id: frame.id, type: "result", results: [], transaction: false });
      return;
    }
    peer.send({ id: frame.id, type: "result", transaction: frame.type === "begin",
      results: frame.type === "execute" ? [{ columns: [{ name: "x", decltype: null }], rows: [[frame.statements[0].args[0]]], rows_affected: 0, last_insert_rowid: null }] : [] });
  });
});

test.beforeEach(async ({ page }) => {
  server.upgrades.length = 0;
  server.frames.length = 0;
  await page.goto("/browser-tests/fixture.html");
});

test.afterAll(async () => { await server.stop(); });

test("packed browser authenticates the SQL WebSocket with its first frame, never the URL", async ({ page }) => {
  const rows = await page.evaluate(async ({ baseUrl, project, token }) => {
    const candidate = "/candidate/src/index.js";
    const sdk = /** @type {typeof import("../src/index.js")} */ (await import(candidate));
    const socket = await sdk.createTerraBaseClient({ project, token, baseUrl }).database("app").connect();
    const rows = await socket.transaction(tx => tx.query("SELECT ? AS x", [7]));
    socket.close();
    return rows;
  }, { baseUrl: server.baseUrl, project, token });
  expect(rows).toEqual([{ x: 7 }]);
  expect(server.upgrades[0]?.headers.authorization).toBeUndefined();
  expect(server.upgrades[0]?.url.search).toBe("");
  expect(server.frames[0]).toEqual({ id: 1, type: "auth", token });
  expect(server.frames.map(frame => frame.type)).toEqual(["auth", "begin", "execute", "commit"]);
});
