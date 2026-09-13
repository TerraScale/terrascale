// Verification for the docs build. Local checks select --sandbox=stub;
// --sandbox=remote is reserved for explicit cloud qualification and fails
// closed when its sandbox credentials are absent.
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
const heartbeat = Uint8Array.from([0x97, 0x01, 0x00, 0x00, 0x06, 0xcf, 0, 0, 0, 0, 0, 0, 0, 0, 0xcf, 0, 0, 0, 0, 0, 0, 0, 0, 0x90]);
assert.equal(canonicalEnvelope(heartbeat).kind, "heartbeat", "canonical indexed heartbeat decodes");
assert.throws(() => canonicalEnvelope(Uint8Array.from([0x81, 0xa1, 0x74, 0xa5, 0xa5, 0x64, 0x65, 0x6c, 0x74, 0x61])), /indexed envelope|trailing bytes|maps are forbidden/u, "legacy map frames fail closed");
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
assert.ok(read("scripts/run-snippets.mjs").includes("TERRASCALE_SANDBOX_API_KEY"), "remote runner is credential-gated");
assert.ok(read("scripts/run-snippets.mjs").includes("launch gate not passed"), "missing real sandbox is an explicit failed launch gate");
assert.ok(read("scripts/run-snippets.mjs").includes("x-terrascale-capability"), "reactive requests carry the view capability");
assert.ok(!read("scripts/run-snippets.mjs").includes("frame.t"), "remote runner does not decode legacy JSON frame fields");

for (const [page, needles] of [
  ["docs/pricing.md", ["$1 per 1M writes", "$0.20 per GB-month per region", "billed once", "Shared organization allowances", "Metered for abuse guards"]],
  ["docs/limits.md", ["Max document", "1 MiB", "Abuse guards", "unlimited on every plan", "direct member read egress"]],
]) {
  assert.ok(existsSync(join(root, page)), `${page} exists`);
  const content = read(page);
  for (const needle of needles) assert.ok(content.includes(needle), `${page} mentions ${needle}`);
}

const llms = read("docs/public/llms.txt");
assert.ok(llms.includes("database-scoped branch, backup, and export routes are published"), "llms distinguishes published database routes from skipped producer fixtures");
assert.ok(!llms.includes("live subscriptions, and tenants run"), "llms does not claim unpublished tenant snippets run");
for (const route of [
  "/", "/getting-started", "/getting-started/", "/concepts",
  "/concepts/replication", "/concepts/sync", "/concepts/sql", "/concepts/pricing-philosophy",
  "/guides/", "/pricing", "/pricing/", "/limits", "/snippets",
  "/blog/reads-are-free", "/blog/benchmarks", "/blog/replication", "/blog/database-per-customer",
  "/legal/", "/legal/terms", "/legal/privacy", "/legal/dpa", "/legal/security", "/legal/support",
  "/changelog", "sync.api.terrascale.tech",
]) assert.ok(llms.includes(route), `llms.txt lists ${route}`);

for (const retired of ["concepts/engines", "blog/why-sqlite"]) {
  assert.ok(!existsSync(join(root, "docs", `${retired}.md`)), `${retired} is outside public source`);
  assert.ok(!existsSync(join(root, "docs/.vitepress/dist", `${retired}.html`)), `${retired} is outside the site build`);
  assert.ok(!llms.includes(`/${retired}`), `${retired} is absent from llms navigation`);
}
assert.ok(!read("docs/.vitepress/config.ts").includes("https://status.terrascale.tech"), "deferred status page is not advertised in navigation");
assert.ok(read("docs/snippets/backups.ts").includes("Backups are unbilled"), "backup fixture agrees with billing policy");

for (const slug of ["reads-are-free", "benchmarks", "replication", "database-per-customer"]) {
  assert.ok(existsSync(join(root, `docs/blog/${slug}.md`)), `blog/${slug}.md exists`);
}

const pricingIndex = read("docs/pricing/index.md");
for (const needle of ["$1 per 1M", "$0.20 per GB-month", "| Writes / month | 100k", "no additional branch allowance", "30-day grace period", "billed once"]) {
  assert.ok(pricingIndex.includes(needle), `pricing/index.md agrees on ${needle}`);
}

console.log(`npm test: ${results.length} snippets + pricing/limits + llms route assertions passed.`);
