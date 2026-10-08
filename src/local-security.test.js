import { generateId } from "./ids.js";
import assert from "node:assert/strict";
import { join } from "node:path";
import { TerraScaleLocalRouter } from "./local/router.js";
import { TerraScaleSqliteStore } from "./local/store.js";
import { test } from "vitest";
import { createTerraBaseClient } from "./local/legacy-client.js";
import {
  localSqliteUnitTestsAllowed,
  localFixture,
} from "./local/test-environment.js";

test.skipIf(!localSqliteUnitTestsAllowed)(
  "actual loopback listener enforces authentication and exact scoped native paths",
  async () => {
    const token = "local-test-token-01234567890123456789";
    const fixture = await localFixture({
      authentication: "required",
      bearerToken: token,
    });
    try {
      const address = await fixture.runtime.start();
      const configured = {
        ...fixture.runtime.scope,
        endpoint: address.origin,
        apiKey: token,
      };
      const client = createTerraBaseClient(configured);
      const result = await client.read(fixture.collection, generateId());
      assert.ok(result.ok);
      assert.equal(result.value.state, "absent");
      const denied = await createTerraBaseClient({
        ...configured,
        apiKey: "wrong",
      }).read(fixture.collection, generateId());
      assert.equal(denied.ok, false);
      if (!denied.ok) assert.equal(denied.error.code, "invalid_key");
      const crossScope = await createTerraBaseClient({
        ...configured,
        database: generateId(),
      }).read(fixture.collection, generateId());
      assert.equal(crossScope.ok, false);
      if (!crossScope.ok)
        assert.equal(crossScope.error.code, "forbidden_scope");
      const old = await fetch(`${address.origin}/api/v1/databases`, {
        headers: { authorization: `Bearer ${token}` },
      });
      assert.equal(old.status, 403);
    } finally {
      await fixture.close();
    }
  },
);

test.skipIf(!localSqliteUnitTestsAllowed)(
  "closing the local runtime fences new calls immediately and shares one completion",
  async () => {
    const fixture = await localFixture();
    const first = fixture.runtime.close();
    const second = fixture.runtime.close();
    assert.equal(first, second);
    assert.throws(() => fixture.runtime.createClient(), /closed/u);
    await assert.rejects(fixture.runtime.reset(), /closed/u);
    await first;
    await fixture.close();
  },
);

test.skipIf(!localSqliteUnitTestsAllowed)(
  "local router cancels an uncooperative streamed request body",
  async () => {
    const fixture = await localFixture();
    const store = new TerraScaleSqliteStore(
      join(fixture.directory, "cancel.sqlite"),
    );
    try {
      const router = new TerraScaleLocalRouter(store, fixture.runtime.scope, {
        authentication: "permissive",
      });
      const controller = new AbortController();
      const scope = fixture.runtime.scope;
      const request = new Request(
        `http://127.0.0.1/v1/tenants/${scope.tenant}/databases/${scope.database}/shards/${scope.shard}/transactions`,
        /** @type {RequestInit} */ ({
          method: "POST",
          headers: { "content-type": "application/json" },
          signal: controller.signal,
          body: /** @type {ReadableStream<Uint8Array>} */ (
            new ReadableStream({
              pull: () => /** @type {Promise<void>} */ (new Promise(() => undefined)),
            })
          ),
          duplex: "half",
        }),
      );
      const pending = router.handle(request);
      controller.abort();
      assert.equal((await pending).status, 400);
    } finally {
      store.close();
      await fixture.close();
    }
  },
);
