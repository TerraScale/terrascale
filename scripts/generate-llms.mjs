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
const canonicalRegions = JSON.parse(readFileSync(join(root, "data", "canonical", "regions.json"), "utf8")).regions;
const regions = canonicalRegions.map((region) => region.id);
const catalog = JSON.parse(readFileSync(join(root, "data", "canonical", "pricing.json"), "utf8"));
const unit = catalog.metering.operationUnit;
const planSummary = catalog.plans
  .map((plan) => `${plan.name} $${plan.priceMonthlyUsd}/month${plan.available ? "" : " (unavailable during Public Alpha)"}`)
  .join(", ");
const routes = [
  ["/", "TerraScale docs home: what TerraScale is, quickstart with an unqualified five-minute target."],
  ["/getting-started", "Legacy Next.js starter: unpublished SDK pin; sign-in and live-update journey unqualified."],
  ["/getting-started/", "Legacy Next.js live-query guide: @terrascale/sdk 0.3.0 is not published; stub success does not qualify the browser journey."],
  ["/getting-started/csharp", "C# candidate quickstart: project-scoped TerraBase Documents CRUD through an exact SDK package; isolated real-binary qualification is separate from production JWTs."],
  ["/guides/atlas", "Atlas candidate: /v1/discovery, project/database /v1/resolve, JWKS and 600-second ES256 routing pins; automatic SDK integration deferred."],
  ["/guides/sdks", "SDK compatibility: project-scoped C#, JavaScript and Go candidates; public release, real-runtime and customer-journey gates are distinct."],
  ["/concepts", "Product concepts and legacy SQL/live-query design; candidate Documents query-v1 is a separate contract."],
  ["/concepts/replication", "Replication guarantees: causal sessions, conflict limits, RegionalDurable/GlobalDurable."],
  ["/concepts/sync", "Sync model: shared views, immutable delta log, CDN delivery."],
  ["/concepts/sql", "SQL: live subset, allow-list, typed builder, auto-indexing."],
  ["/concepts/pricing-philosophy", "Pricing philosophy: operation units and storage across regions are the two meters."],
  ["/guides/", "Guides overview."],
  ["/pricing", "Pricing (generated from the canonical https://terrascale.tech/pricing.json): plans, operation units, storage across regions."],
  ["/pricing/", "Pricing & limits: plans, quotas, guards, shared organization allowances."],
  ["/limits", "Limits and unbilled abuse guards, generated from the same source as pricing."],
  ["/snippets", "Legacy CRUD, SQL and live-subscription fixtures use an unpublished 0.3.0 pin; database-scoped branch, backup, and export routes are published in the retained legacy contract only, not qualified by these fixtures. Candidate APIs require separate evidence."],
  ["/blog/reads-are-free", "Superseded launch post draft: Reads are free — how."],
  ["/blog/benchmarks", "Benchmark publication policy: no measured results published."],
  ["/blog/replication", "Draft: choosing durability across Regions."],
  ["/blog/database-per-customer", "Launch post draft: a database per customer."],
  ["/changelog", "Changelog."],
];

const txt = `# TerraScale docs — llms.txt (P-8)

> TerraScale is a multi-region document database-as-a-service. The current
> C# TerraBase candidate exposes project-scoped JSON Documents APIs, typed
> filter queries and transactions. Retained SQL, reactive, branching and
> tenant guides describe separate legacy contracts and product targets;
> they are not evidence of candidate runtime or launch qualification.
> Usage is metered in operation units (each started 4 KiB read
> or written; writes count once per selected region, reads only in the
> serving region) and storage across regions. Plans use hard allowances with
> no paid overages during Public Alpha.
> SQL-only querying (D-78) belongs to the retained legacy design, not candidate
> Documents query-v1. A write counts once per selected region regardless of
> views/subscribers (D-22).

Base URL: ${base}

## Docs map

${routes.map(([r, desc]) => `- ${desc}: ${base}${r}`).join("\n")}

## Pricing summary (canonical source: https://terrascale.tech/pricing.json; full tables in /pricing and /pricing/)

- Plans: ${planSummary}.
- ${unit.definition} ${unit.rules.join(" ")}
- ${catalog.metering.storage.name}: ${catalog.metering.storage.definition}
- ${catalog.metering.allowances.definition}

## Legacy snippet harness

- Fixtures retain @terrascale/sdk 0.3.0, which is not published on npm.
  Stub success does not establish candidate producer compatibility.
- Snippet sources: docs/snippets/*.ts. Runnable snippets export \`run(sandbox)\`.
- Local mode (the default): \`npm test\` runs the deterministic stub harness.
- Remote qualification (explicit, fail-closed): set TERRASCALE_SANDBOX_URL,
  TERRASCALE_SANDBOX_API_KEY, and TERRASCALE_SANDBOX_DATABASE_ID, then run
  \`npm run test:remote\`. The local harness does not qualify the remote
  contract.

## API entry points

Base domain terrascale.tech. Regions at launch: ${canonicalRegions.map((region) => `${region.id.toUpperCase()} (${region.name})`).join(", ")}.

- Atlas candidate discovery: GET https://atlas.terrascale.tech/v1/discovery
  returns ranked regions, closest and the public service catalog.
- Atlas candidate resolve: GET https://atlas.terrascale.tech/v1/resolve?project=…&database=…
  (database optional; fresh=1 requests refreshed resolution) returns project/database
  placement, api/auth origins and a 600-second ES256 session pin.
- Atlas candidate keys: GET https://atlas.terrascale.tech/v1/keys returns JWKS.
  Verify pin signature, issuer, scope, api and expiry before routing; send
  X-TerraScale-Region-Pin: <region>, not the signed JWS. A routing pin grants
  no database permissions. Re-resolution on expiry, 421 or redirect requires
  caller integration; do not forward credentials through automatic redirects
  or replay uncertain mutations. Deployment and integrated SDK qualification
  remain pending.
- Current SDK candidates require an explicit HTTPS endpoint or caller-supplied
  resolver. Automatic Atlas integration is deferred; no catalog or proxy
  fallback is promised. A configured endpoint proves only its own lane.
- Deprecated compatibility catalogs: /api/v1/regions and /api/v1/auth/discovery;
  new clients use /v1/discovery and regions[].auth.
- API router: https://api.terrascale.tech. Configure the service endpoint explicitly;
  the SDK candidates do not select it implicitly.
- Regional API origins: ${regions.map((region) => `https://${region}.api.terrascale.tech`).join(",\n  ")}.
  Use configured or verified resolved endpoints, not a hardcoded region default.
- Candidate documents: GET/PUT/PATCH/DELETE
  /v1/projects/{project}/databases/{database}/collections/{collection}/documents/{id};
  POST to the collection's /documents route creates a document.
- Candidate queries: POST
  /v1/projects/{project}/databases/{database}/collections/{collection}/query
  uses filter, sort, project, limit, cursor and count; cursors are opaque.
- Candidate transactions: POST /v1/projects/{project}/databases/{database}/transactions.
  Versions are opaque ETags. C# requests go once; mutation deduplication is not
  promised and Idempotency-Key is rejected.

## Retained legacy API references (not candidate qualification)

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

C# candidate: TerraScale.DotNet.TerraBase.TerraBaseDocumentsClient with
Database(projectId, databaseName), document CRUD, QueryAsync and TransactAsync.
JavaScript candidate: @terrascale/sdk createTerraBaseV1Client with explicit
baseUrl or endpointResolver. Go candidate: terrascale.tech/sdk/v1 with an
explicit Config.BaseURL or Config.Resolver. Retired NativeClient and UUID/shard
admission are legacy and do not describe this project-scoped contract.

Current NuGet/npm candidates are not publicly released. As checked October 7,
2026, the latest public @terrascale/sdk version on npm is 0.0.2; it does not qualify the
candidate or the legacy 0.3.0 pin. An exact locally packaged C# candidate passed
real C# binary CRUD/query, stale-version, credential-denial and cleanup checks
through the SDK Testing helper. Its source-bound receipt is checked in at
examples/terrabase-dotnet/qualification.json.
Compilation, isolated compatibility, public package availability and deployed
customer qualification are separate gates. Follow /getting-started/csharp for
the current qualification evidence; cross-SDK parity is not established.

The candidate C# Documents client accepts historical tb_ keys in isolated
compatibility tests. The product design accepts ts-auth JWTs; production JWT
validation, deployed sign-in and authorization remain unqualified. A historical
key receipt does not pass that gate. Auth owns users, organizations and projects;
all application data belongs in TerraBase. Do not duplicate identity/management
models. Dashboard sign-in stays on native pages. Other SDK/runtime, Atlas and
connected customer journeys need independent qualification.
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
