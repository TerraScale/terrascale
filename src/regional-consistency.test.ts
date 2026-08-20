import assert from "node:assert/strict";
import { describe, test } from "node:test";

import { createTerraScaleClient, resolveRuntimeConfig } from "./index.js";
import { toUlid } from "./ids.js";

const repositoryId = toUlid("01ARZ3NDEKTSV4RRFFQ69G5FAV");
const documentId = toUlid("01BX5ZZKBKACTAV9WEVGEMMVRZ");

describe("regional document consistency", () => {
  test("rejects invalid or incomplete strict regional configuration", () => {
    assert.throws(
      () => resolveRuntimeConfig({ preferredRegion: "bad region", fetch: globalThis.fetch }),
      /valid TerraScale region code/u,
    );
    assert.throws(
      () => resolveRuntimeConfig({ regionAffinity: "strict", fetch: globalThis.fetch }),
      /requires a preferredRegion/u,
    );
    assert.throws(
      () => resolveRuntimeConfig({ regionAffinity: "unsupported", fetch: globalThis.fetch } as never),
      /regionAffinity must be either/u,
    );
  });

  test("resolves global, failover, and strict regional runtime boundaries", () => {
    const global = resolveRuntimeConfig({ fetch: globalThis.fetch });
    assert.equal(global.apiBaseUrl, "https://api.terrascale.tech");
    assert.equal(global.preferredRegion, undefined);
    assert.equal(global.regionAffinity, "failover");

    const failover = resolveRuntimeConfig({
      preferredRegion: "FRA",
      regionAffinity: "failover",
      fetch: globalThis.fetch,
    });
    assert.equal(failover.apiBaseUrl, "https://api.terrascale.tech");
    assert.equal(failover.preferredRegion, "fra");

    const strict = resolveRuntimeConfig({
      preferredRegion: "SFO",
      regionAffinity: "strict",
      fetch: globalThis.fetch,
    });
    assert.equal(strict.apiBaseUrl, "https://sfo.api.terrascale.tech");
    assert.equal(strict.preferredRegion, "sfo");
  });

  test("keeps session and retry policy in the resolved runtime boundary", () => {
    const defaults = resolveRuntimeConfig({ fetch: globalThis.fetch });
    assert.equal(defaults.sessionConsistency, "none");
    assert.equal(defaults.maxRetryAttempts, 0);
    assert.equal(defaults.retryDelayMs, 250);

    const configured = resolveRuntimeConfig({
      sessionConsistency: "automatic",
      maxRetryAttempts: 2,
      retryDelayMs: 0,
      fetch: globalThis.fetch,
    });
    assert.equal(configured.sessionConsistency, "automatic");
    assert.equal(configured.maxRetryAttempts, 2);
    assert.equal(configured.retryDelayMs, 0);
  });

  test("keeps stale-read controls on single-document reads only", () => {
    const client = createTerraScaleClient({ fetch: globalThis.fetch });

    if (false) {
      // @ts-expect-error list does not support the single-document stale-read contract
      void client.documents.list({ repositoryId }, undefined, { allowStale: true });
      // @ts-expect-error TQL does not support the single-document stale-read contract
      void client.query.execute({ repositoryId }, { query: "from users" }, { allowStale: true });
      void client.documents.get({ repositoryId }, documentId, { allowStale: true });
    }
  });
});
