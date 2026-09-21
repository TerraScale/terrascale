import { defineConfig } from "@playwright/test";

// TS2_E2E_BASE_URL targets an already-running deployment (e.g. a PR preview)
// instead of booting a local server.
const port = Number(process.env.TS2_E2E_PORT ?? 5321);
const baseURL = process.env.TS2_E2E_BASE_URL ?? `http://127.0.0.1:${port}`;

export default defineConfig({
  testDir: "e2e",
  timeout: 60_000,
  expect: { timeout: 10_000 },
  fullyParallel: false,
  workers: 1,
  retries: process.env.CI ? 1 : 0,
  reporter: process.env.CI ? [["list"], ["html", { open: "never" }]] : "list",
  use: {
    baseURL,
    trace: "retain-on-failure",
    screenshot: "only-on-failure",
  },
  ...(process.env.TS2_E2E_BASE_URL
    ? {}
    : {
        webServer: {
          command: `TS2_E2E=1 dotnet run --project src/TerraScale.Dashboard --no-build --urls ${baseURL}`,
          url: `${baseURL}/health/live`,
          reuseExistingServer: false,
          timeout: 120_000,
          stdout: "ignore",
          stderr: "pipe",
        },
      }),
});
