import { expect, test } from "@playwright/test";
/** @import { DatabaseCommit } from "../src/index.js" */

test("packed browser entrypoint preserves exact native values and preparation identity", async ({
  page,
}) => {
  await page.goto("/browser-tests/fixture.html");
  const observation = await page.evaluate(async () => {
    const candidate = "/candidate/src/index.js";
    const sdk = /** @type {typeof import("../src/index.js")} */ (
      await import(candidate)
    );
    let dispatches = 0;
    const client = sdk.createTerraBaseClient({
      tenant: "00000000-0000-0000-0000-000000000001",
      database: "00000000-0000-0000-0000-000000000002",
      shard: 1n,
      endpoint: "https://gru.api.terrascale.tech",
      apiKey: "browser-unit-only",
      fetch: async () => {
        dispatches += 1;
        throw new Error("Pure browser preparation must not dispatch.");
      },
    });
    const bytes = new Uint8Array([0, 255]);
    /** @type {DatabaseCommit} */
    const commit = {
      transaction_id: {
        expires_at_unix_ms: BigInt(Date.now() + 60_000),
        nonce: "01".repeat(16),
      },
      authority: { mode: "leaderless" },
      writes: [
        {
          collection: "00000000-0000-0000-0000-000000000003",
          document: "00000000-0000-0000-0000-000000000004",
          schema_generation: 1n,
          schema_digest: "ab".repeat(32),
          condition: { kind: "none" },
          observed: [],
          value: {
            kind: "live",
            fields: [
              { id: 1, value: { type: "i64", value: 9_007_199_254_740_993n } },
              { id: 2, value: { type: "bytes", value: bytes } },
              { id: 3, value: { type: "null" } },
            ],
          },
        },
      ],
    };
    const prepared = await client.prepareCommit(commit);
    bytes[0] = 99;
    const changed = await client.prepareCommit(commit);
    return {
      dispatches,
      immutable: Object.isFrozen(prepared),
      digest: prepared.effect_digest,
      changed: prepared.effect_digest !== changed.effect_digest,
      nonce: prepared.transaction_id.nonce,
      removed: [
        "createTerraScaleClient",
        "TerraScaleSyncV1Client",
        "TerraScaleHttp",
        "toUlid",
      ].filter((name) => name in sdk),
      methods: /** @type {const} */ ([
        "read",
        "query",
        "subscription",
        "commit",
        "transactionStatus",
      ]).every((name) => typeof client[name] === "function"),
    };
  });
  expect(observation.dispatches).toBe(0);
  expect(observation.immutable).toBe(true);
  expect(observation.digest).toMatch(/^[0-9a-f]{64}$/u);
  expect(observation.changed).toBe(true);
  expect(observation.nonce).toBe("01".repeat(16));
  expect(observation.removed).toEqual([]);
  expect(observation.methods).toBe(true);
});
