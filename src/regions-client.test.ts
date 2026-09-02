import assert from "node:assert/strict";
import { describe, test } from "vitest";

import { createTerraScaleClient } from "./factory.js";
import { toUlid } from "./ids.js";

const organizationId = toUlid("01ARZ3NDEKTSV4RRFFQ69G5FAV");
const gru = {
  code: "GRU",
  name: "Sao Paulo",
  country: "BR",
  latitude: -23.5475,
  longitude: -46.6361,
  available: true,
} as const;

describe("RegionsClient", () => {
  test("sends and returns the exact registered RegionCode", async () => {
    const requests: Request[] = [];
    const client = createTerraScaleClient({
      apiBaseUrl: "https://api.terrascale.test",
      fetch: async (input) => {
        requests.push(input instanceof Request ? input : new Request(input));
        return Response.json(gru);
      },
    });

    const result = await client.regions.get("GRU");

    assert.equal(result.ok, true);
    if (result.ok) assert.deepEqual(result.value, gru);
    assert.equal(requests.length, 1);
    assert.equal(new URL(requests[0]!.url).pathname, "/api/v1/regions/GRU");
  });

  test("rejects invalid path codes before sending a request", async () => {
    let requestCount = 0;
    const client = createTerraScaleClient({
      fetch: async () => {
        requestCount++;
        return Response.json(gru);
      },
    });

    for (const code of ["gru", "SJC", " GRU", "GRU ", "GR", "GRUU", "京", "JFK"]) {
      await assert.rejects(
        client.regions.get(code as never),
        /regionCode must be exactly one of GRU, SFO, or FRA/u,
      );
    }
    assert.equal(requestCount, 0);
  });

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
      /regions\[\] must be exactly one of GRU, SFO, or FRA/u,
    );
    assert.throws(
      () => client.organizations.createProject(organizationId, {
        name: "Invalid",
        regions: ["GRU"],
        backupRegion: " GRU " as never,
      }),
      /backupRegion must be exactly one of GRU, SFO, or FRA/u,
    );
    assert.equal(requestCount, 0);
  });

  test("turns hostile successful list bodies into typed invalid-response failures", async () => {
    const hostileBodies = [
      { regions: [{ ...gru, code: "gru" }] },
      { regions: [{ ...gru, code: "JFK" }] },
      { regions: [gru, { ...gru }] },
      { regions: [{ ...gru, available: "yes" }] },
      { regions: [{ ...gru, ociRegionCode: "sa-saopaulo-1" }] },
      { regions: [{ ...gru, clusterNodeTag: "A" }] },
      { regions: "GRU" },
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

  test("rejects provider fields on a successful details body", async () => {
    const body = { ...gru, ociRegionCode: "sa-saopaulo-1", clusterNodeTag: "A" };
    const client = createTerraScaleClient({ fetch: async () => Response.json(body) });

    const result = await client.regions.get("GRU");

    assert.equal(result.ok, false);
    if (!result.ok) {
      assert.equal(result.error.kind, "invalid-response");
      assert.match(result.error.message, /forbidden provider field/u);
    }
  });

  test("keeps HTTP error projection unchanged", async () => {
    const client = createTerraScaleClient({
      fetch: async () => Response.json(
        { code: "region_not_found", message: "The Region does not exist." },
        { status: 404 },
      ),
    });

    const result = await client.regions.get("GRU");

    assert.equal(result.ok, false);
    if (!result.ok) {
      assert.equal(result.status, 404);
      assert.equal(result.error.kind, "not-found");
      assert.equal(result.error.code, "region_not_found");
      assert.equal(result.error.message, "The Region does not exist.");
    }
  });
});
