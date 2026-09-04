// Verification for the docs build. The default run executes every snippet in
// the configured cloud branch and fails closed when it cannot qualify it;
// --sandbox=stub selects the deterministic local harness.
import { strict as assert } from "node:assert";
import { spawnSync } from "node:child_process";
import { existsSync, readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { canonicalEnvelope, runAll } from "./run-snippets.mjs";

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
if (stub) console.warn("LOCAL-ONLY: stub snippets are smoke tests; remote launch qualification is not passed.");
const heartbeat = Uint8Array.from([0x97, 0x01, 0x00, 0x00, 0x06, 0x00, 0x00, 0x90]);
assert.equal(canonicalEnvelope(heartbeat).kind, "heartbeat", "canonical indexed heartbeat decodes");
assert.throws(() => canonicalEnvelope(Uint8Array.from([0x81, 0xa1, 0x74, 0xa5, 0xa5, 0x64, 0x65, 0x6c, 0x74, 0x61])), /indexed envelope|trailing bytes/u, "legacy map frames fail closed");
assert.ok(results.length >= 3, "expected runnable CRUD, SQL, and subscribe snippets");
for (const name of ["crud.ts", "sql.ts", "subscribe.ts"]) {
  assert.ok(results.some(({ file }) => file === name), `${name} ran`);
}
for (const name of ["tenants.ts", "branches.ts", "backups.ts", "exports.ts"]) {
  assert.ok(!results.some(({ file }) => file === name), `${name} remains pending until its producer contract is published`);
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
assert.ok(starterPage.includes("TerraScaleSyncV1Client"), "starter uses the current reactive SDK client");
assert.ok(starterPage.includes("createTerraScaleClient"), "starter uses the current SDK factory");
assert.ok(starterPage.includes("databaseId"), "starter scopes reactive attach by databaseId");
assert.ok(!starterPage.includes("new TerraScale("), "starter does not use removed SDK constructor");
assert.ok(!starterPage.includes(".from("), "starter does not use removed collection facade");
assert.ok(read("create-terrascale-app/template/package.json").includes('"@terrascale/sdk": "0.3.0"'), "starter pins the current SDK version");
assert.ok(read("create-terrascale-app/template/terrascale.schema.json").includes('"historyRetentionDays": 7'), "starter schema uses canonical JSON shape");
assert.ok(read("scripts/run-snippets.mjs").includes("TERRASCALE_API_KEY"), "remote runner is credential-gated");
assert.ok(read("scripts/run-snippets.mjs").includes("launch gate not passed"), "missing real sandbox is an explicit failed launch gate");
assert.ok(read("scripts/run-snippets.mjs").includes("x-terrascale-view-capability"), "reactive requests carry the view capability");
assert.ok(!read("scripts/run-snippets.mjs").includes("frame.t"), "remote runner does not decode legacy JSON frame fields");

for (const [page, needles] of [
  ["docs/pricing.md", ["$1 per 1M writes", "$0.20 per GB-month per region", "billed once", "Branch allowance", "Metered for abuse guards"]],
  ["docs/limits.md", ["Max document", "1 MiB", "Abuse guards", "unlimited on every plan", "direct member read egress"]],
]) {
  assert.ok(existsSync(join(root, page)), `${page} exists`);
  const content = read(page);
  for (const needle of needles) assert.ok(content.includes(needle), `${page} mentions ${needle}`);
}

const llms = read("docs/public/llms.txt");
assert.ok(llms.includes("tenant, branch, backup, and export fixtures are retained but skipped"), "llms marks unpublished management fixtures as non-runnable");
assert.ok(!llms.includes("live subscriptions, and tenants run"), "llms does not claim unpublished tenant snippets run");
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
