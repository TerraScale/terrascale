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
