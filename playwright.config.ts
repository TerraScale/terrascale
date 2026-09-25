import { defineConfig } from "@playwright/test";

// Set DASHBOARD_E2E_BASE_URL to exercise an already-running app. Local E2E
// runs use the rewrite's dedicated port so they do not collide with the
// existing dashboard worktree.
const port = Number(process.env.DASHBOARD_E2E_PORT ?? 5481);
const baseURL = process.env.DASHBOARD_E2E_BASE_URL ?? `http://127.0.0.1:${port}`;

export default defineConfig({
  testDir: ".",
  testMatch: ["e2e/**/*.spec.ts", "tests/integration/**/*.spec.ts"],
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
  ...(process.env.DASHBOARD_E2E_BASE_URL
    ? {}
    : {
        webServer: {
          command: "go run -mod=vendor ./cmd/dashboard",
          url: `${baseURL}/health/ready`,
          reuseExistingServer: false,
          timeout: 120_000,
          stdout: "ignore",
          stderr: "pipe",
          env: {
            DASHBOARD_ADDR: `0.0.0.0:${port}`,
            DASHBOARD_DEV: "1",
            TS_DASHBOARD_PRODUCTION_DEMO: "1",
            DASHBOARD_DEMO_ADMIN_EMAIL: process.env.DASHBOARD_E2E_ADMIN_EMAIL ?? process.env.DASHBOARD_DEMO_ADMIN_EMAIL ?? "e2e-admin-seed@example.test",
            DASHBOARD_DEMO_ADMIN_PASSWORD: process.env.DASHBOARD_E2E_ADMIN_PASSWORD ?? process.env.DASHBOARD_DEMO_ADMIN_PASSWORD ?? "E2e-admin-seed-123!",
          },
        },
      }),
});
