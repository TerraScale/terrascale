#!/usr/bin/env node

// Keep the legacy direct entry point aligned with the supported Playwright
// command while forwarding any CLI options to it.
import { spawnSync } from "node:child_process";

const result = spawnSync("npm", ["run", "e2e", "--", ...process.argv.slice(2)], {
  stdio: "inherit",
});

if (result.error) {
  console.error(`Unable to start npm run e2e: ${result.error.message}`);
  process.exitCode = 1;
} else {
  process.exitCode = result.status ?? 1;
}
