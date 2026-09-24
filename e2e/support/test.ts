import { expect, test as playwrightTest } from "@playwright/test";
import type { Page } from "@playwright/test";

export { expect };

export const test = playwrightTest.extend({
  page: async ({ context }, use, testInfo) => {
    const page = await context.newPage();
    const errors: string[] = [];
    page.on("console", (message) => {
      if (message.type() === "error") errors.push(`console: ${message.text()}`);
    });
    page.on("pageerror", (error) => errors.push(`pageerror: ${error.message}`));

    try {
      await use(page);
    } finally {
      if (errors.length > 0) {
        await testInfo.attach("browser-console-errors.txt", {
          body: errors.join("\n"),
          contentType: "text/plain",
        });
      }
    }

    expect(errors, "browser console and uncaught page errors").toEqual([]);
  },
});

export function observeBrowserErrors(page: Page): string[] {
  const errors: string[] = [];
  page.on("console", (message) => {
    if (message.type() === "error") errors.push(`console: ${message.text()}`);
  });
  page.on("pageerror", (error) => errors.push(`pageerror: ${error.message}`));
  return errors;
}
