import assert from "node:assert/strict";
import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "vitest";

import { createTerraScaleSqliteRuntime } from "./local/index.js";
import { installLocalSqliteTestEnvironment } from "./local/test-environment.js";

installLocalSqliteTestEnvironment();

test("local Region routes enforce the public RegionCode contract", async () => {
  const directory = await mkdtemp(join(tmpdir(), "terrascale-local-regions-"));
  const runtime = createTerraScaleSqliteRuntime({
    sqlite: { enabled: true, databasePath: join(directory, "local.sqlite") },
  });

  try {
    const address = await runtime.start({ host: "127.0.0.1", port: 0 });
    const listResponse = await fetch(`${address.origin}/api/v1/regions`);
    assert.equal(listResponse.status, 200);
    const list = await listResponse.json() as { readonly regions: readonly Record<string, unknown>[] };
    assert.deepEqual(list.regions.map((region) => region.code), ["GRU", "SFO", "FRA"]);
    for (const region of list.regions) {
      assert.equal(Object.hasOwn(region, "ociRegionCode"), false);
      assert.equal(Object.hasOwn(region, "clusterNodeTag"), false);
    }

    const detailsResponse = await fetch(`${address.origin}/api/v1/regions/GRU`);
    assert.equal(detailsResponse.status, 200);
    const details = await detailsResponse.json() as Record<string, unknown>;
    assert.equal(details.code, "GRU");
    assert.equal(Object.hasOwn(details, "ociRegionCode"), false);
    assert.equal(Object.hasOwn(details, "clusterNodeTag"), false);

    for (const code of ["gru", "SJC", "%20GRU", "GRU%20", "GR", "GRUU", "%E4%BA%AC", "JFK"]) {
      const response = await fetch(`${address.origin}/api/v1/regions/${code}`);
      assert.equal(response.status, 404, code);
    }

    const organizationResponse = await fetch(`${address.origin}/api/v1/organizations`, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ name: "Region validation" }),
    });
    assert.equal(organizationResponse.status, 201);
    const organization = await organizationResponse.json() as { readonly organizationId: string };

    for (const body of [
      { name: "Invalid", regions: [], backupRegion: "GRU" },
      { name: "Invalid", regions: ["gru"], backupRegion: "GRU" },
      { name: "Invalid", regions: ["GRU"], backupRegion: " GRU " },
      { name: "Invalid", regions: ["SJC"], backupRegion: "SJC" },
    ]) {
      const response = await fetch(
        `${address.origin}/api/v1/organizations/${organization.organizationId}/projects`,
        {
          method: "POST",
          headers: { "content-type": "application/json" },
          body: JSON.stringify(body),
        },
      );
      assert.equal(response.status, 400, JSON.stringify(body));
    }
  } finally {
    await runtime.close();
    await rm(directory, { recursive: true, force: true });
  }
});
