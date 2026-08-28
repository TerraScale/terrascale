// Live View: one SSE tick subscription with reconnect + checkpoint replay
// (dropped-connection handling) and fail-closed rejection of unknown frames,
// all exercised through the fixture's controlled behaviors.

import { expect, test } from "@playwright/test";

import { fixture, readCounter } from "./fixture-control";
import { readRuntimeFile } from "./runtime";

test.describe("studio live view", () => {
  test("renders live ticks and survives the fixture's dropped connection with checkpoint replay", async ({
    page,
  }) => {
    const { studioUrl } = readRuntimeFile();
    await page.goto(`${studioUrl}/live`);

    const state = page.getByTestId("tick-connection-state");
    await expect(state).toHaveAttribute("data-connection-state", "live", { timeout: 15_000 });
    await expect(page.getByTestId("tick-list").locator("li").first()).toBeVisible();

    const ticksBefore = await readCounter(page, "Ticks", { testId: "tick-counters" });
    expect(ticksBefore).toBeGreaterThan(0);

    // The fixture drops the connection after every 8 delivered deltas. The
    // drop itself is transient (the browser auto-reconnects within ~2s and
    // the live/reconnecting cycle repeats), so the durable outcomes are
    // asserted: the loss is observed and counted, delivery returns to live,
    // the missed identities are replayed, and ticks keep flowing.
    await expect
      .poll(async () => readCounter(page, "Reconnects", { testId: "tick-counters" }), {
        timeout: 20_000,
      })
      .toBeGreaterThanOrEqual(1);
    await expect(state).toHaveAttribute("data-connection-state", "live", { timeout: 20_000 });
    await expect
      .poll(async () => readCounter(page, "Replayed", { testId: "tick-counters" }), {
        timeout: 20_000,
      })
      .toBeGreaterThanOrEqual(1);
    await expect
      .poll(async () => readCounter(page, "Ticks", { testId: "tick-counters" }), {
        timeout: 20_000,
      })
      .toBeGreaterThan(ticksBefore);
  });

  test("counts and surfaces fail-closed rejection of an unknown frame type", async ({ page }) => {
    const { studioUrl, fixtureUrl } = readRuntimeFile();
    const { control } = fixture(fixtureUrl);
    await page.goto(`${studioUrl}/live`);

    const state = page.getByTestId("tick-connection-state");
    await expect(state).toHaveAttribute("data-connection-state", "live", { timeout: 15_000 });
    const ticksBefore = await readCounter(page, "Ticks", { testId: "tick-counters" });

    await control({ framePoison: true });
    await expect
      .poll(async () => readCounter(page, "Rejected", { testId: "tick-counters" }), {
        timeout: 20_000,
      })
      .toBeGreaterThanOrEqual(1);
    await expect(page.getByTestId("tick-last-error")).toContainText("unknown frame type");

    // Fail-closed rejection is counted, not fatal: the connection stays live.
    await expect(state).toHaveAttribute("data-connection-state", "live");
    await expect
      .poll(async () => readCounter(page, "Ticks", { testId: "tick-counters" }), {
        timeout: 20_000,
      })
      .toBeGreaterThan(ticksBefore);
  });

  test("resyncs honestly when the checkpoint was evicted, then keeps ticking on a fresh session", async ({
    page,
  }) => {
    // The resync fixture keeps only 2 buffered frames and drops after 5
    // deltas: the reconnect's checkpoint is evicted, so the host answers
    // resync.required and the client opens a fresh session.
    const { studioResyncUrl } = readRuntimeFile();
    await page.goto(`${studioResyncUrl}/live`);

    const state = page.getByTestId("tick-connection-state");
    await expect(state).toHaveAttribute("data-connection-state", "live", { timeout: 15_000 });

    await expect
      .poll(async () => readCounter(page, "Resyncs", { testId: "tick-counters" }), {
        timeout: 30_000,
      })
      .toBeGreaterThanOrEqual(1);
    await expect(page.getByTestId("tick-last-error")).toContainText("resync required");

    // The fresh session must resume live delivery.
    await expect(state).toHaveAttribute("data-connection-state", "live", { timeout: 15_000 });
    const ticksBefore = await readCounter(page, "Ticks", { testId: "tick-counters" });
    await expect
      .poll(async () => readCounter(page, "Ticks", { testId: "tick-counters" }), {
        timeout: 20_000,
      })
      .toBeGreaterThan(ticksBefore);
  });
});
