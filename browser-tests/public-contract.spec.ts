import { expect, test } from "@playwright/test";

test("the browser entrypoint keeps regional and conditional document behavior", async ({ page }) => {
  await page.goto("/browser-tests/fixture.html");

  const observation = await page.evaluate(async () => {
    // The browser consumes the package extracted from the exact tarball that
    // the web server prepared for this run. It must not silently fall back to
    // the checkout's build output.
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
      preferredRegion: "fra",
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
  expect(observation[0]?.headers["x-terrascale-preferred-region"]).toBe("fra");
  expect(observation[1]?.headers["x-terrascale-session-consistency-token"]).toBe("session-2");
});
