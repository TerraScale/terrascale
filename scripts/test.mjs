// Snippet test entry: runs the harness plus static docs assertions.
// `npm test` = this file (pretest already checks generated pages are fresh).
import { strict as assert } from "node:assert";
import { existsSync, readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { runAll } from "./run-snippets.mjs";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");
const read = (p) => readFileSync(join(root, p), "utf8");

const results = await runAll();
assert.ok(results.length >= 4, "expected at least 4 runnable snippets");

// L-1 draft: pricing/limits pages carry the closed decisions.
for (const [page, needles] of [
  ["docs/pricing.md", ["$1 per 1M writes", "$0.20 per GB-month per region", "billed once", "Branch allowance"]],
  ["docs/limits.md", ["Max document", "1 MiB", "Abuse guards", "unlimited on every plan"]],
]) {
  assert.ok(existsSync(join(root, page)), `${page} exists`);
  for (const n of needles) assert.ok(read(page).includes(n), `${page} mentions "${n}"`);
}

// P-8: llms.txt lists every route.
const llms = read("docs/public/llms.txt");
for (const r of ["/pricing", "/limits", "/snippets", "/blog/why-sqlite", "/blog/database-per-customer", "/guides/", "/legal/", "/changelog", "/concepts/engines", "sync.api.terrascale.tech"]) {
  assert.ok(llms.includes(r), `llms.txt lists ${r}`);
}

// L-5: all five launch-post stubs exist.
for (const slug of ["why-sqlite", "reads-are-free", "benchmarks", "replication", "database-per-customer"]) {
  assert.ok(existsSync(join(root, `docs/blog/${slug}.md`)), `blog/${slug}.md exists`);
}

// The hand-written pricing page (docs/pricing/index.md) must agree with the
// generated L-1 draft source of truth on every published number.
const pricingIndex = read("docs/pricing/index.md");
for (const n of ["$1 per 1M", "$0.20 per GB-month", "100k writes", "1 GB", "crypto-shred", "billed once"]) {
  assert.ok(pricingIndex.includes(n), `pricing/index.md agrees on "${n}"`);
}

console.log(`npm test: ${results.length} snippet(s) + static assertions passed.`);
