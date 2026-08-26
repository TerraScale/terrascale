import { defineConfig, devices } from "@playwright/test";

// Playwright harness for the studio (DB-02). Servers are booted on randomized
// loopback ports by e2e/global-setup (fixture servers + the built studio app);
// no fixed ports anywhere. Tests run strictly serially against one shared
// fixture so control-behavior changes (status-slow/fail, session-gate) cannot
// interleave with each other.
export default defineConfig({
  testDir: "./e2e",
  testMatch: "**/*.e2e.ts",
  fullyParallel: false,
  workers: 1,
  timeout: 120_000,
  expect: {
    timeout: 10_000,
  },
  reporter: [["list"]],
  globalSetup: "./e2e/global-setup.ts",
  globalTeardown: "./e2e/global-teardown.ts",
  use: {
    trace: "retain-on-failure",
  },
  projects: [
    {
      name: "chromium",
      use: { ...devices["Desktop Chrome"] },
    },
  ],
});
