import { defineConfig } from "vitest/config";

export default defineConfig({
  test: {
    include: ["src/**/*.test.js", "test/**/*.test.js", "scripts/**/*.test.js"],
    // The release suites shell out to npm ci/pack; they need far more than
    // Vitest's 5s default.
    testTimeout: 300_000,
  },
});
