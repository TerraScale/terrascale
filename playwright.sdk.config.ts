import { defineConfig } from "@playwright/test";

export default defineConfig({
  testDir: ".",
  testMatch: "tests/integration/tsgo-real-stack.spec.ts",
  timeout: 120_000,
  expect: { timeout: 10_000 },
  fullyParallel: false,
  workers: 1,
  reporter: "list",
  use: {
    trace: "retain-on-failure",
    screenshot: "only-on-failure",
  },
});
