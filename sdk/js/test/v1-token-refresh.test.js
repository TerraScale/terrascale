import { expect, test, vi } from "vitest";
import { createTerraBaseClient } from "../src/index.js";

const project = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
const apiKey = `01ARZ3NDEKTSV4RRFFQ69G5FAX_${"a".repeat(52)}`;

test("valid key tokens do not wait for a shared background exchange", async () => {
  let now = 0;
  const clock = vi.spyOn(performance, "now").mockImplementation(() => now);
  let calls = 0;
  let release = () => {};
  let entered = () => {};
  const held = new Promise(resolve => { release = () => resolve(undefined); });
  const started = new Promise(resolve => { entered = () => resolve(undefined); });
  const fetch = vi.fn(async input => {
    if (String(input).endsWith("/oauth/token")) {
      calls++;
      if (calls === 2) { entered(); await held; }
      return Response.json({ access_token: `${calls}.payload.signature`, expires_in: 60, token_type: "Bearer" });
    }
    return Response.json({ results: [{ columns: [], rows: [], rows_affected: 1, last_insert_rowid: 0 }] });
  });
  const client = createTerraBaseClient({ project, apiKey, baseUrl: "https://api.terrascale.tech", fetch });
  const write = () => client.database("app").execute("INSERT INTO rows (id) VALUES ('fresh')");
  try {
    await write();
    now = 30_000;
    await write();
    await started;
    now = 59_999;
    await write();
    now = 60_000;
    let done = false;
    const expired = write().then(() => { done = true; });
    await Promise.resolve();
    expect(done).toBe(false);
    release();
    await expired;
    expect(calls).toBe(2);
    expect(fetch.mock.calls.filter(([url]) => !String(url).endsWith("/oauth/token")).length).toBe(4);
  } finally {
    release();
    clock.mockRestore();
  }
});
