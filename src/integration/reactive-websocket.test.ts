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
  test("production URLs keep HTTP on sync and sockets on the attached member", async () => {
    const requests: URL[] = [];
    const attachAuthorization: Array<string | null> = [];
    const deliveryHeaders: Array<{ authorization: string | null; capability: string | null }> = [];
    const sockets: URL[] = [];
    let omitRegion = false;
    // Route production names to the isolated TCP member, preserving real
    // responses and frames. This exercises URL selection, not DNS or TLS.
    const client = new TerraScaleSyncV1Client({
      accessToken: accessToken!,
      fetch: async (input, init) => {
        const url = new URL(input instanceof Request ? input.url : input.toString());
        const headers = new Headers(init?.headers);
        requests.push(url);
        if (url.pathname === "/api/v1/reactive/views") {
          attachAuthorization.push(headers.get("authorization"));
        } else if (url.pathname.includes("/snapshot/") || url.pathname.endsWith("/log")) {
          deliveryHeaders.push({
            authorization: headers.get("authorization"),
            capability: headers.get("x-terrascale-capability"),
          });
        }
        const response = await globalThis.fetch(new URL(url.pathname + url.search, origin!), init);
        if (omitRegion && url.pathname === "/api/v1/reactive/views") {
          const headers = new Headers(response.headers);
          headers.delete("x-terrascale-region");
          return new Response(response.body, { status: response.status, headers });
        }
        return response;
      },
      webSocketFactory: (raw, protocols) => {
        const url = new URL(raw);
        sockets.push(url);
        assert.equal(url.origin, "wss://gru.terrascale.tech");
        const target = new URL(url.pathname, origin!);
        target.protocol = "ws:";
        return new WebSocket(target, protocols === undefined ? undefined : [...protocols]);
      },
    });
    const subscription = await client.subscribe({
      databaseId: "prod", query: "SELECT total FROM routing_probe LIMIT 10",
      parameters: new Uint8Array(), subscription: "routing", transport: "websocket",
    });
    try {
      await subscription.reconnect();
      assert.equal(sockets.length, 2);
      assert.ok(requests.some(url => url.pathname.endsWith("/snapshot/0")));
      for (const url of requests) {
        assert.equal(url.origin, url.pathname === "/api/v1/reactive/views"
          ? "https://api.terrascale.tech" : "https://sync.api.terrascale.tech");
      }
      assert.ok(attachAuthorization.length >= 2, "both live views must authenticate their attach");
      const expectedAuthorization = accessToken!.startsWith("Bearer ")
        ? accessToken!
        : `Bearer ${accessToken!}`;
      assert.ok(attachAuthorization.every(value => value === expectedAuthorization));
      assert.ok(deliveryHeaders.length > 0, "the HTTP view must fetch a CDN snapshot or log");
      assert.ok(deliveryHeaders.every(({ authorization, capability }) => authorization === null && capability !== null));
      omitRegion = true;
      await assert.rejects(client.subscribe({
        databaseId: "prod", query: "SELECT total FROM routing_probe LIMIT 10",
        parameters: new Uint8Array(), subscription: "missing-region", transport: "websocket",
      }), /did not identify a regional member/);
      assert.equal(sockets.length, 2, "missing routing evidence must not open a socket");
    } finally {
      subscription.close();
    }
  });

  test("canonical views preserve local names across HTTP, WebSocket, and reconnect", async () => {
    const fetch = globalThis.fetch.bind(globalThis);
    const rest = createTerraScaleClient({ apiBaseUrl: origin!, accessToken: accessToken!, fetch });
    const client = new TerraScaleSyncV1Client({ apiBaseUrl: origin!, syncBaseUrl: origin!, accessToken: accessToken!, fetch });
    assert.equal((await rest.documents.put("prod", "orders", "sdk-one", { total: 1 })).ok, true);
    const options = { databaseId: "prod", query: "SELECT total FROM orders LIMIT 10", parameters: new Uint8Array() };
    const socket = await client.subscribe({ ...options, subscription: "local-websocket", transport: "websocket" });
    const http = await client.subscribe({ ...options, subscription: "local-http", autoStart: false });
    try {
      const empty = await client.subscribe({ ...options, query: "SELECT total FROM absent LIMIT 10", subscription: "empty-result", autoStart: false });
      assert.deepEqual(empty.view.getItems(), []);
      empty.close();
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
