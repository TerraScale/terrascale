// Operator session bootstrap: host-enforced by contract, simulated by the
// fixture. Unauthenticated access redirects to the host's login flow; no
// credentials appear in client traffic or console output.

import { expect, test } from "@playwright/test";

import { fixture } from "./fixture-control";
import { readRuntimeFile } from "./runtime";

const CREDENTIAL_SHAPED = /password|credential|secret|token|apikey|api[_-]key/i;

test.describe("operator session bootstrap", () => {
  test("unauthenticated access redirects to the host's login flow (fixture-simulated)", async ({
    page,
  }) => {
    const { studioUrl, fixtureUrl } = readRuntimeFile();
    const { control } = fixture(fixtureUrl);
    await control({ behavior: "session-gate" });
    try {
      await page.goto(studioUrl);
      // The bootstrap honors the host's loginUrl. vinext serves the redirect
      // as a one-second meta refresh plus a client-side redirect, so the
      // browser lands on the fixture's simulated host login flow shortly
      // after the initial response.
      await expect.poll(() => page.url(), { timeout: 10_000 }).toBe(`${fixtureUrl}/fixture/login`);
      await expect(
        page.getByRole("heading", { name: "Host login flow (simulated)" }),
      ).toBeVisible();
    } finally {
      await control({ behavior: "session-open" });
    }
  });

  test("the ungated default renders engine status after the gate lifts", async ({ page }) => {
    const { studioUrl, fixtureUrl } = readRuntimeFile();
    const { control } = fixture(fixtureUrl);
    await control({ behavior: "session-gate" });
    await page.goto(studioUrl);
    await expect.poll(() => page.url(), { timeout: 10_000 }).toBe(`${fixtureUrl}/fixture/login`);

    await control({ behavior: "session-open" });
    await page.goto(studioUrl);
    await expect(page.getByTestId("engine-id")).toHaveText("fixture-engine-01");
  });

  test("carries no credentials in client traffic or console output", async ({ page }) => {
    const { studioUrl } = readRuntimeFile();
    const consoleMessages: string[] = [];
    const credentialHeaders: string[] = [];
    page.on("console", (message) => {
      consoleMessages.push(message.text());
    });
    page.on("request", (request) => {
      const headers = request.headers();
      if (headers["authorization"] !== undefined || headers["cookie"] !== undefined) {
        credentialHeaders.push(request.url());
      }
    });

    await page.goto(studioUrl);
    await expect(page.getByTestId("engine-id")).toBeVisible();
    await page.goto(`${studioUrl}/live`);
    await expect(page.getByTestId("tick-connection-state")).toHaveAttribute(
      "data-connection-state",
      "live",
      { timeout: 15_000 },
    );

    const leaking = consoleMessages.filter((message) => CREDENTIAL_SHAPED.test(message));
    expect(leaking, "console output must not contain credential-shaped values").toEqual([]);
    expect(credentialHeaders, "no request may carry an authorization header or cookie").toEqual([]);
  });
});
