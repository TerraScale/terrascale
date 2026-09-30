import assert from "node:assert/strict";
import { describe, test } from "vitest";

import { createTerraScaleClient } from "./factory.js";

const organizationId = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
const gru = {
  code: "GRU",
  name: "Sao Paulo",
  country: "BR",
  latitude: -23.5475,
  longitude: -46.6361,
  available: true,
} as const;

describe("RegionsClient", () => {
  test("rejects invalid project selections before sending a request", () => {
    let requestCount = 0;
    const client = createTerraScaleClient({
      fetch: async () => {
        requestCount++;
        return Response.json({});
      },
    });

    assert.throws(
      () => client.organizations.createProject(organizationId, {
        name: "Invalid",
        regions: [],
        backupRegion: "GRU",
      }),
      /between 1 and 20/u,
    );
    assert.throws(
      () => client.organizations.createProject(organizationId, {
        name: "Invalid",
        regions: ["gru" as never],
        backupRegion: "GRU",
      }),
      /regions\[\] must be an uppercase Region code/u,
    );
    assert.throws(
      () => client.organizations.createProject(organizationId, {
        name: "Invalid",
        regions: ["GRU"],
        backupRegion: " GRU " as never,
      }),
      /backupRegion must be an uppercase Region code/u,
    );
    assert.equal(requestCount, 0);
  });

  test("turns hostile successful list bodies into typed invalid-response failures", async () => {
    const hostileBodies = [
      { regions: [], hosts: {} },
      { regions: [{ ...gru, code: "gru" }] },
      { regions: [{ ...gru, code: "ABC_" }] },
      { regions: [gru, { ...gru }] },
      { regions: [{ ...gru, name: "" }], hosts: { GRU: "https://gru.terrascale.tech" } },
      { regions: [{ ...gru, name: "x".repeat(129) }], hosts: { GRU: "https://gru.terrascale.tech" } },
      { regions: [{ ...gru, country: "usa" }], hosts: { GRU: "https://gru.terrascale.tech" } },
      { regions: [{ ...gru, country: "U" }], hosts: { GRU: "https://gru.terrascale.tech" } },
      { regions: [{ ...gru, latitude: -90.001 }], hosts: { GRU: "https://gru.terrascale.tech" } },
      { regions: [{ ...gru, latitude: 90.001 }], hosts: { GRU: "https://gru.terrascale.tech" } },
      { regions: [{ ...gru, longitude: -180.001 }], hosts: { GRU: "https://gru.terrascale.tech" } },
      { regions: [{ ...gru, longitude: 180.001 }], hosts: { GRU: "https://gru.terrascale.tech" } },
      { regions: [{ ...gru, available: "yes" }] },
      { regions: [{ ...gru, ociRegionCode: "sa-saopaulo-1" }] },
      { regions: [{ ...gru, clusterNodeTag: "A" }] },
      { regions: [{ ...gru, extra: true }], hosts: { GRU: "https://gru.terrascale.tech" } },
      { regions: [gru], hosts: { GRU: "https://gru.terrascale.tech" }, extra: true },
      { regions: "GRU" },
      { regions: [gru] },
      { regions: [gru], hosts: {} },
      { regions: [gru], hosts: { GRU: 42 } },
      { regions: [gru], hosts: { gru: "https://gru.terrascale.tech" } },
    ];

    for (const body of hostileBodies) {
      const client = createTerraScaleClient({
        fetch: async () => Response.json(body, { headers: { "x-request-id": "region-request" } }),
      });
      const result = await client.regions.list();
      assert.equal(result.ok, false, JSON.stringify(body));
      if (!result.ok) {
        assert.equal(result.status, 200);
        assert.equal(result.error.kind, "invalid-response");
        assert.equal(result.error.code, "invalid_response");
        assert.equal(result.error.requestId, "region-request");
        assert.deepEqual(result.body, body);
      }
    }
  });

  test("preserves the required region host map", async () => {
    const client = createTerraScaleClient({
      fetch: async () => Response.json({
        regions: [gru],
        hosts: { GRU: "https://gru.terrascale.tech" },
      }),
    });
    const result = await client.regions.list();
    assert.equal(result.ok, true);
    if (result.ok) assert.deepEqual(result.value.hosts, { GRU: "https://gru.terrascale.tech" });
  });

  test("accepts the published inclusive region bounds", async () => {
    const boundaryRegion = {
      ...gru,
      name: "x".repeat(128),
      country: "AZ",
      latitude: -90,
      longitude: 180,
    };
    const client = createTerraScaleClient({
      fetch: async () => Response.json({
        regions: [boundaryRegion],
        hosts: { GRU: "https://gru.terrascale.tech" },
      }),
    });

    const result = await client.regions.list();
    assert.equal(result.ok, true);
    if (result.ok) assert.deepEqual(result.value.regions, [boundaryRegion]);
  });

  test("decodes the member health contract, including unhealthy HTTP 503", async () => {
    const client = createTerraScaleClient({
      fetch: async () => Response.json(
        { region: "GRU", healthy: false, version: "terrabase-1.2.3" },
        { status: 503 },
      ),
    });
    const result = await client.regions.health();
    assert.equal(result.ok, true);
    if (result.ok) {
      assert.equal(result.status, 503);
      assert.deepEqual(result.value, {
        region: "GRU",
        healthy: false,
        version: "terrabase-1.2.3",
      });
    }
  });

  test("rejects health responses without the required build identity", async () => {
    const client = createTerraScaleClient({
      fetch: async () => Response.json({ region: "GRU", healthy: true, version: "" }),
    });
    const result = await client.regions.health();
    assert.equal(result.ok, false);
    if (!result.ok) {
      assert.equal(result.error.kind, "invalid-response");
      assert.match(result.error.message, /version must not be empty/u);
    }
  });

});
