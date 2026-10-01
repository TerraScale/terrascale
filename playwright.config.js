import { defineConfig, devices } from "@playwright/test";
import { availableParallelism } from "node:os";

delete process.env.NO_COLOR;
delete process.env.PLAYWRIGHT_SKIP_VALIDATE_HOST_REQUIREMENTS;

const chrome =
  process.env.PLAYWRIGHT_CHROME_EXECUTABLE_PATH?.trim() || undefined;
const edge = process.env.PLAYWRIGHT_EDGE_EXECUTABLE_PATH?.trim() || undefined;

export default defineConfig({
  testDir: "./browser-tests",
  fullyParallel: true,
  // Cap shared CI browser concurrency; local runs and --workers retain normal control.
  ...(process.env.CI
    ? { workers: Math.min(4, Math.max(1, Math.floor(availableParallelism() / 2))) }
    : {}),
  forbidOnly: true,
  retries: 0,
  reporter: "line",
  use: {
    baseURL: "http://127.0.0.1:4173",
    trace: "retain-on-failure",
  },
  webServer: {
    command: "node scripts/serve-browser-tests.js",
    url: "http://127.0.0.1:4173/browser-tests/fixture.html",
    reuseExistingServer: false,
  },
  projects: [
    {
      name: "chrome-current",
      use: {
        ...devices["Desktop Chrome"],
        channel: "chrome",
        ...(chrome === undefined
          ? {}
          : { launchOptions: { executablePath: chrome } }),
      },
    },
    { name: "firefox-current", use: { ...devices["Desktop Firefox"] } },
    { name: "webkit-current", use: { ...devices["Desktop Safari"] } },
    {
      name: "edge-current",
      use: {
        ...devices["Desktop Edge"],
        channel: "msedge",
        ...(edge === undefined
          ? {}
          : { launchOptions: { executablePath: edge } }),
      },
    },
  ],
});
