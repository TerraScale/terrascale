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

/** Reads a rendered tick counter value (e.g. "Ticks", "Replayed") as a number. */
export async function readCounter(page: Page, label: string): Promise<number> {
  const text = await page.locator(`[data-counter="${label}"] dd`).textContent();
  if (text === null) {
    throw new Error(`counter ${label} not rendered`);
  }
  return Number.parseInt(text.replace(/,/g, ""), 10);
}
