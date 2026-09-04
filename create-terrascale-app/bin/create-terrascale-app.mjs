#!/usr/bin/env node
// create-terrascale-app stub (P-8/D-56/D-74).
// Usage: npx create-terrascale-app@latest my-app
// Copies template/ into ./my-app and prints the 5-minute flow.
// No network calls, no credentials needed to scaffold.
import { cpSync, existsSync, mkdirSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");
const templateDir = join(root, "template");

const targetArg = process.argv[2];
if (!targetArg || targetArg === "--help" || targetArg === "-h") {
  console.log("Usage: npx create-terrascale-app@latest my-app");
  console.log("Scaffolds a Next.js + @terrascale/sdk starter with a first live query.");
  process.exit(targetArg ? 0 : 1);
}

const target = resolve(process.cwd(), targetArg);
if (existsSync(target)) {
  console.error(`FAIL: ${targetArg} already exists; choose an empty directory name.`);
  process.exit(1);
}
if (!existsSync(templateDir)) {
  console.error("FAIL: template/ is missing next to the installed bin.");
  process.exit(1);
}

mkdirSync(target, { recursive: true });
cpSync(templateDir, target, { recursive: true });

console.log(`Created ${targetArg} from create-terrascale-app.`);
console.log("");
console.log("Next steps (under 5 minutes on a fresh machine, D-74):");
console.log(`  cd ${targetArg}`);
console.log("  npm install");
console.log("  # sign in when prompted (card required at signup for fraud screening;");
console.log("  # free plan needs no subscription; first paid month free during onboarding)");
console.log("  # from a ts-cli checkout: go install ./cmd/terrascale");
console.log("  terrascale dev <database> --watch=false --no-log-tail");
console.log("  # --seed is pending the published CLI seed contract");
console.log("  npm run dev   # attach the starter's live result view");
console.log("");
console.log("Docs: https://docs.terrascale.tech/getting-started/");
