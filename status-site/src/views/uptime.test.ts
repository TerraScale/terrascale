import { expect, it } from "vitest";
import { loadSnapshot } from "../db/reads";
import type { Env } from "../env";
import { overview, snapshotWindow } from "../overview";
import { testD1 } from "../testing/d1";
import { uptimePage } from "./uptime";

it("publishes D1 measurements without an SLA promise or treating missing months as healthy", async () => {
  const db = testD1();
  const now = Date.parse("2026-10-23T12:00:00Z");
  const env = { STATUS_DB: db } as unknown as Env;
  const view = overview(await loadSnapshot(env, snapshotWindow(now, "90d")), now, "90d");
  const endpoint = view.endpoints[0]!;
  const periods = [
    {
      endpointId: endpoint.id,
      vantage: "all",
      start: "2026-10-23T00:00:00Z",
      checks: 10,
      failures: 2,
      degraded: 0,
      latencySum: 800,
      hist: "[]",
    },
  ];
  const body = String(uptimePage(view, periods, now).body);
  expect(body).toContain("Cloudflare D1");
  expect(body).toContain("No SLA during Public Alpha");
  expect(body).toContain("30 consecutive days of clean monitoring data");
  expect(body).toContain("Unmeasured time is excluded, not counted as healthy");
  expect(body).toContain("0 of 4 vantage points verified");
  expect(body).toContain('data-sla="measured">80.00%');
  expect(body).toContain('data-sla="none">—');
  expect(body).not.toContain('data-sla="met"');
  expect(body).not.toContain('data-sla="missed"');
  expect(body).not.toContain("99.90%");
  db.sqlite.close();
});
