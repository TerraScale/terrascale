import { cloudflare } from "@cloudflare/vite-plugin";
import { defineConfig, type UserConfig as ViteConfig } from "vite";
import type { UserConfig } from "vite-plus";

export default defineConfig((): ViteConfig & Pick<UserConfig, "fmt"> => ({
  plugins: [cloudflare()],
  server: { cors: false },
  fmt: {
    printWidth: 120,
    ignorePatterns: ["AGENTS.md", "CLAUDE.md"],
  },
}));
