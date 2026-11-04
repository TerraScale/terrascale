import { readFileSync } from "node:fs";
import { expect, test } from "vitest";
import { SqlError, SqlErrorCode, SqlSocketClosedError } from "../src/index.js";
import { sqlSocketFixture } from "./helpers/sql-socket-server.js";
import { prefix, token } from "./helpers/v1-http.js";

const transcript = JSON.parse(readFileSync(new URL("./fixtures/sql-socket/transaction.json", import.meta.url), "utf8"));

/** @param {{ exchanges: { client: unknown; server: unknown }[] }} recorded */
function replay(recorded) {
  /** @type {import("./helpers/sql-socket-server.js").SqlSocketHandler} */
  return (frame, peer) => {
    const exchange = recorded.exchanges.find(entry => /** @type {{ id: number }} */ (entry.client).id === frame.id);
    expect(frame).toEqual(exchange?.client);
    peer.send(exchange?.server);
  };
}

test("connects with the bearer key and runs the recorded transaction with typed rows and blobs", async () => {
  const { client, upgrades, frames, closes } = await sqlSocketFixture(replay(transcript));
  const socket = await client.database("app").connect();
  expect(upgrades[0]?.url.pathname).toBe(`${prefix}/databases/app/sql/ws`);
  expect(upgrades[0]?.headers.authorization).toBe(`Bearer ${token}`);
  expect(upgrades[0]?.headers["sec-websocket-protocol"]).toBe("terrascale.sql.v1");
  const rows = await socket.transaction(async tx => {
    expect(tx.inTransaction).toBe(true);
    await tx.execute("INSERT INTO people (id, name, avatar) VALUES (?, ?, ?)", [1, "Ada", new Uint8Array([0, 1, 255])]);
    return tx.query("SELECT id, name, avatar FROM people WHERE id = :id", { id: 1 });
  });
  expect(rows).toEqual([{ id: 1, name: "Ada", avatar: new Uint8Array([0, 1, 255]) }]);
  expect(socket.inTransaction).toBe(false);
  expect(frames.map(frame => frame.type)).toEqual(["begin", "execute", "execute", "commit"]);
  await socket[Symbol.asyncDispose]();
  await expect.poll(() => closes).toEqual([1000]);
});

test("a revoked key answers SqlError, then the close rejects every later request", async () => {
  const { client } = await sqlSocketFixture((frame, peer) => {
    peer.send({ id: frame.id, type: "error", error: { code: "unauthorized", message: "the credential no longer grants this access to the database", statement: 0, sqlstate: "28000" }, transaction: false });
    peer.close(1008, "unauthorized");
  });
  const socket = await client.database("app").connect();
  const refused = await socket.execute("SELECT 1").catch(error => error);
  expect(refused).toBeInstanceOf(SqlError);
  expect(refused).toMatchObject({ code: SqlErrorCode.unauthorized, status: 401, sqlstate: "28000", retryable: false });
  await expect.poll(() => socket.closed).toBe(true);
  const closed = await socket.execute("SELECT 1").catch(error => error);
  expect(closed).toBeInstanceOf(SqlSocketClosedError);
  expect(closed).toMatchObject({ closeCode: 1008, reason: "unauthorized" });
});
