import assert from "node:assert/strict";
import { randomUUID } from "node:crypto";
import { test } from "vitest";
import { createTerraScaleSqliteRuntime } from "./local/index.js";
import {
  localSqliteUnitTestsAllowed,
  localFixture,
} from "./local/test-environment.js";
/** @import { DatabaseWrite } from "./database-types.js" */

test.skipIf(!localSqliteUnitTestsAllowed)(
  "SQLite native commits preserve i64, bytes, explicit null, context and durable status across restart",
  async () => {
    const fixture = await localFixture();
    try {
      const document = randomUUID();
      const fields = [
        { id: 1, value: { type: /** @type {const} */ ("i64"), value: -(1n << 63n) } },
        {
          id: 2,
          value: { type: /** @type {const} */ ("bytes"), value: Uint8Array.of(0, 255, 17) },
        },
        { id: 3, value: { type: /** @type {const} */ ("null") } },
      ];
      const prepared = await fixture.client.prepareCommit(
        fixture.commit(document, fields),
      );
      const result = await fixture.client.commit(prepared);
      assert.equal(result.ok, true);
      const point = await fixture.client.read(fixture.collection, document);
      assert.ok(point.ok);
      assert.equal(point.value.state, "value");
      assert.deepEqual(
        point.value.siblings[0]?.kind === "live"
          ? point.value.siblings[0].fields
          : [],
        fields,
      );
      assert.equal(point.value.context.length, 1);
      const repeated = await fixture.client.commit(prepared);
      assert.deepEqual(repeated, result);
      await fixture.runtime.close();
      const restarted = createTerraScaleSqliteRuntime({
        sqlite: { enabled: true, databasePath: fixture.databasePath },
      });
      try {
        assert.deepEqual(
          await restarted.createClient().read(fixture.collection, document),
          point,
        );
        const status = await restarted
          .createClient()
          .transactionStatus(prepared);
        assert.ok(status.ok);
        assert.equal(status.value.state, "committed");
        if (status.value.state === "committed" && result.ok)
          assert.deepEqual(status.value.outcome.receipt, result.value);
      } finally {
        await restarted.close();
      }
    } finally {
      await fixture.close();
    }
  },
);

test.skipIf(!localSqliteUnitTestsAllowed)(
  "SQLite rejected conditional groups retain rejection and reserve no mutation sequence",
  async () => {
    const fixture = await localFixture();
    try {
      const first = randomUUID();
      const fields = [{ id: 1, value: { type: /** @type {const} */ ("bool"), value: true } }];
      const initial = await fixture.client.commit(
        await fixture.client.prepareCommit(fixture.commit(first, fields)),
      );
      assert.ok(initial.ok);
      const failed = fixture.commit(first, fields);
      const other = randomUUID();
      const extra = /** @type {DatabaseWrite} */ (fixture.commit(other, fields).writes[0]);
      const prepared = await fixture.client.prepareCommit({
        ...failed,
        writes: [...failed.writes, extra].sort((a, b) =>
          a.document.localeCompare(b.document),
        ),
      });
      const rejection = await fixture.client.commit(prepared);
      assert.equal(rejection.ok, false);
      if (!rejection.ok)
        assert.equal(rejection.error.mutation_outcome, "rejected");
      assert.deepEqual(await fixture.client.commit(prepared), rejection);
      const status = await fixture.client.transactionStatus(prepared);
      assert.ok(status.ok);
      assert.equal(status.value.state, "rejected");
      const absent = await fixture.client.read(fixture.collection, other);
      assert.ok(absent.ok);
      assert.equal(absent.value.state, "absent");
      const next = await fixture.client.commit(
        await fixture.client.prepareCommit(fixture.commit(other, fields)),
      );
      assert.ok(next.ok);
      assert.equal(
        next.value.commit_sequence,
        initial.value.commit_sequence + 1n,
      );
      const changed = await fixture.client.prepareCommit({
        ...failed,
        writes: [
          {
            .../** @type {DatabaseWrite} */ (failed.writes[0]),
            value: {
              kind: "live",
              fields: [{ id: 1, value: { type: "bool", value: false } }],
            },
          },
        ],
      });
      const conflict = await fixture.client.commit(changed);
      assert.equal(conflict.ok, false);
      if (!conflict.ok)
        assert.equal(conflict.error.code, "idempotency_conflict");
    } finally {
      await fixture.close();
    }
  },
);

test.skipIf(!localSqliteUnitTestsAllowed)(
  "SQLite compare-and-swap update and tombstone preserve full observed context",
  async () => {
    const fixture = await localFixture();
    try {
      const document = randomUUID();
      assert.ok(
        (
          await fixture.client.commit(
            await fixture.client.prepareCommit(
              fixture.commit(document, [
                { id: 1, value: { type: "utf8", value: "old" } },
              ]),
            ),
          )
        ).ok,
      );
      const point = await fixture.client.read(fixture.collection, document);
      assert.ok(point.ok);
      const update = await fixture.client.prepareCommit(
        fixture.commit(
          document,
          [{ id: 1, value: { type: "utf8", value: "new" } }],
          point.value,
        ),
      );
      assert.ok((await fixture.client.commit(update)).ok);
      const next = await fixture.client.read(fixture.collection, document);
      assert.ok(next.ok);
      assert.equal(next.value.siblings.length, 1);
      const deletion = fixture.commit(document, [], next.value);
      const tombstone = await fixture.client.prepareCommit({
        ...deletion,
        writes: [
          { .../** @type {DatabaseWrite} */ (deletion.writes[0]), value: { kind: "tombstone" } },
        ],
      });
      assert.ok((await fixture.client.commit(tombstone)).ok);
      const deleted = await fixture.client.read(fixture.collection, document);
      assert.ok(deleted.ok);
      assert.equal(deleted.value.state, "deleted");
      assert.equal(deleted.value.siblings[0]?.kind, "tombstone");
    } finally {
      await fixture.close();
    }
  },
);
