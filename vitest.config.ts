import { defineConfig } from "vitest/config";

export default defineConfig({
  test: {
    include: ["src/**/*.test.ts", "test/**/*.test.mjs"],
    // The release suites shell out to npm ci/build/pack; they need far more
    // than Vitest's 5s default.
    testTimeout: 300_000,
  },
});
