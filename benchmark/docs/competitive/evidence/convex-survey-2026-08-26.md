# Convex 2026 public feature survey (CP-01 evidence record)

Status: competitor evidence record for ticket TerraScale/TerraBaseV2#29
(CP-01). Web survey of Convex's public documentation, accessed 2026-08-26.
Every entry names the exact URL it was verified from; entries marked
(index) were verified only as listings on a fetched index page, not by
fetching the target page. No claim in this file is a TerraBaseV2 claim;
it records what the competitor ships.

## EV-C1 — Docs index and positioning

- URL: https://docs.convex.dev/ (accessed 2026-08-26)
- Positioning: "the reactive database with TypeScript queries".
- Verified capability listings (sidebar, all (index) unless fetched
  elsewhere below): Queries, Mutations, Actions, HTTP Actions, Internal
  Functions, Runtimes; Schemas, Indexes, Paginated Queries, Document IDs,
  Backups, Data Import & Export, Streaming Data in and out; Cron Jobs,
  Scheduled Functions; Full Text Search, Vector Search; File Storage
  (upload/serve/delete/metadata); Convex Auth plus Auth0/Clerk/WorkOS/
  custom OIDC/custom JWT; AI Gateway, AI Agents, Workflows, RAG, Rate
  Limiting, Tool Approval, Threads, Streaming, Usage Tracking; "Convex
  MCP Server"; Optimistic Updates; clients for React, React Native, Vue,
  Nuxt, Svelte, Node.js, Bun, Python, Rust, Android Kotlin, iOS/macOS
  Swift; Dashboard, Environment Variables, Custom Domains, Regions,
  Usage Limits, Abuse Protection; Audit Logging, Exception Reporting,
  Log Streams; Components; convex-test; Local Deployments; Self Hosting;
  Deployment API, Management API, Platform APIs; Custom Roles, SSO.

## EV-C2 — Reactive queries, caching, consistency

- URL: https://docs.convex.dev/realtime — page "Realtime" (accessed
  2026-08-26)
- "Turns out Convex is automatically realtime!" — no special
  configuration; Convex tracks query-function dependencies and "triggers
  the subscription in the client libraries".
- "Automatic caching": query results cached automatically; the cache
  refreshes whenever underlying data changes; "You don't get charged for
  database bandwidth for cached reads"; "requires no work or
  bookkeeping".
- "Consistent data across your app": "Every client subscription gets
  updated simultaneously to the same snapshot of the database."
- Function contract (https://docs.convex.dev/functions/query-functions,
  page "Queries", accessed 2026-08-26): handlers "must be deterministic";
  non-deterministic built-ins are handled by Convex; read limits surface
  via `maximumBytesRead`/`maximumRowsRead` and
  `ctx.meta.getTransactionMetrics()`; `QueryCtx` carries `db`, `storage`,
  `auth`; actions exist because "queries can't be non-deterministic...

  To call third party APIs, use actions."

## EV-C3 — Serverless functions (queries, mutations, actions, HTTP)

- URL: https://docs.convex.dev/functions/query-functions (accessed
  2026-08-26) plus the docs index (EV-C1) for the function-family pages
  not individually fetched (mutation-functions, actions, http-actions,
  internal functions — (index) verified).
- Queries fetch data with caching and reactivity (EV-C2); "mutations"
  insert/update/remove; "actions" "Call third-party services and external
  APIs from Convex"; "HTTP Actions" "Build HTTP APIs directly in
  Convex"; "Internal Functions" are callable only by other Convex
  functions. Runtimes: Convex runtime vs Node.js runtime.

## EV-C4 — Optimistic updates

- URL: https://docs.convex.dev/client/react/optimistic-updates — page
  "Optimistic Updates" (accessed 2026-08-26)
- "temporary, local changes to your query results which are used to make
  your app more responsive"; registered per mutation invocation via
  `.withOptimisticUpdate(...)`; the updater receives an
  `OptimisticLocalStore` ("a view of the Convex client's internal state")
  with `getQuery`/`setQuery`.
- Lifecycle: updates "run when a mutation is initiated, rerun if the
  local query results change, and rolled back when a mutation completes";
  rollback is automatic and "the UI will always eventually render the
  correct values"; client-generated values are replaced by server values.
- Rule: "Mutating objects inside of optimistic updates will corrupt the
  client's internal state."

## EV-C5 — Typed schemas (optional, validated on write)

- URL: https://docs.convex.dev/database/schemas — page "Schemas" (accessed
  2026-08-26)
- `schema.ts` with `defineSchema`/`defineTable` and validators from
  `convex/values` (`v.string()`, `v.number()`, `v.id()`, `v.union()`,
  `v.record()`, ...); system appends `_id` and `_creationTime`.
- "It is possible to use Convex without defining a schema"; recommended
  workflow is prototype without, add later. First schema push validates
  all existing documents — "a failing document causes the push to fail";
  afterwards "every insert, update, db.insert, db.replace, and db.patch
  call is validated against the schema"; `schemaValidation: false` and
  `strictTableNameTypes: false` options.
- "end-to-end type safety throughout your app": `npx convex dev`
  regenerates `dataModel.d.ts`; `Doc<TableName>` and `Infer` types.
- No schema versioning or in-place schema migration contract is
  documented on this page (changes are push-validated).

## EV-C6 — Indexes (explicit declarations)

- URL: https://docs.convex.dev/database/reading-data/indexes — page
  "Indexes" (accessed 2026-08-26)
- Indexes are declared in the schema with `.index("by_channel",
  ["channel"])`; queries use `.withIndex(name, q => ...)` with chained
  `.eq/.gt/.gte/.lt/.lte` that "must step through fields in index order";
  results are sorted by the index columns with `_creationTime` tiebreak.
- A default `by_creation_time` index is "created automatically (and is
  what is used in database queries that don't specify an index)";
  non-index `.filter()` "doesn't affect which documents are scanned".
  Limits: 16 fields per index, 32 indexes per table, `staged: true`
  backfills asynchronously.
- Comparative fact for the matrix: creating a purpose-fit index requires
  an explicit schema declaration; there is no documented optimizer that
  creates indexes from query shapes.

## EV-C7 — Full-text search

- URL: https://docs.convex.dev/search/text-search — page "Full Text Search"
  (accessed 2026-08-26)
- `searchIndex` on a table schema: exactly one `searchField` (type
  string), optional `filterFields` (up to 16), optional `staged` flag;
  built with Convex's "multi-segment search algorithm" on top of Tantivy.
- Queries via `.withSearchIndex(name, ...)` with exactly one `.search`
  expression plus 0..n `.eq` filter expressions; results always in
  "relevance order"; ranking combines a "BM25 score" with match proximity
  and count; ties favor newest documents. Tokenization: Tantivy
  `SimpleTokenizer`, lowercased, 32-character terms; final term gets
  prefix matching ("Typeahead Search"); fuzzy matching deprecated (typo
  results removed after 2025-01-15).
- Limits: 16 search terms, 8 filter expressions, 1024 scanned results;
  search indexes count toward the 32-indexes-per-table cap. No Lucene
  AND/OR/wildcard/phrase operators are documented.

## EV-C8 — Vector search

- URL: https://docs.convex.dev/search/vector-search — page "Vector Search"
  (accessed 2026-08-26)
- `vectorIndex` in the schema: `vectorField`, `dimensions` (2..4096),
  optional `filterFields` (up to 16) and `staged`.
- `ctx.vectorSearch` — actions only: `{ vector, limit (1..256, default
  10), filter }`; returns `_id` and `_score` (cosine similarity -1..1)
  with "approximate nearest neighbor search"; documents are loaded
  separately by ID; pitched "to provide useful context to LLMs for AI
  powered applications" (RAG) and recommendations; "consistent and fully
  up-to-date" (no stale-allowed mode documented); up to 4 vector indexes
  per table; "millions of vectors supported".

## EV-C9 — Scheduled functions and cron jobs

- URL: https://docs.convex.dev/scheduling/scheduled-functions — page
  "Scheduled Functions" (accessed 2026-08-26): scheduling survives
  downtime and restarts because jobs are "stored in the database";
  `ctx.scheduler.runAfter(ms, fn, args)` and `runAt(date)`; from
  mutations scheduling is atomic — "if the mutation succeeds, the
  scheduled function is guaranteed to be scheduled"; states
  Pending/InProgress/Success/Failed/Canceled in the `_scheduled_functions`
  system table with 7-day results; cancel semantics; mutations run
  exactly once with automatic retries, actions at most once.
- URL: https://docs.convex.dev/scheduling/cron-jobs — page "Cron Jobs"
  (accessed 2026-08-26): `crons.ts` with `cronJobs()`; `crons.interval()`
  ("seconds-level granularity"), `crons.cron()` (five-field UTC cron
  string), `crons.hourly()/daily()/weekly()/monthly()`; one run per job
  at a time (slow runs skip); dashboard views for jobs, history, logs.

## EV-C10 — File storage

- URL: https://docs.convex.dev/file-storage/overview — page "File Storage"
  (accessed 2026-08-26)
- "All file types are supported": Upload, Store (generated/third-party
  files), Serve (via URL), Delete, plus metadata; storage IDs
  (`Id<"_storage">`) are "safe to store in your tables and pass through
  Convex functions".
- Generated URLs are bearer-style — "anyone with the URL can access the
  file"; revocation is deletion; HTTP action responses are "limited to
  20MB"; for expiring URLs the docs recommend a storage component
  (Cloudflare R2) instead.

## EV-C11 — Workflows (durable execution)

- URL: https://docs.convex.dev/agents/workflows — page "Workflows"
  (accessed 2026-08-26)
- The Workflow component "Builds on the Workpool to provide durable
  execution of long running functions with retries and delays"; workflows
  "can be long-lived, survive server restarts, and have strong guarantees
  around retrying, idempotency, and completing".
- Each step is idempotent with arguments and results stored in workflow
  state, enabling resume by replaying history after a crash;
  "configurable retry settings"; `step.runMutation`/`step.runAction`
  split mutation vs network steps; related components: "Action Retrier",
  "Workpool" ("parallelism limits and retries").

## EV-C12 — Authentication

- URL: https://docs.convex.dev/auth/overview — page "Authentication"
  (accessed 2026-08-26)
- WebSocket/RPC auth via "OpenID Connect (based on OAuth) ID tokens in
  the form of JWTs"; provider integrations: Clerk, WorkOS AuthKit, Auth0,
  "Custom Auth Integration" for any OIDC provider.
- "Convex Auth" (built-in, beta): sign-up/sign-in via "social identity
  providers, one-time email or SMS access codes, or via passwords"
  directly on the deployment; fewer features than third-party options.
- Authorization model: "Convex skips opinionated frameworks like RLS —
  you write per-function permission checks yourself" (e.g.
  `getAuthUserId(ctx)`); service auth via shared secrets.

## EV-C13 — Dashboard

- URL: https://docs.convex.dev/dashboard/overview — page "Dashboard"
  (accessed 2026-08-26): "the central hub for managing your Convex
  projects" — teams, projects, deployments.
- Index-verified sub-pages (EV-C1, (index)): dashboard deployments data
  browser and health views; scheduled-function and cron views are
  documented on the scheduling pages (EV-C9).

## EV-C14 — MCP server and agent tooling

- URL: https://docs.convex.dev/cli/reference/mcp — page "npx convex mcp"
  (accessed 2026-08-26)
- `npx convex mcp start` "exposes your Convex codebase to AI tools in a
  structured way"; tools (verbatim list from `--disable-tools`): `data`,
  `envGet`, `envList`, `envRemove`, `envSet`, `functionSpec`,
  `insights`, `logs`, `run`, `runOneoffQuery`, `status`, `tables`.
- Production safety gating: read-only production tools require
  `--cautiously-allow-production-pii`; mutating production access is
  behind `--dangerously-enable-production-deployments`.

## Not verified as shipped (recorded for matrix honesty)

- Offline persistence: no offline client store, offline write queue, or
  local-first cache appears in the docs index (EV-C1); optimistic
  updates are explicitly temporary local patches rolled back on
  mutation completion (EV-C4). Local deployments (index) are dev
  servers, not offline client capability.
- Presence/ephemeral channels: no presence, rooms, or ephemeral
  topic/channel capability appears in the docs index (EV-C1); the
  verified realtime surface is reactive query subscriptions (EV-C2).
- Durable token streams: "Streaming" is listed under AI Agents (EV-C1,
  (index) — page not fetched) as an agent-component concern; no
  storage-backed resumable-stream primitive is documented on fetched
  pages.
- Self-hosting, AI Gateway, RAG/Threads components, audit logging, log
  streams, environment variables, custom domains, regions: verified as
  docs-index listings only (EV-C1) where not fetched above.
