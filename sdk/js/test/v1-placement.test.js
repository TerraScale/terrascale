import { readFileSync } from "node:fs";
import { expect, test } from "vitest";
import { createTerraBaseClient, TerraBaseV1Error } from "../src/index.js";

const fixture = (/** @type {string} */ name) =>
  JSON.parse(readFileSync(new URL(`../../dotnet/contracts/terrabase/placement/${name}.json`, import.meta.url), "utf8"));
const project = "01JA0000000000000000000001";
const database = "01JA0000000000000000000004";

/** @param {any[]} recordings @param {{ method: string; path: string; body: unknown }[]} seen */
function client(recordings, seen) {
  return createTerraBaseClient({ project, token: "header.payload.signature", baseUrl: "https://database.example.invalid",
    fetch: async (input, init) => {
      const request = new Request(input, init);
      const text = await request.text();
      seen.push({ method: request.method, path: new URL(request.url).pathname, body: text === "" ? null : JSON.parse(text) });
      const next = recordings.shift();
      return Response.json(next.response.body, { status: next.response.status, headers: next.response.headers ?? {} });
    },
  });
}

test("placement operations send and decode the recorded contract", async () => {
  const names = ["create-with-replicas", "create-all-regions", "set-replicas", "set-all-regions", "move-home"];
  const recordings = names.map(fixture);
  /** @type {{ method: string; path: string; body: unknown }[]} */
  const seen = [];
  const terrabase = client([...recordings], seen);
  const results = [
    await terrabase.createDatabase(project, "orders", { homeRegion: "gru", replicaRegions: ["sjc"] }),
    await terrabase.createDatabase(project, "orders", { homeRegion: "gru", allRegions: true }),
    await terrabase.setDatabasePlacement(project, database, { replicaRegions: ["fra", "sjc"] }),
    await terrabase.setDatabasePlacement(project, database, { allRegions: true }),
    await terrabase.moveDatabaseHome(project, database, "sjc"),
  ];
  expect(seen).toEqual(recordings.map(r => ({ method: r.request.method, path: r.request.path, body: r.request.body })));
  expect(results).toEqual(recordings.map(r => r.response.body));
  expect(results[4]).toMatchObject({ homeRegion: "sjc", replicaRegions: ["fra", "gru"], allRegions: false });
});

test("placement refuses ambiguous requests and retries a move only while it is unresolved", async () => {
  const refused = fixture("move-home-refused");
  const unresolved = { response: { status: 503, headers: { "retry-after": "0" },
    body: { error: { code: "move_unresolved", message: "The move outcome is being settled." } } } };
  /** @type {{ method: string; path: string; body: unknown }[]} */
  const seen = [];
  const terrabase = client([unresolved, refused], seen);
  await expect(terrabase.setDatabasePlacement(project, database, { replicaRegions: ["sjc"], allRegions: true })).rejects.toThrow(TypeError);
  await expect(terrabase.setDatabasePlacement(project, database, {})).rejects.toThrow(TypeError);
  await expect(terrabase.createDatabase(project, "orders", { replicaRegions: ["sjc"], allRegions: true })).rejects.toThrow(TypeError);
  expect(seen).toHaveLength(0);
  const error = await terrabase.moveDatabaseHome(project, database, "fra").catch(e => e);
  expect(error).toBeInstanceOf(TerraBaseV1Error);
  expect(error).toMatchObject({ status: 400, code: "invalid_request" });
  expect(seen).toEqual([0, 1].map(() => ({ method: "POST", path: refused.request.path, body: refused.request.body })));
});
