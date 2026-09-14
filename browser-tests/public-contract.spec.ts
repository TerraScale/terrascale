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

test("the browser entrypoint keeps regional routing behavior", async ({ page }) => {
  await page.goto("/browser-tests/fixture.html");

  const observation = await page.evaluate(async () => {
    const sdk = await import("/candidate/dist/index.js");
    const requests: { url: string; headers: Record<string, string> }[] = [];
    const client = sdk.createTerraScaleClient({
      preferredRegion: "FRA",
      projectId: "01ARZ3NDEKTSV4RRFFQ69G5FAV",
      fetch: async (input: RequestInfo | URL) => {
        const request = input instanceof Request ? input : new Request(input);
        requests.push({ url: request.url, headers: Object.fromEntries(request.headers) });
        return Response.json({});
      },
    });
    const databaseId = "01BX5ZZKBKACTAV9WEVGEMMVRZ";

    const result = await client.databases.get(databaseId);
    return { requests, ok: result.ok };
  });

  expect(observation.ok).toBe(true);
  expect(observation.requests).toHaveLength(1);
  expect(observation.requests[0]?.url).toBe("https://api.terrascale.tech/api/v1/projects/01ARZ3NDEKTSV4RRFFQ69G5FAV/databases/01BX5ZZKBKACTAV9WEVGEMMVRZ");
  expect(observation.requests[0]?.headers["x-terrascale-preferred-region"]).toBe("FRA");
});

test("the packed browser client decodes the generated reactive protocol", async ({ page }) => {
  await page.goto("/browser-tests/fixture.html");

  const observation = await page.evaluate(async () => {
    const sdk = await import("/candidate/dist/index.js");
    const protocol = await import("/candidate/dist/msgpack/generated/terrascale-sync-v1-msgpack.js");
    const encoder = new TextEncoder();
    const client = new sdk.TerraScaleSyncV1Client();
    const opened = client.openSubscription({
      subscription: "browser-view",
      query: "from documents",
      parameters: new Uint8Array(),
    });
    const items = [{ identity: "document-1", value: { title: encoder.encode("ready") } }];
    const frame = protocol.encodeTerrascaleSyncV1MsgpackEnvelope({
      features: 0,
      correlation: 1n,
      causation: 0n,
      message: {
        kind: "snapshot",
        subscription: "browser-view",
        snapshot: {
          view_generation: 1n,
          authorization_generation: 1n,
          revision: 1n,
          items: [{ identity: "document-1", fields: [{ path: "title", value: encoder.encode("ready") }] }],
          digest: sdk.syncV1ResultDigest(items),
          checkpoint: encoder.encode("browser-checkpoint"),
        },
      },
    });
    client.receive(frame);
    return {
      checkpoint: opened.subscription.view.getCheckpoint()?.toString(),
      title: new TextDecoder().decode(opened.subscription.view.getItems()[0]?.value.title),
    };
  });

  expect(observation).toEqual({
    checkpoint: "[opaque SyncV1ViewCheckpoint]",
    title: "ready",
  });
});

test("the packed browser client decodes the generated reactive protocol", async ({ page }) => {
  await page.goto("/browser-tests/fixture.html");

  const observation = await page.evaluate(async () => {
    const sdk = await import("/candidate/dist/index.js");
    const protocol = await import("/candidate/dist/msgpack/generated/terrascale-sync-v2-msgpack.js");
    const encoder = new TextEncoder();
    const client = new sdk.TerraScaleSyncV2Client();
    const opened = client.openSubscription({
      subscription: "browser-view",
      query: "from documents",
      parameters: new Uint8Array(),
    });
    const items = [{ identity: "document-1", value: { title: encoder.encode("ready") } }];
    const frame = protocol.encodeTerrascaleSyncV2MsgpackEnvelope({
      features: 0,
      correlation: 1n,
      causation: 0n,
      message: {
        kind: "snapshot",
        subscription: "browser-view",
        snapshot: {
          view_generation: 1n,
          authorization_generation: 1n,
          revision: 1n,
          items: [{ identity: "document-1", fields: [{ path: "title", value: encoder.encode("ready") }] }],
          digest: sdk.syncV2ResultDigest(items),
          checkpoint: encoder.encode("browser-checkpoint"),
        },
      },
    });
    client.receive(frame);
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

test("the browser entrypoint exposes the published Database API document operations", async ({ page }) => {
  await page.goto("/browser-tests/fixture.html");

  const observation = await page.evaluate(async () => {
    const sdk = await import("/candidate/dist/index.js");
    const requests: { method: string; url: string; headers: Record<string, string>; body: unknown }[] = [];
    const client = sdk.createTerraScaleClient({
      apiBaseUrl: "https://api.terrascale.test",
      accessToken: "browser-token",
      fetch: async (input: RequestInfo | URL) => {
        const request = input instanceof Request ? input : new Request(input);
        const text = await request.clone().text();
        requests.push({
          method: request.method,
          url: request.url,
          headers: Object.fromEntries(request.headers),
          body: text.length === 0 ? null : JSON.parse(text),
        });
        return Response.json(request.method === "GET"
          ? {
            database: "customer database",
            collection: "users/v1",
            id: "user #1",
            version: "18446744073709551615",
            body: { name: "Ada" },
            position: null,
          }
          : {
            database: "customer database",
            collection: "users/v1",
            id: "user #1",
            version: "2",
            sequence: "18446744073709551615",
            operationId: "operation-1",
            acknowledgement: "global",
            backup: "full",
          }, { status: request.method === "PUT" ? 201 : 200 });
      },
    });
    const scope = { database: "customer database", collection: "users/v1" };
    const key = sdk.toUlid("01ARZ3NDEKTSV4RRFFQ69G5FB0");

    const read = await client.databaseDocuments.read(scope, "user #1");
    const created = await client.databaseDocuments.create(
      scope,
      "user #1",
      { body: { name: "Ada" }, durability: "global" },
      { idempotencyKey: key },
    );
    await client.databaseDocuments.patch(
      scope,
      "user #1",
      { body: { title: "Engineer" } },
      { idempotencyKey: key },
    );
    await client.databaseDocuments.delete(scope, "user #1", {
      idempotencyKey: key,
      durability: "global",
    });

    return {
      requests,
      readBody: read.ok ? read.value.body : null,
      readVersion: read.ok ? read.value.version : null,
      createSequence: created.ok ? created.value.sequence : null,
    };
  });

  expect(observation.readBody).toEqual({ name: "Ada" });
  expect(observation.readVersion).toBe("18446744073709551615");
  expect(observation.createSequence).toBe("18446744073709551615");
  expect(observation.requests.map(request => request.method)).toEqual([
    "GET",
    "PUT",
    "PATCH",
    "DELETE",
  ]);
  for (const request of observation.requests) {
    expect(new URL(request.url).pathname).toBe(
      "/api/v1/databases/customer%20database/collections/users%2Fv1/documents/user%20%231",
    );
    expect(request.headers.authorization).toBe("Bearer browser-token");
  }
  expect(observation.requests[1]?.headers["idempotency-key"]).toBe(
    "01ARZ3NDEKTSV4RRFFQ69G5FB0",
  );
  expect(observation.requests[1]?.body).toEqual({
    body: { name: "Ada" },
    durability: "global",
  });
  expect(new URL(observation.requests[3]!.url).searchParams.get("durability")).toBe("global");
});

test("the browser entrypoint preserves the standard Database API error contract", async ({ page }) => {
  await page.goto("/browser-tests/fixture.html");

  const error = await page.evaluate(async () => {
    const sdk = await import("/candidate/dist/index.js");
    const client = sdk.createTerraScaleClient({
      apiBaseUrl: "https://api.terrascale.test",
      fetch: async () => Response.json({
        error: {
          code: "TERRASCALE-2000",
          message: "The document does not satisfy the active schema.",
          category: "Schema",
          detail: "profile.email must be a string.",
          suggestedAction: "Correct the document to match the active schema.",
          docsUrl: "https://docs.terrascale.tech/errors/TERRASCALE-2000",
          traceId: "trace-browser-1",
          fieldErrors: [{
            field: "profile.email",
            constraint: "String",
            message: "Expected a string.",
          }],
        },
      }, {
        status: 422,
        headers: { "x-request-id": "request-browser-1" },
      }),
    });
    const result = await client.databaseDocuments.create(
      { database: "app", collection: "users" },
      "user-1",
      { body: { profile: { email: 42 } } },
      { idempotencyKey: sdk.toUlid("01ARZ3NDEKTSV4RRFFQ69G5FB0") },
    );
    return result.ok ? null : result.error;
  });

  expect(error).toMatchObject({
    kind: "validation",
    code: "TERRASCALE-2000",
    category: "Schema",
    detail: "profile.email must be a string.",
    traceId: "trace-browser-1",
    requestId: "request-browser-1",
    validation: [{
      path: "profile.email",
      code: "String",
      message: "Expected a string.",
    }],
  });
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
