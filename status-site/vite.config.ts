import { cloudflare } from "@cloudflare/vite-plugin";
import { defineConfig, type UserConfig as ViteConfig } from "vite";
import type { UserConfig } from "vite-plus";

export default defineConfig((): ViteConfig & Pick<UserConfig, "fmt"> => ({
  plugins: [
    cloudflare({
      config: (config) => ({ vars: { ...config.vars, VERSION: process.env.REVISION ?? "unknown" } }),
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
          vars: { VERSION: process.env.REVISION ?? "unknown" },
        },
      })),
    }),
  ],
  css: { transformer: "lightningcss" },
  build: { cssMinify: "lightningcss" },
  // Reachable over the tailnet during development.
  server: { host: true },
  fmt: {
    printWidth: 120,
    // Template whitespace is rendered output, so html`` stays as written.
    embeddedLanguageFormatting: "off",
    // Human-owned rule files, and the design system's generated tokens file (kept verbatim).
    ignorePatterns: ["AGENTS.md", "CLAUDE.md", "src/styles/tokens.css"],
  },
}));
