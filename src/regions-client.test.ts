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
      { regions: [{ ...gru, name: "" }], hosts: { GRU: "https://gru.api.terrascale.tech" } },
      { regions: [{ ...gru, name: "x".repeat(129) }], hosts: { GRU: "https://gru.api.terrascale.tech" } },
      { regions: [{ ...gru, country: "usa" }], hosts: { GRU: "https://gru.api.terrascale.tech" } },
      { regions: [{ ...gru, country: "U" }], hosts: { GRU: "https://gru.api.terrascale.tech" } },
      { regions: [{ ...gru, latitude: -90.001 }], hosts: { GRU: "https://gru.api.terrascale.tech" } },
      { regions: [{ ...gru, latitude: 90.001 }], hosts: { GRU: "https://gru.api.terrascale.tech" } },
      { regions: [{ ...gru, longitude: -180.001 }], hosts: { GRU: "https://gru.api.terrascale.tech" } },
      { regions: [{ ...gru, longitude: 180.001 }], hosts: { GRU: "https://gru.api.terrascale.tech" } },
      { regions: [{ ...gru, available: "yes" }] },
      { regions: [{ ...gru, ociRegionCode: "sa-saopaulo-1" }] },
      { regions: [{ ...gru, clusterNodeTag: "A" }] },
      { regions: "GRU" },
      { regions: [gru] },
      { regions: [gru], hosts: {} },
      { regions: [gru], hosts: { GRU: 42 } },
      { regions: [gru], hosts: { gru: "https://gru.api.terrascale.tech" } },
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
        hosts: { GRU: "https://gru.api.terrascale.tech" },
      }),
    });
    const result = await client.regions.list();
    assert.equal(result.ok, true);
    if (result.ok) assert.deepEqual(result.value.hosts, { GRU: "https://gru.api.terrascale.tech" });
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
        hosts: { GRU: "https://gru.api.terrascale.tech" },
      }),
    });

    const result = await client.regions.list();
    assert.equal(result.ok, true);
    if (result.ok) assert.deepEqual(result.value.regions, [boundaryRegion]);
  });

});
