import assert from "node:assert/strict";
import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "vitest";
import { createTerraScaleBetterAuthAdapter } from "./better-auth.js";
import { createLocalBetterAuthDatabaseOperations } from "./local/better-auth.js";
import { TerraScaleSqliteStore } from "./local/store.js";

test("local Better Auth records survive a SQLite runtime restart", async () => {
  const directory = await mkdtemp(join(tmpdir(), "terrascale-auth-sqlite-"));
  const databasePath = join(directory, "auth.sqlite");
  try {
    const firstStore = new TerraScaleSqliteStore(databasePath);
    const first = createTerraScaleBetterAuthAdapter(createLocalBetterAuthDatabaseOperations(firstStore));
    const created = await first.create<{ id: string; email: string }>({
      model: "user",
      data: { email: "persistent@example.test" },
    });
    const account = await first.create<{ id: string; userId: string; providerId: string }>({
      model: "account",
      data: { userId: created.id, providerId: "credential" },
    });
    firstStore.close();

    const secondStore = new TerraScaleSqliteStore(databasePath);
    const second = createTerraScaleBetterAuthAdapter(createLocalBetterAuthDatabaseOperations(secondStore));
    assert.deepEqual(await second.findOne<{ account: unknown[] }>({
      model: "user",
      where: [{ field: "email", value: "persistent@example.test" }],
      join: { account: true },
    }), { ...created, account: [account] });
    secondStore.close();
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});
