# InstantDB 2026 public feature survey (CP-01 evidence record)

Status: competitor evidence record for ticket TerraScale/TerraBaseV2#29
(CP-01). Web survey of InstantDB's public documentation and product pages,
accessed 2026-08-26. Every entry names the exact URL it was verified from;
entries marked (index) were verified only as listings on a fetched index
page, not by fetching the target page. No claim in this file is a
TerraBaseV2 claim; it records what the competitor ships.

Survey context that the matrix must carry, not hide: the InstantDB docs
carry a banner stating "Instant is sunsetting. Services will continue
until August 31st, 2027." (visible on the docs, auth, and streams pages
below, accessed 2026-08-26). PD-2026-08-26-06 D1 fixes the parity baseline
as the verified 2026 public feature set, so the sunset does not remove any
row; it is recorded here once as survey context.

## EV-I1 — Docs index and positioning

- URL: https://www.instantdb.com/docs (accessed 2026-08-26)
- Positioning: "Instant is the easy-to-use backend for your frontend";
  tagline "The Modern Firebase".
- Verified capability listings (sidebar, all (index) unless fetched
  elsewhere below): Init, Modeling data, Writing data, Reading data,
  Infinite Queries, backend usage, Auth (magic codes, guest auth, Google
  OAuth, Sign In with Apple, GitHub OAuth, LinkedIn OAuth, Clerk, Firebase
  Auth), Permissions, Rate Limits, users management, "Presence, Cursors,
  and Activity", CLI, Devtool, Platform API, Explorer Component, custom
  emails, App teams, Storage, Streams, Webhooks, Backups, Stripe Payments,
  Admin HTTP API, experimental Next.js SSR, self-hosting (VPS, AWS,
  migrate-from-cloud), "Using LLMs".
- SDK languages listed: React, React Native, Vanilla JS, SolidJS, Svelte,
  Vue, TanStack Start, Python.

## EV-I2 — Reactive queries (InstaQL, useQuery)

- URL: https://www.instantdb.com/docs/instaql — page "Reading data"
  (accessed 2026-08-26)
- `db.useQuery(query)` returns `{ isLoading, error, data }` as an ongoing
  subscription; opening another tab shows todos updating "in realtime".
- Declarative JSON query shape ("InstaQL — it's like GraphQL without the
  configuration"); nested association fetches including inverse
  associations; `where`, `order`, `limit`, `offset`, `first`/`after`,
  `last`/`before`, `fields` under `$`. Documented limits: `limit`,
  `offset`, cursors, and `order` are top-level-namespace only; "Order is
  not supported on nested attributes".
- Filtering vocabulary: multiple `where` keys (implicit AND), `and`, `or`,
  `$in`, `$gt`/`$lt`/`$gte`/`$lte` (which "require indexed, type-checked
  attributes"), `$ne`, `$isNull`, `$like` (case-sensitive substring),
  `$ilike` (case-insensitive). This is substring pattern matching — no
  full-text search index is documented on this page.
- One-off reads: `queryOnce` "will throw an error if the user is offline".

## EV-I3 — Transactions and writes (InstaML, db.transact)

- URL: https://www.instantdb.com/docs/instaml — page "Writing data"
  (accessed 2026-08-26)
- Mutations via `db.transact` on `db.tx[namespace][id].action(...)`:
  `create`, `update` (upsert by default; `{ upsert: false }` for strict),
  `merge` (deep-merge objects; arrays/numbers/booleans overwrite),
  `delete` (also deletes associations), `link`, `unlink`, `lookup` by
  unique attribute.
- Atomicity: "Transacts are atomic" — "all the transactions are committed
  atomically"; if any fails, none commit.
- Batching guidance: split large writes "to avoid hitting transaction
  limits and timeouts" (example: 3000 goals as 30 batches of 100).
- Typesafety is permissive by default (missing attributes auto-create);
  schemas add enforcement.
- Conflict handling: `update` on JSON objects can clobber concurrent
  changes; `merge` mitigates by deep-merging objects.

## EV-I4 — Sync engine: optimistic updates, realtime, offline, conflicts

- URL: https://www.instantdb.com/product/sync — page "Sync Engine"
  (accessed 2026-08-26)
- Names four hard-to-build components, verbatim: "1. Optimistic Update
  Layer 2. Real-time Sync 3. Offline Persistence 4. Conflict Resolution".
- Optimistic updates: "When a user makes a change we first apply it to a
  local store so users see the update immediately"; pending mutations are
  tracked and rolled back if the server rejects them.
- Offline: "Apps keep working even when you're offline"; "You can keep
  using the app and your changes will sync when you come back online."
- Supporting pages: https://www.instantdb.com/about — "Every app will
  eventually need real-time sync, optimistic updates, and offline mode."
  and https://www.instantdb.com/essays/architecture — "For offline mode
  you store caches in IndexedDB. And for optimistic updates, you figure
  out how to apply and undo mutations in the client." (both accessed
  2026-08-26).

## EV-I5 — Optional typed schemas

- URL: https://www.instantdb.com/docs/modeling-data — page "Modeling data"
  (accessed 2026-08-26)
- Schema-as-code in `instant.schema.ts`: namespaces ("equivalent to
  'tables'... or 'collections' in NoSQL"), attributes typed
  `i.string()/i.number()/i.boolean()/i.date()/i.json()/i.any()`,
  `.optional()`, `.unique()` ("guarantees no duplicates and speeds up
  filtered queries"), `.indexed()` ("enables comparison operators ($gt,
  $lt, $gte, $lte) and order clauses").
- Links with forward/reverse directions, `has: "one"|"many"`, four
  relationship kinds, `onDelete: "cascade"`, `required: true`.
- Schema is optional: "we'll automatically create missing entities for you"
  on transact. Migration: `npx instant-cli push schema` shows a planned
  diff; renames/deletes must go through the dashboard's Explorer.
- Production lockdown of client-side schema drift via
  `attrs.allow.$default: 'false'` in permission rules.

## EV-I6 — Permissions (CEL rules)

- URL: https://www.instantdb.com/docs/permissions — page "Permissions"
  (accessed 2026-08-26)
- Rules are boolean expressions in CEL, declared per namespace under
  `allow`, governing `view`, `create`, `update`, `delete`; unset rules
  "evaluate to true" by default; `$default` flips defaults; field-level
  rules via `fields`; `attrs` namespace gates on-the-fly schema changes.
- Vocabulary: `data`, `newData`, `ref()` relation traversal, `bind`
  aliases, `ruleParams`, `request.time/origin/ip/modifiedFields`.
- Enforcement is server-side for reads and writes: every object matching
  a query is evaluated against the `view` rule, and transactions fail on
  insufficient permission. No admin-role system is built in (patterns use
  role/email checks). No caching or performance claims are made.

## EV-I7 — Authentication

- URL: https://www.instantdb.com/docs/auth — page "Auth" (accessed
  2026-08-26)
- Built-in methods, verbatim names: "Magic Codes", "Guest Auth", "Google
  OAuth", "Sign In with Apple", "GitHub OAuth", "LinkedIn OAuth", "Clerk",
  "Firebase Auth"; "Custom Auth" via the Admin SDK.
- Client API: `db.useUser` (gated by `<db.SignedIn>/<db.SignedOut>`),
  `db.useAuth`, `db.getAuth`, `db.auth.signOut()`; custom `$users`
  attributes at signup.

## EV-I8 — Presence, cursors, and ephemeral topics (rooms)

- URL: https://www.instantdb.com/docs/presence-and-topics — page
  "Presence, Cursors, and Activity" (accessed 2026-08-26)
- "Rooms" — "a temporary context for realtime events"; `db.room(type,id)`.
- "Presence" — "an object that each peer shares with every other peer",
  replicated instantly in the room, "automatically cleaned up when a user
  leaves the room"; `usePresence` with `keys` slicing and write-only mode.
- "Topics" — fire-and-forget broadcast: "publishing fires a callback for
  every other user in the room listening for that topic";
  `usePublishTopic`/`useTopicEffect`; typed topic payloads via schema
  rooms.
- Decision guide, verbatim: "Use `transact` when you need to persist data
  to the db"; presence persists in the room but not the db; topics
  broadcast without persistence.
- Drop-in components: `<Cursors>` (multiplayer cursors, `spaceId`
  scoping) and `useTypingIndicator`.

## EV-I9 — Storage (files)

- URL: https://www.instantdb.com/docs/storage — page "Storage" (accessed
  2026-08-26)
- "Instant Storage makes it simple to upload and serve files for your
  app" — "images, videos, documents, and any other file type".
- `db.storage.uploadFile(path, file, opts?)` keyed by path ("If the path
  already exists... it will be overwritten!"); query results carry a
  hosted `url` (Instant's S3); files are queried through the `$files`
  namespace (`id`, `path`, `url`, `content-type`, `content-disposition`);
  delete and link-to-entities via `db.transact`; permission rules cover
  `view`/`create`/`update`/`delete` on `$files`; the Admin SDK bypasses
  permission checks. No size limits documented on the page.

## EV-I10 — Streams (AI-era, durable, resumable)

- URL: https://www.instantdb.com/docs/streams — page "Streams" (accessed
  2026-08-26; sunset banner present)
- Streams "provide a simple way to build durable, real-time data flows"
  implementing the standard Web Streams API; written data is "buffered in
  memory on Instant's servers", periodically flushed to Storage, and
  "fully flushed to storage when finished".
- Marketed as "excellent for LLM-native applications";
  `@instantdb/resumable-stream` is "a drop-in replacement" for Vercel's
  library with "no Redis instance" and streams that "never expire";
  `consumeSseStream` resumes LLM SSE output server-side;
  `reconnectToStream` resumes "directly from the browser"; the ai-chat
  template wires `resume: true`.
- Semantics: readers "can pick up from any point in the stream, and
  resume if the connection is lost" (`createReadStream` with
  `byteOffset`); one writer per `clientId`; server-assigned stream UUID;
  metadata (`done`, `size`, `abortReason`) queryable via the `$streams`
  namespace and linkable to entities; permission rules default to
  `"false"` on `$streams`.

## EV-I11 — Dashboard, devtool, and explorer

- URL: https://www.instantdb.com/docs/devtool — page "Devtool" (accessed
  2026-08-26): localhost-attached devtool with "Explorer" ("Use the
  Explorer to change up your data and schema") and "Sandbox" ("try out
  different queries and transactions").
- https://www.instantdb.com/docs/modeling-data (EV-I5): renames/deletes
  "must be done through the dashboard's Explorer" — the hosted dashboard
  carries the Explorer. The hosted dashboard's own page was not fetched
  (index listing only).

## EV-I12 — Rate limits

- URL: https://www.instantdb.com/docs/rate-limits — page "Rate Limits"
  (accessed 2026-08-26)
- Token-bucket `rateLimit` objects declared under `$rateLimits` inside
  permission rules; `rateLimit.<bucket>.limit(key)` consumes a token,
  over-limit calls fail with a `type: "rate-limited"` error carrying
  `retry-after` and `remaining-tokens`; keys like `auth.id`/`request.ip`;
  greedy vs interval refill; multiple simultaneous limits; limits apply to
  writes and to `view` queries, "evaluated per entity" (a 50-row query
  consumes 50 tokens).

## EV-I13 — Webhooks

- URL: https://www.instantdb.com/docs/webhooks — page "Webhooks" (accessed
  2026-08-26)
- "Subscribe to changes to your entities via POST requests to your
  server", configured by namespace and action (create/update/delete);
  signed deliveries (`Instant-Signature`, Ed25519) carry a short-lived
  `payloadUrl` plus JWT exchanged for full `before`/`after` records and
  an `idempotencyKey` for dedup across redeliveries; retries with
  backoff, 15-second timeout, status stages (pending/processing/success/
  error/failed), auto-disable on repeated failure; configured via the
  dashboard, `npx instant-cli webhook`, or the Admin SDK manager
  (100-active-webhook limit, https-only); ~60 days of event history with
  resend.

## EV-I14 — Backups

- URL: https://www.instantdb.com/docs/backups — page "Backups" (accessed
  2026-08-26)
- "Instant automatically generates a nightly backup that contains all of
  the data from your app", viewable for the last 7 days in the dashboard
  or via `npx instant-cli backup list`; downloads are a zip of schema,
  rules, magic-code email template, entities (NDJSON per table), and
  files; restore path exists for self-hosted instances (superuser
  sign-in, `/intern/restore`).

## EV-I15 — Admin HTTP API (backend access)

- URL: https://www.instantdb.com/docs/http-api — page "Admin HTTP API"
  (accessed 2026-08-26)
- Backend access from any language via `APP_ID`/`ADMIN_TOKEN` headers:
  `POST /admin/query` (InstaQL with `$$ruleParams`), `POST
  /admin/transact` (steps: update/link/unlink/delete), server-side
  reactive subscriptions via SSE `POST /admin/subscribe-query`, user
  impersonation (`As-Email`/`As-Token`/`As-Guest`), user management,
  room-presence queries, sign-out, refresh-token minting and magic-code
  endpoints for custom auth, and storage upload/delete/list. Admin
  requests bypass permissions; impersonation headers run "on behalf of a
  user and respect permissions".

## EV-I16 — Agent surface (LLMs, MCP)

- URL: https://www.instantdb.com/docs/using-llms — page "Using Instant
  with LLMs" (accessed 2026-08-26)
- "Instant Skill" (`npx skills add instantdb/skills`), "Instant Rules"
  (`/llm-rules/AGENTS.md`, installed as CLAUDE.md/GEMINI.md/AGENTS.md),
  markdown docs and llms.txt ("llms-full.txt").
- "Instant MCP Server" — hosted at `https://mcp.instantdb.com/mcp`
  (streamable HTTP) or `/sse`, OAuth-authenticated, plus a local stdio
  server (`@instantdb/mcp`) with a personal access token.
- MCP tools, verbatim: `learn`, `get-schema`, `get-perms`, `push-schema`,
  `push-perms`.

## Not verified as shipped (recorded for matrix honesty)

- Vector search: no vector index, embedding, or similarity-search
  capability appears in the docs index or any fetched page (EV-I1..I16).
- Full-text search indexes: no analyzer/bm25-style search documented;
  the verified text predicates are substring `$like`/`$ilike` (EV-I2).
- Scheduled jobs/cron: no scheduler or cron capability appears in the
  docs index (EV-I1).
- Offline persistence for Convex is covered in the Convex survey; this
  file records InstantDB's own capability only.
