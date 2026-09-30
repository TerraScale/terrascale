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
const regions = ["gru", "sfo", "fra"];
const routes = [
  ["/", "TerraScale docs home: what TerraScale is, quickstart with an unqualified five-minute target."],
  ["/getting-started", "Quickstart: create-terrascale-app to development write and live query; sign-in journey unqualified."],
  ["/getting-started/", "Quickstart: cloud dev branch, document write, and live query; real browser journey unqualified."],
  ["/concepts", "Core concepts: documents, SQL-only queries (D-78), live queries vs collection sync (D-38), branches, tenants, placement."],
  ["/concepts/replication", "Replication guarantees: causal sessions, conflict limits, RegionalDurable/GlobalDurable."],
  ["/concepts/sync", "Sync model: shared views, immutable delta log, CDN delivery."],
  ["/concepts/sql", "SQL: live subset, allow-list, typed builder, auto-indexing."],
  ["/concepts/pricing-philosophy", "Pricing philosophy: writes + storage are the only billed dimensions."],
  ["/guides/", "Guides overview."],
  ["/pricing", "Pricing (draft, generated from checked-in pricing data): writes + storage billed, everything else free."],
  ["/pricing/", "Pricing & limits: plans, quotas, guards, shared organization allowances."],
  ["/limits", "Limits and unbilled abuse guards, generated from the same source as pricing."],
  ["/snippets", "Runnable snippets: CRUD, SQL, and live subscriptions run against a real sandbox branch pre-filled with the reader's key; database-scoped branch, backup, and export routes are published, while tenant and project-scoped branch/backup fixtures await contracts and the export fixture awaits qualification."],
  ["/blog/reads-are-free", "Launch post draft: Reads are free — how."],
  ["/blog/benchmarks", "Benchmark publication policy: no measured results published."],
  ["/blog/replication", "Draft: choosing durability across Regions."],
  ["/blog/database-per-customer", "Launch post draft: a database per customer."],
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

## Pricing summary (draft; full tables in /pricing and /pricing/)

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
- Local mode (the default): \`npm test\` runs the deterministic stub harness.
- Remote qualification (explicit, fail-closed): set TERRASCALE_SANDBOX_URL,
  TERRASCALE_SANDBOX_API_KEY, and TERRASCALE_SANDBOX_DATABASE_ID, then run
  \`npm run test:remote\`. The local harness does not qualify the remote
  contract.

## API entry points

Base domain terrascale.tech. Regions at launch: ${regions.map((region) => region.toUpperCase()).join(", ")}.

- Discovery: GET https://atlas.terrascale.tech/api/v1/discovery?database=…
  (optional branch, rev, stale) — served by ts-atlas, a metadata-only Worker:
  credential-free KV-backed metadata that returns ranked regional endpoints
  with capabilities, residency, a routing revision, and a refresh policy. The
  TS SDK calls it once per database/branch/scope, then connects directly
  (HTTPS + WebSocket).
- Region catalog: GET https://atlas.terrascale.tech/api/v1/regions — also
  ts-atlas; the catalog-selection fallback when discovery fails and the
  selector for database-less member routes.
- Nearest-region API router: https://api.terrascale.tech — forwards requests
  to the nearest configured regional TerraBase HTTPS API. SDKs use discovery
  to select direct regional origins.
- Direct regional REST + WebSocket: ${regions.map((region) => `https://${region}.terrascale.tech`).join(",\n  ")} — direct
  routing with catalog fallback; SDK data requests go straight to the region.
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

TypeScript (@terrascale/sdk), C#, Go, and Rust are supported SDK projects.
Use each published package's pinned public contract for its available methods;
cross-SDK parity and connected customer journeys still require qualification.
Tenant provisioning is awaiting its producer contract. Framework adapters
(React, TanStack, Better Auth) are TypeScript-only launch scope.
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
