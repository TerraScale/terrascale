import assert from "node:assert/strict";
import { randomUUID } from "node:crypto";
import { test } from "vitest";
import {
  localSqliteUnitTestsAllowed,
  localFixture,
} from "./local/test-environment.js";
/** @import { DatabaseQueryPredicate } from "./database-types.js" */
/** @type {DatabaseQueryPredicate} */
const predicate = {
  op: "equal",
  path: [1],
  value: { type: "bool", value: true },
};

test.skipIf(!localSqliteUnitTestsAllowed)(
  "local snapshots freeze one cut and poll publishes complete groups including empty projected effects",
  async () => {
    const fixture = await localFixture();
    try {
      const documents = [randomUUID(), randomUUID()].sort();
      for (const document of documents)
        assert.ok(
          (
            await fixture.client.commit(
              await fixture.client.prepareCommit(
                fixture.commit(document, [
                  { id: 1, value: { type: "bool", value: true } },
                ]),
              ),
            )
          ).ok,
        );
      const first = await fixture.client.subscription(fixture.collection, {
        phase: "snapshot",
        predicate,
        limit: 1,
      });
      assert.ok(
        first.ok &&
          first.value.phase === "snapshot" &&
          "next_snapshot_cursor" in first.value,
      );
      const newDocument = randomUUID();
      assert.ok(
        (
          await fixture.client.commit(
            await fixture.client.prepareCommit(
              fixture.commit(newDocument, [
                { id: 1, value: { type: "bool", value: true } },
              ]),
            ),
          )
        ).ok,
      );
      const terminal = await fixture.client.subscription(fixture.collection, {
        phase: "snapshot",
        predicate,
        limit: 1,
        snapshot_cursor: first.value.next_snapshot_cursor,
      });
      assert.ok(
        terminal.ok &&
          terminal.value.phase === "snapshot" &&
          "poll_cursor" in terminal.value,
      );
      assert.deepEqual(
        [...first.value.rows, ...terminal.value.rows].map(
          (row) => row.document,
        ),
        documents,
      );
      const poll = await fixture.client.subscription(fixture.collection, {
        phase: "poll",
        predicate,
        poll_cursor: terminal.value.poll_cursor,
      });
      assert.ok(poll.ok && poll.value.phase === "poll");
      assert.equal(poll.value.groups.length, 1);
      assert.equal(poll.value.groups[0]?.effects[0]?.document, newDocument);
      const nonmatch = randomUUID();
      assert.ok(
        (
          await fixture.client.commit(
            await fixture.client.prepareCommit(
              fixture.commit(nonmatch, [
                { id: 1, value: { type: "bool", value: false } },
              ]),
            ),
          )
        ).ok,
      );
      const empty = await fixture.client.subscription(fixture.collection, {
        phase: "poll",
        predicate,
        poll_cursor: poll.value.poll_cursor,
      });
      assert.ok(empty.ok && empty.value.phase === "poll");
      assert.equal(empty.value.groups.length, 1);
      assert.deepEqual(empty.value.groups[0]?.effects, []);
      const current = await fixture.client.read(
        fixture.collection,
        newDocument,
      );
      assert.ok(current.ok);
      assert.ok(
        (
          await fixture.client.commit(
            await fixture.client.prepareCommit(
              fixture.commit(
                newDocument,
                [{ id: 1, value: { type: "bool", value: false } }],
                current.value,
              ),
            ),
          )
        ).ok,
      );
      const removal = await fixture.client.subscription(fixture.collection, {
        phase: "poll",
        predicate,
        poll_cursor: empty.value.poll_cursor,
      });
      assert.ok(removal.ok && removal.value.phase === "poll");
      assert.deepEqual(removal.value.groups[0]?.effects, [
        { document: newDocument, point: null },
      ]);
      const changed = await fixture.client.subscription(fixture.collection, {
        phase: "poll",
        predicate: { op: "is_missing", path: [2] },
        poll_cursor: empty.value.poll_cursor,
      });
      assert.equal(changed.ok, false);
      if (!changed.ok) assert.equal(changed.error.code, "invalid_cursor");
    } finally {
      await fixture.close();
    }
  },
);

test.skipIf(!localSqliteUnitTestsAllowed)(
  "local query cursor is finite, bound to limit and invalidated by local restart/reset",
  async () => {
    const fixture = await localFixture();
    try {
      for (let i = 0; i < 2; i++)
        assert.ok(
          (
            await fixture.client.commit(
              await fixture.client.prepareCommit(
                fixture.commit(randomUUID(), [
                  { id: 1, value: { type: "bool", value: true } },
                ]),
              ),
            )
          ).ok,
        );
      const page = await fixture.client.query(fixture.collection, {
        predicate,
        limit: 1,
      });
      assert.ok(page.ok && page.value.next_cursor !== undefined);
      const wrong = await fixture.client.query(fixture.collection, {
        predicate,
        limit: 2,
        cursor: page.value.next_cursor,
      });
      assert.equal(wrong.ok, false);
      const next = await fixture.client.query(fixture.collection, {
        predicate,
        limit: 1,
        cursor: page.value.next_cursor,
      });
      assert.ok(next.ok);
      assert.equal(next.value.next_cursor, undefined);
      await fixture.runtime.reset();
      await fixture.runtime.bindCollection(fixture.binding);
      const reset = await fixture.client.query(fixture.collection, {
        predicate,
        limit: 1,
        cursor: page.value.next_cursor,
      });
      assert.equal(reset.ok, false);
      if (!reset.ok) assert.equal(reset.error.code, "invalid_cursor");
    } finally {
      await fixture.close();
    }
  },
);

test.skipIf(!localSqliteUnitTestsAllowed)(
  "local snapshot pagination respects encoded response bytes without dropping rows",
  async () => {
    const fixture = await localFixture();
    try {
      const documents = Array.from({ length: 5 }, () => randomUUID()).sort();
      for (const document of documents) {
        const prepared = await fixture.client.prepareCommit(
          fixture.commit(document, [
            { id: 1, value: { type: "utf8", value: "x".repeat(900_000) } },
          ]),
        );
        assert.ok((await fixture.client.commit(prepared)).ok);
      }
      /** @type {DatabaseQueryPredicate} */
      const predicate = { op: "is_missing", path: [2] };
      const page = await fixture.client.query(fixture.collection, {
        predicate,
        limit: 128,
      });
      assert.ok(page.ok && page.value.next_cursor !== undefined);
      assert.ok(
        page.value.rows.length > 0 && page.value.rows.length < documents.length,
      );
      const next = await fixture.client.query(fixture.collection, {
        predicate,
        limit: 128,
        cursor: page.value.next_cursor,
      });
      assert.ok(next.ok);
      assert.equal(next.value.next_cursor, undefined);
      assert.deepEqual(
        [...page.value.rows, ...next.value.rows].map((row) => row.document),
        documents,
      );
    } finally {
      await fixture.close();
    }
  },
);
