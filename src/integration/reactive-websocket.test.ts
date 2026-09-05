import assert from "node:assert/strict";
import { describe, test } from "vitest";
import { createTerraScaleClient, TerraScaleSyncV1Client, type SyncV1LiveSubscription } from "../index.js";

// The Rust TCP harness supplies an isolated real member. No local/mock backend.
const origin = process.env.TERRASCALE_REACTIVE_TEST_ORIGIN;
const accessToken = process.env.TERRASCALE_REACTIVE_TEST_TOKEN;

function observe(subscription: SyncV1LiveSubscription, identity: string): Promise<void> {
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => { stop(); reject(new Error("live revision did not arrive")); }, 5000);
    const stop = subscription.view.subscribe(() => {
      if (subscription.view.getItems().some(item => item.identity === identity)) {
        clearTimeout(timer);
        stop();
        resolve();
      }
    });
  });
}

describe.skipIf(origin === undefined || accessToken === undefined)("real member browser WebSocket", () => {
  test("canonical views preserve local names across HTTP, WebSocket, and reconnect", async () => {
    const fetch = globalThis.fetch.bind(globalThis);
    const rest = createTerraScaleClient({ apiBaseUrl: origin!, accessToken: accessToken!, fetch });
    const client = new TerraScaleSyncV1Client({ apiBaseUrl: origin!, syncBaseUrl: origin!, accessToken, fetch });
    assert.equal((await rest.documents.put("prod", "orders", "sdk-one", { total: 1 })).ok, true);
    const options = { databaseId: "prod", query: "SELECT total FROM orders LIMIT 10", parameters: new Uint8Array() };
    const socket = await client.subscribe({ ...options, subscription: "local-websocket", transport: "websocket" });
    const http = await client.subscribe({ ...options, subscription: "local-http", autoStart: false });
    try {
      assert.equal(socket.id, "local-websocket");
      assert.deepEqual(socket.view.getItems(), http.view.getItems());
      assert.equal(socket.view.getItems()[0]?.identity, "sdk-one");
      const updated = observe(socket, "sdk-two");
      assert.equal((await rest.documents.put("prod", "orders", "sdk-two", { total: 2 })).ok, true);
      await updated;
      await http.poll();
      assert.deepEqual(socket.view.getItems(), http.view.getItems());
      assert.notEqual(client.checkpointAcknowledgement(socket.id).subscription, socket.id);
      await socket.reconnect();
      const reconnected = observe(socket, "sdk-three");
      assert.equal((await rest.documents.put("prod", "orders", "sdk-three", { total: 3 })).ok, true);
      await reconnected;
      await http.poll();
      assert.deepEqual(socket.view.getItems(), http.view.getItems());
    } finally {
      socket.close();
      http.close();
    }
  });
});
