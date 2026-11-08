import { describe, expect, it } from "vitest";
import { loadSnapshot } from "../db/reads";
import type { Env } from "../env";
import { DAY } from "../format";
import { overview, snapshotWindow } from "../overview";
import { testD1 } from "../testing/d1";
import { bars } from "./components";
import { homePage } from "./home";
import { compactBarData, document } from "./layout";

const now = Date.UTC(2026, 9, 23, 12);

describe("home HTML budget", () => {
  it("keeps the complete catalog and full 90-day charts below 150,000 UTF-8 bytes", async () => {
    const db = testD1();
    const snapshot = await loadSnapshot({ STATUS_DB: db } as unknown as Env, snapshotWindow(now, "90d"));
    snapshot.components.forEach((component) => {
      component.lifecycle = "active";
    });
    snapshot.endpoints.forEach((endpoint) => {
      endpoint.liveSince = "2026-01-01T00:00:00Z";
    });
    snapshot.latest = snapshot.endpoints.map((endpoint, index) => ({
      endpointId: endpoint.id,
      at: new Date(now).toISOString(),
      state: "operational",
      ok: 4,
      total: 4,
      latencyMs: 40 + index,
      results: [],
    }));
    snapshot.days = snapshot.endpoints.flatMap((endpoint, endpointIndex) =>
      Array.from({ length: 90 }, (_, index) => ({
        endpointId: endpoint.id,
        start: new Date(now - (now % DAY) - (89 - index) * DAY).toISOString(),
        checks: 2800 + index,
        failures: index % 17,
        degraded: 0,
        latencySum: (40 + endpointIndex + (index % 7)) * (2800 + index),
        latencyMax: 100,
        hist: "[]",
      })),
    );
    const view = overview(snapshot, now, "90d");
    const body = await compactBarData(document(homePage(view), 2026).value);

    expect(body).toContain("Response time");
    expect(body).toContain('class="st-map"');
    expect(body).toContain('class="st-bars st-bars-static st-bars-lg"');
    expect(Buffer.byteLength(body)).toBeLessThan(150_000);
    db.sqlite.close();
  });
});

async function unpack(markup: string): Promise<unknown> {
  const encoded = markup.match(/data-bar-data>([^<]+)</)?.[1];
  expect(encoded).toBeTruthy();
  const bytes = Uint8Array.from(atob(encoded!), (char) => char.charCodeAt(0));
  return new Response(new Blob([bytes]).stream().pipeThrough(new DecompressionStream("gzip"))).json();
}

it("round-trips tooltip content and exact irregular/partial bucket boundaries safely", async () => {
  const list = [
    { start: now, end: now + 3600000, state: "operational" as const },
    { start: now + 3600000, end: now + 5400000, state: "degraded" as const },
    { start: now + 6000000, end: now + 6300000, state: "none" as const },
  ];
  const card = {
    rows: [["Checks", "1 of 2 up"]] as [string, string][],
    notes: ['</script><script>alert("x")</script> & São Paulo'],
  };
  const source = `<main>${bars(list, { label: "Partial buckets", card: () => card }).value}</main>`;
  expect(source).not.toContain("</script><script>alert");
  expect(source).toContain("<title>Degraded performance:");
  const markup = await compactBarData(source);
  expect(markup).not.toContain("data-bar-record");
  expect(await unpack(markup)).toEqual([
    list.map((bucket) => ({
      state: [
        bucket.state,
        bucket.state === "operational"
          ? "Operational"
          : bucket.state === "degraded"
            ? "Degraded performance"
            : "No data",
      ],
      from: bucket.start,
      to: bucket.end,
      card,
    })),
  ]);
  expect(await compactBarData(source.replace("<main>", "").replace("</main>", ""))).toBe(
    source.replace("<main>", "").replace("</main>", ""),
  );
});
