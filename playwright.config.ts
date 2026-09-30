import { defineConfig, devices } from "@playwright/test";

delete process.env.NO_COLOR;
delete process.env.PLAYWRIGHT_SKIP_VALIDATE_HOST_REQUIREMENTS;

const localChromium = process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH;
const localChromiumLaunch = localChromium === undefined
  ? {}
  : { launchOptions: { executablePath: localChromium } };

export default defineConfig({
  testDir: "./browser-tests",
  fullyParallel: true,
  // Qualify every engine without booting one browser per test on the shared builder.
  workers: 4,
  forbidOnly: true,
  retries: 0,
  reporter: "line",
  use: {
    baseURL: "http://127.0.0.1:4173",
    trace: "retain-on-failure",
  },
  webServer: {
    command: "node scripts/serve-browser-tests.ts",
    url: "http://127.0.0.1:4173/browser-tests/fixture.html",
    reuseExistingServer: false,
  },
  projects: [
    {
      name: "chrome-current",
      use: { ...devices["Desktop Chrome"], ...localChromiumLaunch },
    },
    { name: "firefox-current", use: { ...devices["Desktop Firefox"] } },
    { name: "safari-current", use: { ...devices["Desktop Safari"] } },
    {
      name: "edge-current",
      use: { ...devices["Desktop Edge"], ...localChromiumLaunch },
    },
  ],
});
