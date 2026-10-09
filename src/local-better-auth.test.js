import assert from "node:assert/strict";
import { DatabaseSync } from "node:sqlite";
import { rm } from "node:fs/promises";
import { test } from "vitest";
import { isValidId } from "./ids.js";
import { TerraScaleSqliteStore } from "./local/store.js";
import { createLocalBetterAuthAdapter } from "./local/better-auth.js";
import { createTerraScaleSqliteRuntime } from "./local/index.js";
import {
  localSqliteUnitTestsAllowed,
  localFixture,
} from "./local/test-environment.js";
/** @import { DBAdapter } from "@better-auth/core/db/adapter" */
/** @import { BetterAuthOptions } from "@better-auth/core" */
const options = {
  appName: "local-native-auth",
  secret: "test-secret-test-secret-test-secret",
};
/** @param {string} email */
const user = (email) => ({
  name: email,
  email,
  emailVerified: false,
  createdAt: new Date(),
  updatedAt: new Date(),
});

test.skipIf(!localSqliteUnitTestsAllowed)("old auth storage requires a reset before startup and fresh storage recreates bootstrap state", async () => {
  const fixture = await localFixture();
  /** @type {ReturnType<typeof createTerraScaleSqliteRuntime> | undefined} */
  let fresh;
  try {
    await fixture.runtime.close();
    const legacy = new DatabaseSync(fixture.databasePath);
    try {
      legacy.exec("PRAGMA user_version = 0; CREATE TABLE schema_migrations (version INTEGER PRIMARY KEY, applied_at TEXT NOT NULL); INSERT INTO schema_migrations VALUES (1, 'legacy');");
      const insert = legacy.prepare("INSERT INTO records (namespace, scope, id, value) VALUES ('local-auth', ?, ?, ?)");
      const oldId = "00000000-0000-4000-8000-000000000001";
      insert.run("user", oldId, JSON.stringify({ id: oldId, email: "old@example.test" }));
      insert.run("session", "00000000-0000-4000-8000-000000000002", JSON.stringify({ userId: oldId }));
    } finally {
      legacy.close();
    }
    assert.throws(() => createTerraScaleSqliteRuntime({ sqlite: { enabled: true, databasePath: fixture.databasePath } }), /TS-754: unsupported local SQLite format.*delete.*fresh ULID storage/u);
    for (const suffix of ["", "-wal", "-shm"])
      await rm(`${fixture.databasePath}${suffix}`, { force: true });
    fresh = createTerraScaleSqliteRuntime({ sqlite: { enabled: true, databasePath: fixture.databasePath } });
    assert.ok(isValidId(fresh.scope.tenant));
    assert.ok(isValidId(fresh.scope.database));
    const adapter = (await fresh.createBetterAuthAdapter())(options);
    assert.equal(await adapter.count({ model: "user" }), 0);
    assert.equal(await adapter.count({ model: "session" }), 0);
    const created = /** @type {{ id: string }} */ (await adapter.create({ model: "user", data: user("fresh@example.test") }));
    assert.ok(isValidId(created.id));
  } finally {
    await fresh?.close();
    await fixture.close();
  }
});

test.skipIf(!localSqliteUnitTestsAllowed)("scoped entries preserve record keys and exclude unrelated models and namespaces", () => {
  const store = new TerraScaleSqliteStore(":memory:");
  try {
    const first = "01ARZ3NDEKTSV4RRFFQ6000001";
    const second = "01ARZ3NDEKTSV4RRFFQ6000002";
    store.put("local-auth", "user", second, { email: "second@example.test" });
    store.put("local-auth", "user", first, { email: "first@example.test" });
    store.put("local-auth", "session", first, { token: "unrelated-session" });
    store.put("other", "user", first, { email: "unrelated@example.test" });
    assert.deepEqual(store.listEntries("local-auth", "user"), [
      { id: first, value: { email: "first@example.test" } },
      { id: second, value: { email: "second@example.test" } },
    ]);
    assert.deepEqual(store.listEntries("local-auth", "account"), []);
  } finally {
    store.close();
  }
});

test.skipIf(!localSqliteUnitTestsAllowed)("local auth persists one ULID key and rejects UUID or prefixed identities", async () => {
  const store = new TerraScaleSqliteStore(":memory:");
  try {
    const factory = createLocalBetterAuthAdapter(store);
    const adapter = factory(options);
    const created = /** @type {{ id: string }} */ (await adapter.create({ model: "user", data: user("ulid@example.test") }));
    assert.ok(isValidId(created.id));
    const record = store.listNamespace("local-auth")[0];
    assert.equal(record?.id, created.id);
    assert.equal(Object.hasOwn(/** @type {object} */ (record?.value), "id"), false);
    assert.equal((/** @type {{ id: string }} */ (await adapter.findOne({ model: "user", where: [{ field: "id", value: created.id }] }))).id, created.id);
    for (const id of ["usr_1", "00000000-0000-4000-8000-000000000001", created.id.toLowerCase()])
      await assert.rejects(adapter.create({ model: "user", data: { ...user("invalid@example.test"), id }, forceAllowId: true }), /ULID/u);
    assert.throws(() => factory({ ...options, advanced: { database: { generateId: "uuid" } } }), /ULID/u);
    assert.equal(await adapter.count({ model: "user" }), 1);
  } finally {
    store.close();
  }
});

test.skipIf(!localSqliteUnitTestsAllowed)(
  "pinned local factory converts dates, supports custom names and enforces unique writes",
  async () => {
    const fixture = await localFixture();
    try {
      const adapter = (await fixture.runtime.createBetterAuthAdapter())({
        ...options,
        user: { modelName: "members", fields: { email: "email_address" } },
      });
      const created = await adapter.create({
        model: "user",
        data: user("user@example.test"),
      });
      assert.ok(created.createdAt instanceof Date);
      assert.equal(typeof /** @type {{ id?: string }} */ (created).id, "string");
      const found = /** @type {{ email: string } | null} */ (
        await adapter.findOne({
          model: "user",
          where: [{ field: "email", value: "user@example.test" }],
        })
      );
      assert.equal(found?.email, "user@example.test");
      const competing = await Promise.allSettled(
        Array.from({ length: 8 }, () =>
          adapter.create({ model: "user", data: user("unique@example.test") }),
        ),
      );
      assert.equal(
        competing.filter((result) => result.status === "fulfilled").length,
        1,
      );
      assert.equal(await adapter.count({ model: "user" }), 2);
      const page = /** @type {{ email: string }[]} */ (
        await adapter.findMany({
          model: "user",
          sortBy: { field: "email", direction: "asc" },
          limit: 1,
          offset: 1,
        })
      );
      assert.equal(page.length, 1);
    } finally {
      await fixture.close();
    }
  },
);

test.skipIf(!localSqliteUnitTestsAllowed)(
  "local one-time consume is atomic and an async callback rolls back after an awaited failure",
  async () => {
    const fixture = await localFixture();
    try {
      const adapter = (await fixture.runtime.createBetterAuthAdapter())(
        options,
      );
      const record = await adapter.create({
        model: "verification",
        data: {
          identifier: "one-time",
          value: "secret",
          expiresAt: new Date(Date.now() + 60_000),
          createdAt: new Date(),
          updatedAt: new Date(),
        },
      });
      const competing = await Promise.all(
        Array.from({ length: 8 }, () =>
          adapter.consumeOne({
            model: "verification",
            where: [{ field: "id", value: /** @type {{ id: string }} */ (record).id }],
          }),
        ),
      );
      assert.equal(competing.filter((value) => value !== null).length, 1);
      /** @type {Omit<DBAdapter<BetterAuthOptions>, "transaction"> | undefined} */
      let escaped;
      await assert.rejects(
        adapter.transaction(async (transaction) => {
          escaped = transaction;
          await transaction.create({
            model: "user",
            data: user("rolled-back@example.test"),
          });
          await Promise.resolve();
          throw new Error("rollback requested");
        }),
        /rollback requested/u,
      );
      assert.equal(await adapter.count({ model: "user" }), 0);
      assert.ok(escaped !== undefined);
      await assert.rejects(escaped.count({ model: "user" }), /settled/u);
    } finally {
      await fixture.close();
    }
  },
);

test.skipIf(!localSqliteUnitTestsAllowed)(
  "caught auth failures poison the transaction and later operations cannot commit",
  async () => {
    const fixture = await localFixture();
    try {
      const adapter = (await fixture.runtime.createBetterAuthAdapter())(
        options,
      );
      await adapter.create({
        model: "user",
        data: user("existing@example.test"),
      });
      await assert.rejects(
        adapter.transaction(async (transaction) => {
          await transaction.create({
            model: "user",
            data: user("must-rollback@example.test"),
          });
          await transaction
            .create({ model: "user", data: user("existing@example.test") })
            .catch(() => undefined);
          return "caught";
        }),
        /unique/u,
      );
      assert.equal(await adapter.count({ model: "user" }), 1);
      const updated = await adapter.update({
        model: "user",
        where: [
          { field: "email", value: "existing@example.test" },
          { field: "name", value: "wrong" },
        ],
        update: { name: "bad" },
      });
      assert.equal(updated, null);
    } finally {
      await fixture.close();
    }
  },
);
