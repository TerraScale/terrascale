import { expect, test } from "@playwright/test";

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
            version: 1,
            body: { name: "Ada" },
            position: null,
          }
          : {
            database: "customer database",
            collection: "users/v1",
            id: "user #1",
            version: 2,
            sequence: 2,
            operationId: "operation-1",
            acknowledgement: "global",
            backup: "full",
          }, { status: request.method === "PUT" ? 201 : 200 });
      },
    });
    const scope = { database: "customer database", collection: "users/v1" };
    const key = sdk.toUlid("01ARZ3NDEKTSV4RRFFQ69G5FB0");

    const read = await client.databaseDocuments.read(scope, "user #1");
    await client.databaseDocuments.create(
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

    return { requests, readBody: read.ok ? read.value.body : null };
  });

  expect(observation.readBody).toEqual({ name: "Ada" });
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
