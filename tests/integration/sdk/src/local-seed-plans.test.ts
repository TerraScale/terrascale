import assert from "node:assert/strict";
import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "vitest";

import type { TerraScaleResult } from "./result.js";
import {
  createTerraScaleSqliteRuntime,
  type TerraScaleLocalPlan,
  type TerraScaleSqliteRuntime,
} from "./local/index.js";
import { installLocalSqliteTestEnvironment } from "./local/test-environment.js";

installLocalSqliteTestEnvironment();

function expectOk<T>(result: TerraScaleResult<T>): T {
  if (!result.ok) throw new Error(`${result.routeName}: ${result.error.message}`);
  return result.value;
}

async function withRuntime<T>(work: (runtime: TerraScaleSqliteRuntime) => Promise<T>): Promise<T> {
  const directory = await mkdtemp(join(tmpdir(), "terrascale-local-seed-plans-"));
  const runtime = createTerraScaleSqliteRuntime({
    sqlite: { enabled: true, databasePath: join(directory, "seed.sqlite") },
  });
  try {
    return await work(runtime);
  } finally {
    await runtime.close();
    await rm(directory, { recursive: true, force: true });
  }
}

test("seeds an organization per paid plan with the requested active plan", async () => {
  await withRuntime(async (runtime) => {
    const seeds = [
      await runtime.seedStarter(),
      await runtime.seedPro(),
      await runtime.seedBusiness(),
    ];
    assert.deepEqual(seeds.map((seed) => seed.plan), ["starter", "pro", "business"]);
    assert.equal(new Set(seeds.map((seed) => seed.organizationId)).size, seeds.length);

    const client = runtime.createClient({ accessToken: "local-test-token" });
    for (const seed of seeds) {
      const organization = expectOk(await client.organizations.get(seed.organizationId));
      assert.equal(organization.activePlan, seed.plan);
      assert.equal(organization.pendingPlan, null);
      assert.equal(organization.ownerUserIds.length, 1);
      assert.equal(expectOk(await client.databases.get(seed.databaseId, seed.projectId)).databaseId, seed.databaseId);
    }
  });
});

test("defaults the local seed to the free plan", async () => {
  await withRuntime(async (runtime) => {
    const seed = await runtime.seed();
    assert.equal(seed.plan, "free");
    const client = runtime.createClient({ accessToken: "local-test-token" });
    assert.equal(expectOk(await client.organizations.get(seed.organizationId)).activePlan, "free");
  });
});

test("rejects an unsupported seed plan", async () => {
  await withRuntime(async (runtime) => {
    await assert.rejects(
      () => runtime.seed("platinum" as unknown as TerraScaleLocalPlan),
      /plan must be one of/u,
    );
  });
});
