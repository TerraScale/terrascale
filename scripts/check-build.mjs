// Verification for the docs build. The default run executes every snippet in
// the configured cloud branch and fails closed when it cannot qualify it;
// --sandbox=stub selects the deterministic local harness.
import { strict as assert } from "node:assert";
import { spawnSync } from "node:child_process";
import { existsSync, readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { runAll } from "./run-snippets.mjs";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");
const read = (path) => readFileSync(join(root, path), "utf8");
const stub = process.argv.includes("--sandbox=stub");
if (stub && process.argv.includes("--sandbox=remote")) throw new Error("choose one sandbox mode");
const generated = spawnSync(process.execPath, [join(root, "scripts", "generate-pricing.mjs"), "--check"], {
  cwd: root,
  encoding: "utf8",
});
assert.equal(generated.status, 0, generated.stdout + generated.stderr);
const llmsGenerated = spawnSync(process.execPath, [join(root, "scripts", "generate-llms.mjs"), "--check"], {
  cwd: root,
  encoding: "utf8",
});
assert.equal(llmsGenerated.status, 0, llmsGenerated.stdout + llmsGenerated.stderr);
const results = await runAll(stub ? "stub" : "remote");
assert.ok(results.length >= 4, "expected four runnable snippets (crud/sql/subscribe/tenants)");
for (const name of ["crud.ts", "sql.ts", "subscribe.ts", "tenants.ts"]) {
  assert.ok(results.some(({ file }) => file === name), `${name} ran`);
}

// P-8: create-terrascale-app Next starter stub exists with its README,
// bin, template, and starter-owned AGENTS.md (D-74).
for (const file of [
  "create-terrascale-app/package.json",
  "create-terrascale-app/README.md",
  "create-terrascale-app/bin/create-terrascale-app.mjs",
  "create-terrascale-app/template/package.json",
  "create-terrascale-app/template/app/page.tsx",
  "create-terrascale-app/template/terrascale.schema.json",
  "create-terrascale-app/template/seed.ts",
  "create-terrascale-app/template/AGENTS.md",
]) assert.ok(existsSync(join(root, file)), `${file} exists`);
const starterReadme = read("create-terrascale-app/README.md");
for (const needle of ["npx create-terrascale-app", "terrascale dev", "AGENTS.md"]) {
  assert.ok(starterReadme.includes(needle), `create-app README mentions ${needle}`);
}
const starterPage = read("create-terrascale-app/template/app/page.tsx");
assert.ok(starterPage.includes(".subscribe("), "starter template runs a first live query");

for (const [page, needles] of [
  ["docs/pricing.md", ["$1 per 1M writes", "$0.20 per GB-month per region", "billed once", "Branch allowance", "Metered for abuse guards"]],
  ["docs/limits.md", ["Max document", "1 MiB", "Abuse guards", "unlimited on every plan", "direct member read egress"]],
]) {
  assert.ok(existsSync(join(root, page)), `${page} exists`);
  const content = read(page);
  for (const needle of needles) assert.ok(content.includes(needle), `${page} mentions ${needle}`);
}

const llms = read("docs/public/llms.txt");
for (const route of [
  "/", "/getting-started", "/getting-started/", "/concepts", "/concepts/engines",
  "/concepts/replication", "/concepts/sync", "/concepts/sql", "/concepts/pricing-philosophy",
  "/guides/", "/pricing", "/pricing/", "/limits", "/snippets", "/blog/why-sqlite",
  "/blog/reads-are-free", "/blog/benchmarks", "/blog/replication", "/blog/database-per-customer",
  "/legal/", "/legal/terms", "/legal/privacy", "/legal/dpa", "/legal/security", "/legal/support",
  "/changelog", "sync.api.terrascale.tech",
]) assert.ok(llms.includes(route), `llms.txt lists ${route}`);

for (const slug of ["why-sqlite", "reads-are-free", "benchmarks", "replication", "database-per-customer"]) {
  assert.ok(existsSync(join(root, `docs/blog/${slug}.md`)), `blog/${slug}.md exists`);
}

const pricingIndex = read("docs/pricing/index.md");
for (const needle of ["$1 per 1M", "$0.20 per GB-month", "100k writes", "1 GB", "crypto-shred", "billed once"]) {
  assert.ok(pricingIndex.includes(needle), `pricing/index.md agrees on ${needle}`);
}

console.log(`npm test: ${results.length} snippets + pricing/limits + llms route assertions passed.`);
