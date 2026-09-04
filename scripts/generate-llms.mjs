// Generates docs/public/llms.txt from the VitePress nav (P-8: llms.txt).
// Usage: node scripts/generate-llms.mjs [--check]
import { readFileSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");
const outPath = join(root, "docs", "public", "llms.txt");
const check = process.argv.includes("--check");

const routes = [
  ["/", "TerraScale docs home: what TerraScale is, 5-minute quickstart."],
  ["/getting-started", "Quickstart: create-terrascale-app to first live query in < 5 minutes; runnable snippets."],
  ["/concepts", "Core concepts: documents, SQL-only queries (D-78), live queries vs collection sync (D-38), branches, tenants, placement."],
  ["/pricing", "Pricing (L-1 draft, generated from D-16/D-17/D-21/D-25/D-64): writes + storage billed, everything else free."],
  ["/limits", "Limits and unbilled abuse guards (D-25/D-64), generated from the same source as pricing."],
  ["/snippets", "Runnable snippets: every snippet runs against a real sandbox branch pre-filled with the reader's key (D-56)."],
  ["/blog/why-sqlite", "Launch post stub (L-5): Why TerraScale runs on SQLite."],
  ["/blog/reads-are-free", "Launch post stub (L-5): Reads are free — how."],
  ["/blog/benchmarks", "Launch post stub (L-5): benchmarks."],
  ["/blog/replication", "Launch post stub (L-5): replication design."],
  ["/blog/database-per-customer", "Launch post stub (L-5): a database per customer."],
];

const txt = `# TerraScale docs — llms.txt (P-8)

> TerraScale is a multi-region document database: pay for writes and storage;
> reads, egress, fan-out, and subscribers are free. SQL is the only query
> language (D-78). Live queries and collection sync over CDN-backed HTTP
> long-polling by default. This file lists the docs routes for LLM consumers.

${routes.map(([r, desc]) => `- https://docs.terrascale.tech${r}: ${desc}`).join("\n")}

## Pricing summary (L-1 draft; full tables in /pricing)

- Billed: writes (1 op <= 4 KiB; +1 per extra replicated region) and storage
  (logical bytes x regions at $/GB-month). Overage/PAYG: $1 per 1M writes,
  $0.20 per GB-month per region.
- Free/unbilled: reads, queries, egress, CDN delivery, fan-out, subscribers,
  WebSocket connections, webhooks, catch-up/replay, TTL expiry.
- A write is billed once regardless of views/subscribers (D-22).

## Snippet harness

- Snippet sources: docs/snippets/*.ts. Each exports \`run(sandbox)\`.
- Stub mode (CI, no credentials): \`npm test\`.
- Remote mode (real sandbox branch): TERRASCALE_SANDBOX_URL + TERRASCALE_API_KEY,
  \`npm test -- --sandbox=remote\`.
`;

if (check) {
  const cur = readFileSync(outPath, "utf8");
  if (cur !== txt) {
    console.error("FAIL: docs/public/llms.txt is stale; run node scripts/generate-llms.mjs");
    process.exit(1);
  }
  console.log("llms.txt is up to date.");
} else {
  writeFileSync(outPath, txt);
  console.log("wrote docs/public/llms.txt");
}
