import { mkdirSync } from "node:fs";
import type { Page } from "@playwright/test";

const outputDirectory = "/tmp/dsh-198-after";

export async function captureResponsiveScreenshots(page: Page, name: string): Promise<void> {
  mkdirSync(outputDirectory, { recursive: true });
  const original = page.viewportSize();
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.screenshot({ path: `${outputDirectory}/${name}-desktop.png`, fullPage: true });
  await page.setViewportSize({ width: 390, height: 844 });
  await page.screenshot({ path: `${outputDirectory}/${name}-mobile.png`, fullPage: true });
  if (original) await page.setViewportSize(original);
}
