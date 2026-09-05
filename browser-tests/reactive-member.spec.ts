import { expect, test } from "@playwright/test";

const origin = process.env.TERRASCALE_REACTIVE_TEST_ORIGIN;
const accessToken = process.env.TERRASCALE_REACTIVE_TEST_TOKEN;

test("cross-origin member attach, HTTP, and WebSocket use the packed SDK", async ({ page }, info) => {
  test.skip(origin === undefined || accessToken === undefined, "requires the real Rust TCP member harness");
  await page.goto("/browser-tests/fixture.html");
  const result = await page.evaluate(async ({ origin, accessToken, collection }) => {
    const sdk = await import("/candidate/dist/index.js");
    const nativeFetch = globalThis.fetch.bind(globalThis);
    const rest = sdk.createTerraScaleClient({ apiBaseUrl: origin, accessToken, fetch: nativeFetch });
    const client = new sdk.TerraScaleSyncV1Client({
      apiBaseUrl: origin, syncBaseUrl: origin, accessToken, fetch: nativeFetch,
    });
    const put = await rest.documents.put("prod", collection, "one", { total: 1 });
    if (!put.ok) throw new Error(JSON.stringify(put.error));
    const options = {
      databaseId: "prod", query: `SELECT total FROM ${collection} LIMIT 10`, parameters: new Uint8Array(),
    };
    const socket = await client.subscribe({ ...options, subscription: "browser-socket", transport: "websocket" });
    const http = await client.subscribe({ ...options, subscription: "browser-http", autoStart: false });
    try {
      await socket.reconnect();
      const changed = new Promise<void>((resolve, reject) => {
        const timer = setTimeout(() => { stop(); reject(new Error("browser delta timed out")); }, 5000);
        const stop = socket.view.subscribe(() => {
          if (socket.view.getItems().some(item => item.identity === "two")) {
            clearTimeout(timer); stop(); resolve();
          }
        });
      });
      const write = await rest.documents.put("prod", collection, "two", { total: 2 });
      if (!write.ok) throw new Error(JSON.stringify(write.error));
      await changed;
      await http.poll();
      const attach = await nativeFetch(`${origin}/api/v1/reactive/views`, {
        method: "POST", headers: { authorization: accessToken, "content-type": "application/json" },
        body: JSON.stringify({ databaseId: "prod", statement: options.query, parameters: "" }),
      });
      const denied = await nativeFetch(`${origin}/api/v1/databases`);
      return {
        socket: socket.view.getItems().map(item => item.identity),
        http: http.view.getItems().map(item => item.identity),
        region: attach.headers.get("x-terrascale-region"),
        denied: denied.status,
      };
    } finally {
      socket.close(); http.close();
    }
  }, { origin: origin!, accessToken: accessToken!, collection: `browser_${info.project.name.replaceAll("-", "_")}` });
  expect(result.socket).toEqual(["one", "two"]);
  expect(result.http).toEqual(result.socket);
  expect(result.region).toBe("GRU");
  expect(result.denied).toBe(401);
});
