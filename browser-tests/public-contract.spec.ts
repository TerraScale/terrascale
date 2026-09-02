import { expect, test } from "@playwright/test";
import { Buffer } from "node:buffer";

import {
  encodeTerrascaleSyncV2MsgpackEnvelope,
  type TerrascaleSyncV2MsgpackMessage,
} from "../src/msgpack/generated/terrascale-sync-v2-msgpack.js";

function snapshotFrame(subscription: string): Uint8Array {
  const encoder = new TextEncoder();
  const items = [{ identity: "document-1", value: { title: encoder.encode("ready") } }];
  return encodeTerrascaleSyncV2MsgpackEnvelope({
    features: 0,
    correlation: 2n,
    causation: 1n,
    message: {
      kind: "snapshot",
      subscription,
      snapshot: {
        view_generation: 1n,
        authorization_generation: 1n,
        revision: 1n,
        items: [{ identity: "document-1", fields: [{ path: "title", value: items[0]!.value.title }] }],
        digest: digest(items),
        checkpoint: encoder.encode(`${subscription}-checkpoint`),
      },
    },
  });
}

function digest(items: readonly { identity: string; value: Readonly<Record<string, Uint8Array>> }[]): string {
  const bytes = new TextEncoder();
  const payload = items.map(item => [item.identity, Object.entries(item.value).map(([path, value]) => [path, [...value]])]);
  let hash = 2166136261;
  for (const byte of bytes.encode(JSON.stringify(payload))) hash = Math.imul(hash ^ byte, 16777619);
  return hash.toString(16).padStart(8, "0").repeat(8);
}

test("the browser entrypoint keeps regional and conditional document behavior", async ({ page }) => {
  await page.goto("/browser-tests/fixture.html");

  const observation = await page.evaluate(async () => {
    const sdk = await import("/candidate/dist/index.js");
    const requests: { url: string; headers: Record<string, string> }[] = [];
    const responses = [
      {
        repositoryId: "01ARZ3NDEKTSV4RRFFQ69G5FAV",
        repository: "users",
        id: "01BX5ZZKBKACTAV9WEVGEMMVRZ",
        data: { name: "Ada" },
        changeToken: "change-2",
        sessionConsistencyToken: "session-2",
      },
      {
        repositoryId: "01ARZ3NDEKTSV4RRFFQ69G5FAV",
        repository: "users",
        id: "01BX5ZZKBKACTAV9WEVGEMMVRZ",
        data: { name: "Ada" },
        changeToken: "change-2",
      },
    ];
    const client = sdk.createTerraScaleClient({
      preferredRegion: "FRA",
      sessionConsistency: "automatic",
      fetch: async (input: RequestInfo | URL) => {
        const request = input instanceof Request ? input : new Request(input);
        requests.push({ url: request.url, headers: Object.fromEntries(request.headers) });
        return Response.json(responses.shift());
      },
    });
    const scope = { repositoryId: sdk.toUlid("01ARZ3NDEKTSV4RRFFQ69G5FAV") };
    const id = sdk.toUlid("01BX5ZZKBKACTAV9WEVGEMMVRZ");

    await client.documents.put(scope, id, { name: "Ada" }, { expectedChangeToken: "change-1" });
    await client.documents.get(scope, id);

    return requests;
  });

  expect(observation).toHaveLength(2);
  expect(observation[0]?.url).toBe("https://api.terrascale.tech/api/v1/repositories/01ARZ3NDEKTSV4RRFFQ69G5FAV/documents/01BX5ZZKBKACTAV9WEVGEMMVRZ");
  expect(observation[0]?.headers["if-match"]).toBe("change-1");
  expect(observation[0]?.headers["x-terrascale-preferred-region"]).toBe("FRA");
  expect(observation[1]?.headers["x-terrascale-session-consistency-token"]).toBe("session-2");
});

test("the browser client sends and receives generated frames over WebSocket", async ({ page }) => {
  await page.routeWebSocket("**/sync-ws", socket => {
    socket.onMessage(() => socket.send(Buffer.from(snapshotFrame("websocket-view"))));
  });
  await page.goto("/browser-tests/fixture.html");

  const observation = await page.evaluate(async () => {
    const sdk = await import("/candidate/dist/index.js");
    const client = new sdk.TerraScaleSyncV2Client();
    const opened = client.openSubscription({
      subscription: "websocket-view",
      query: "from documents",
      parameters: new Uint8Array(),
    });
    const transport = new sdk.TerraScaleSyncV2Transport({
      client,
      endpoint: `${location.origin.replace("http", "ws")}/sync-ws`,
    });
    await transport.connect();
    await transport.send(opened.message, 1n);
    return {
      checkpoint: opened.subscription.view.getCheckpoint()?.toString(),
      title: new TextDecoder().decode(opened.subscription.view.getItems()[0]?.value.title),
    };
  });

  expect(observation).toEqual({
    checkpoint: "[opaque SyncV2ViewCheckpoint]",
    title: "ready",
  });
});

test("the browser client falls back to HTTP+MessagePack when WebSocket setup fails", async ({ page }) => {
  await page.route("**/sync-http", async route => {
    await route.fulfill({
      status: 200,
      contentType: "application/msgpack",
      body: Buffer.from(snapshotFrame("http-view")),
    });
  });
  await page.goto("/browser-tests/fixture.html");

  const observation = await page.evaluate(async () => {
    const sdk = await import("/candidate/dist/index.js");
    const client = new sdk.TerraScaleSyncV2Client();
    const opened = client.openSubscription({
      subscription: "http-view",
      query: "from documents",
      parameters: new Uint8Array(),
    });
    const transport = new sdk.TerraScaleSyncV2Transport({
      client,
      endpoint: `${location.origin}/sync-http`,
      webSocketFactory: () => { throw new Error("WebSocket intentionally unavailable"); },
    });
    await transport.connect();
    const response = await transport.send(opened.message, 1n);
    return {
      mode: transport.mode,
      response: response?.message.kind,
      title: new TextDecoder().decode(opened.subscription.view.getItems()[0]?.value.title),
    };
  });

  expect(observation).toEqual({ mode: "http", response: "snapshot", title: "ready" });
});
