import { defineConfig } from "vitest/config";

export default defineConfig({
  test: {
    include: ["src/**/*.test.ts", "test/**/*.test.ts", "scripts/**/*.test.ts"],
    // Installed-package checks and maintained Better Auth suites perform real I/O.
    testTimeout: 300_000,
    hookTimeout: 300_000,
  },
});
