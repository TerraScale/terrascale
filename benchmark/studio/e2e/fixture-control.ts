// Shared helpers for the e2e specs: fixture control and counter reading.

import { expect, type Page } from "@playwright/test";

export interface Fixture {
  readonly control: (body: Record<string, unknown>) => Promise<void>;
}

export function fixture(baseUrl: string): Fixture {
  return {
    async control(body: Record<string, unknown>): Promise<void> {
      const response = await fetch(`${baseUrl}/api/v2/_fixture/control`, {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify(body),
      });
      expect(response.ok, `fixture control ${JSON.stringify(body)} failed`).toBeTruthy();
    },
  };
}

/**
 * Reads a rendered tick counter value (e.g. "Ticks", "Replayed") as a number.
 * `scope` narrows the search when several live panels render the same label
 * (the DB-04 changes feed and the tick feed both carry "Reconnects").
 */
export async function readCounter(
  page: Page,
  label: string,
  scope?: { testId: string },
): Promise<number> {
  const root = scope === undefined ? page.locator("body") : page.getByTestId(scope.testId);
  const text = await root.locator(`[data-counter="${label}"] dd`).first().textContent();
  if (text === null) {
    throw new Error(`counter ${label} not rendered`);
  }
  return Number.parseInt(text.replace(/,/g, ""), 10);
}
