import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { cloudflare } from "@cloudflare/vite-plugin";
import type { UserConfig } from "vite-plus";
// Cloudflare and Vitest share upstream Vite types; Vite+ uses a separate core alias.
import { defineConfig, type ViteUserConfig } from "vitest/config";

const root = dirname(fileURLToPath(import.meta.url));

export default defineConfig((): ViteUserConfig & Pick<UserConfig, "fmt"> => ({
  plugins: process.env.VITEST
    ? []
    : [
        cloudflare({
          // Workers Builds sets the commit; baked into the build so any deploy command reports it.
          config: (config) => ({ vars: { ...config.vars, VERSION: process.env.WORKERS_CI_COMMIT_SHA ?? "unknown" } }),
          auxiliaryWorkers: (
            [
              ["sfo", "aws:us-west-1"],
              ["gru", "aws:sa-east-1"],
            ] as const
          ).map(([code, region]) => ({
            config: {
              name: `ts-status-vantage-${code}`,
              main: "src/vantage/worker.ts",
              compatibility_date: "2026-09-13",
              workers_dev: false,
              preview_urls: false,
              placement: { region },
              vars: { VERSION: process.env.WORKERS_CI_COMMIT_SHA ?? "unknown" },
            },
          })),
        }),
      ],
  css: { transformer: "lightningcss" },
  build: { cssMinify: "lightningcss" },
  // Reachable over the tailnet during development.
  server: { host: true },
  test: {
    environment: "node",
    include: ["src/**/*.test.ts"],
    alias: {
      "cloudflare:workers": join(root, "src/testing/cloudflare-workers.ts"),
    },
  },
  fmt: {
    printWidth: 120,
    // Template whitespace is rendered output, so html`` stays as written.
    embeddedLanguageFormatting: "off",
    // Human-owned rule files, and the design system's generated tokens file (kept verbatim).
    ignorePatterns: ["AGENTS.md", "CLAUDE.md", "src/styles/tokens.css"],
  },
}));
