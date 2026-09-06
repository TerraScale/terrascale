// Generates docs/public/llms.txt (P-8: llms.txt).
// Usage: node scripts/generate-llms.mjs [--check]
// --check exits non-zero when the checked-in file differs from generated output.
import { readFileSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");
const outPath = join(root, "docs", "public", "llms.txt");
const check = process.argv.includes("--check");

const base = "https://docs.terrascale.tech";
const routes = [
  ["/", "TerraScale docs home: what TerraScale is, 5-minute quickstart."],
  ["/getting-started", "Quickstart: create-terrascale-app to development write and live query; sign-in journey unqualified."],
  ["/getting-started/", "Quickstart (guides IA): cloud dev branch, seed, codegen, first live query."],
  ["/concepts", "Core concepts: documents, SQL-only queries (D-78), live queries vs collection sync (D-38), branches, tenants, placement."],
  ["/concepts/replication", "Replication guarantees: causal sessions, conflict limits, RegionalDurable/GlobalDurable."],
  ["/concepts/sync", "Sync model: shared views, immutable delta log, CDN delivery."],
  ["/concepts/sql", "SQL: live subset, allow-list, typed builder, auto-indexing."],
  ["/concepts/pricing-philosophy", "Pricing philosophy: writes + storage are the only billed dimensions."],
  ["/guides/", "Guides overview."],
  ["/pricing", "Pricing (L-1 draft, generated from D-16/D-17/D-21/D-25/D-64): writes + storage billed, everything else free."],
  ["/pricing/", "Pricing & limits: plans, quotas, guards, shared organization allowances."],
  ["/limits", "Limits and unbilled abuse guards (D-25/D-64), generated from the same source as pricing."],
  ["/snippets", "Runnable snippets: CRUD, SQL, and live subscriptions run against a real sandbox branch pre-filled with the reader's key (D-56); tenant, branch, backup, and export fixtures are retained but skipped until their producer contracts are published."],
  ["/blog/reads-are-free", "Launch post draft (L-5): Reads are free — how."],
  ["/blog/benchmarks", "Benchmark publication policy: no measured results published."],
  ["/blog/replication", "Draft: choosing durability across Regions."],
  ["/blog/database-per-customer", "Launch post draft (L-5): a database per customer."],
  ["/legal/", "Legal overview: ToS, Privacy, DPA, security.txt, Support."],
  ["/legal/terms", "Terms of Service"],
  ["/legal/privacy", "Privacy Policy"],
  ["/legal/dpa", "Data Processing Addendum"],
  ["/legal/security", "Security disclosures"],
  ["/legal/support", "Support policy"],
  ["/changelog", "Changelog."],
];

const txt = `# TerraScale docs — llms.txt (P-8)

> TerraScale is a multi-region document database-as-a-service: leaderless
> causal replication across selected Regions, CDN-backed reactive
> live queries, SQL over documents (SQLite dialect), per-document history
> with deterministic conflict explanations, PITR/branching, and a database
> per tenant. Pay only for writes (one write = one create/update/patch/delete
> of <= 4 KiB, ceil(bytes/4 KiB); replicating to another selected region
> multiplies its 4 KiB units by selected Regions before allowances) and storage (logical bytes x regions at
> $/GB-month). Reads, queries, egress, CDN delivery, fan-out, subscribers,
> WebSocket connections, webhook delivery, catch-up/replay, and TTL expiry
> are unbilled within disclosed safety limits; reads, queries, and direct member read egress are
> metered for unbilled abuse guards, while CDN-delivered reactive data has no
> bandwidth guard.
> SQL is the only query language (D-78). A write is billed once regardless of
> views/subscribers (D-22).

Base URL: ${base}

## Docs map

${routes.map(([r, desc]) => `- ${desc}: ${base}${r}`).join("\n")}

## Pricing summary (L-1 draft; full tables in /pricing and /pricing/)

- Billed: writes (4 KiB units multiplied by selected Regions before allowances) and storage
  (logical bytes x regions at $/GB-month). Overage/PAYG: $1 per 1M writes,
  $0.20 per GB-month per region.
- Free/unbilled: reads, queries, egress, CDN delivery, fan-out, subscribers,
  WebSocket connections, webhooks, catch-up/replay, TTL expiry. Reads, queries,
  and direct member read egress are metered for unbilled abuse guards; none is
  billed.
- A write is billed once regardless of views/subscribers (D-22).

## Snippet harness

- Snippet sources: docs/snippets/*.ts. Runnable snippets export \`run(sandbox)\`.
- Remote mode (the default, fail-closed): set TERRASCALE_SANDBOX_URL,
  TERRASCALE_API_KEY, and TERRASCALE_DATABASE_ID, then run \`npm test\`.
- Explicit local stub mode (no credentials): \`npm run test:stub\`. This does
  not qualify the remote contract.

## API entry points

Base domain terrascale.tech. Regions at launch: GRU, SFO, FRA.

- REST first contact: https://api.terrascale.tech — routes to the nearest
  healthy region hosting the database; returns 421 Misdirected Request with
  X-TerraScale-Placement, after which the SDK pins REST (reads and writes).
- Pinned regional REST: https://gru.api.terrascale.tech,
  https://sfo.api.terrascale.tech, https://fra.api.terrascale.tech
- Regions list: GET https://api.terrascale.tech/regions
- Reactive HTTP (live queries; CDN-backed long-polling, capability header):
  https://sync.api.terrascale.tech
- SQL (read-only; JSON or MessagePack):
  POST /api/v1/databases/{id}/sql
- Reactive views (create/attach; returns view id, snapshot chunk URLs, log
  URL, checkpoint, view capability):
  POST /api/v1/reactive/views
- Reactive log (shared cache key; offset = revision):
  GET /api/v1/reactive/views/{view}/{generation}/log?offset={revision}
- Export (lossless bundle or JSONL):
  POST /api/v1/databases/{id}/exports
- Public status and metrics pages are deferred.

## SDKs

TypeScript (@terrascale/sdk), C#, Go, Rust — all generated from a pinned
contract with the same surface: document CRUD, typed SQL builder, raw
parameterized SQL, subscribe/execute, checkpoints and typed resets, region
pinning, tenants, management. Framework adapters (React, TanStack, Better
Auth) are TypeScript.
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
