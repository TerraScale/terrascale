import { defineConfig } from "vitest/config";

export default defineConfig({
  test: {
    include: ["slow/**/*.test.js"],
    testTimeout: 300_000,
  },
});
