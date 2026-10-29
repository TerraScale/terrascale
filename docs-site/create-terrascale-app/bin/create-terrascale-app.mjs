#!/usr/bin/env node
// create-terrascale-app scaffolder.
// Usage: node bin/create-terrascale-app.mjs my-app
// Copies template/ into ./my-app and prints cloud setup instructions.
// No network calls, no credentials needed to scaffold.
import { cpSync, existsSync, mkdirSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");
const templateDir = join(root, "template");

const targetArg = process.argv[2];
if (!targetArg || targetArg === "--help" || targetArg === "-h") {
  console.log("Usage: node bin/create-terrascale-app.mjs my-app");
  console.log("Scaffolds a Next.js + @terrascale/sdk starter.");
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
console.log("Next steps:");
console.log(`  cd ${targetArg}`);
console.log("  npm install");
console.log("  cp .env.example .env.local   # set the API key, project ID and database name");
console.log("  npm run dev");
console.log("");
console.log("Docs: https://docs.terrascale.tech/getting-started/");
