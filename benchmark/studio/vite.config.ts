import tailwindcss from "@tailwindcss/vite";
import { defineConfig } from "vite-plus";
import vinext from "vinext";

// The studio is a local operator tool (charter PD-2026-08-26-11): it binds
// loopback only and has no authentication gate. The API base URL points at
// the local C# API host in production shape; during the studio wave it
// points at the in-repo fixture server (scripts/fixture-server.mjs).
const studioPort = Number(process.env.STUDIO_PORT ?? 3031);

export default defineConfig({
  plugins: [tailwindcss(), vinext({ precompress: true })],
  server: {
    host: "127.0.0.1",
    port: studioPort,
  },
  preview: {
    host: "127.0.0.1",
    port: studioPort,
  },
  resolve: {
    tsconfigPaths: true,
  },
  lint: {
    ignorePatterns: [
      "dist/**",
      ".next/**",
      ".vinext/**",
      "playwright-report/**",
      "test-results/**",
      "src/api/generated/**",
    ],
  },
  fmt: {
    // src/api/generated/client.ts is machine-generated; its formatting is the
    // generator's output (guarded by tests/client-fresh.test.mjs).
    ignorePatterns: ["dist/**", ".next/**", ".vinext/**", "src/api/generated/**"],
    semi: true,
    singleQuote: false,
  },
  run: {
    tasks: {
      // Every task runs with cache: false: under the current vp task runner
      // (0.3.0) with this project's vite-plus 0.2.6 install, vt's task-cache
      // replay path dies in "materialize vt_client_napi" (PermissionDenied)
      // for tasks that load the vite-plus addon (vinext build/check/dev).
      // Uncached tasks execute plainly and pass; revisit when the toolchain
      // versions realign (Dashboard.Next enables caching where it works).
      "vinext:dev": {
        command: "vinext dev",
        cache: false,
      },
      "vinext:build": {
        command: "vinext build",
        cache: false,
      },
      "vinext:start": {
        command: "node dist/standalone/server.js",
        cache: false,
      },
      "vinext:check": {
        command: "vinext check",
        cache: false,
      },
      "playwright:e2e": {
        command: "playwright test",
        cache: false,
      },
      "fixture:serve": {
        command: "node scripts/fixture-server.mjs",
        cache: false,
      },
      "conformance:vectors": {
        command: "node scripts/run-conformance.mjs",
        cache: false,
      },
      "generate:client": {
        command: "node scripts/generate-client.mjs",
        cache: false,
      },
      "unit:test": {
        command:
          "node --test tests/fixture-server.test.mjs tests/contract.test.mjs tests/client-fresh.test.mjs",
        cache: false,
      },
    },
  },
});
