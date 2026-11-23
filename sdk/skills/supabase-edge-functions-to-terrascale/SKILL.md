---
name: supabase-edge-functions-to-terrascale
description: Migrate Supabase Edge Functions to TerraScale Functions, including Deno handler rewrites, callers, cron schedules and webhook gateways. Use when moving off Supabase Functions or replacing supabase.functions.invoke.
---

# Migrate Supabase Edge Functions to TerraScale

Use this skill for handler code, deployment, invocation, schedules and incoming
webhooks. First migrate the required tables and identity model. Keep the source
deployment runnable until the rollback window closes.

## Prerequisites and supported boundary

- [ ] Have source repository access, the linked Supabase CLI project, and
  permission to inspect deployed functions and configuration.
- [ ] Prepare Bash, curl, jq and a TerraScale project/database. Use a server-held
  project credential with read/write SQL access for deployment/logs; invocation
  requires read access. The function's SQL uses its own authority, per the
  [Functions guide](https://docs.terrascale.tech/guides/functions).
- [ ] Read the [worked port](references/worked-port.md), which replaces a small
  database handler and its caller without relying on Deno imports.
- [ ] Obtain `TERRASCALE_ACCESS_TOKEN` through the
  [documented exchange](https://docs.terrascale.tech/guides/authentication).
  Access tokens last at most 60 seconds; refresh before each command group.
  Keep keys/tokens in server environment variables and turn off shell tracing.
- [ ] Treat the public [Functions guide](https://docs.terrascale.tech/guides/functions)
  as the runtime contract: one JavaScript file defining `handler(event)`, SQL
  bindings and console logs. It documents no Deno runtime, import/package loader,
  outbound HTTP API, environment binding, secret store, raw request/headers or
  response-header API. Do not assume those Deno facilities exist. Keep handlers
  that need them in an external service until redesigned and verified.

## 1. Inventory deployed functions and all entry points

- [ ] Run in the linked Supabase repository:

```bash
supabase functions list
rg --files supabase/functions
rg -n 'Deno\.|import |npm:|jsr:|https:|serve\(|fetch\(|functions\.invoke|functions/v1|verify_jwt' supabase
rg -n 'functions\.invoke|functions/v1|cron\.schedule|net\.http_post|webhook' .
```

The list command and function source directory are documented in the
[Supabase CLI reference](https://supabase.com/docs/reference/cli/supabase-functions-list).
Compare deployed names with `supabase/functions/*/index.ts`; include shared files,
`deno.json`, import maps, `supabase/config.toml`, package versions and deployment
automation. Recover missing source from the approved release or the CLI's
[functions download](https://supabase.com/docs/reference/cli/supabase-functions-download)
command before porting; never assume local source matches production.

- [ ] For each function record input/output JSON, methods, query parameters,
  response headers/CORS, JWT validation, anonymous access, service-role use,
  table operations, secrets **names only**, external calls, binary/streamed
  output, background work, expected duration/memory, schedules and retries.
  Supabase's runtime model is documented in
  [Edge Functions](https://supabase.com/docs/guides/functions),
  [secrets](https://supabase.com/docs/guides/functions/secrets) and
  [configuration](https://supabase.com/docs/guides/functions/function-configuration).
- [ ] Inventory source cron jobs and webhook triggers with read-only SQL:

```sql
SELECT jobid, jobname, schedule, active, database, username
FROM cron.job ORDER BY jobid;

SELECT t.tgname, n.nspname, c.relname, p.proname
FROM pg_trigger t
JOIN pg_class c ON c.oid = t.tgrelid
JOIN pg_namespace n ON n.oid = c.relnamespace
JOIN pg_proc p ON p.oid = t.tgfoid
WHERE NOT t.tgisinternal ORDER BY n.nspname, c.relname, t.tgname;
```

If `cron` is not installed, record no pg_cron jobs and inspect external schedulers.
Review job commands and trigger definitions locally without printing embedded
credentials. Supabase uses pg_cron/pg_net for
[scheduled functions](https://supabase.com/docs/guides/functions/schedule-functions)
and database triggers for
[webhooks](https://supabase.com/docs/guides/database/webhooks).

## 2. Classify and approve the port plan

All target contracts and bounds in this table come from the
[Functions guide](https://docs.terrascale.tech/guides/functions).

| Source feature | Target or explicit workaround |
| --- | --- |
| `Deno.serve(async request => ...)` | Define `async function handler(event)`; HTTP input is `{trigger:'http', body, query}`. Return `{status, body}` or JSON for HTTP 200. |
| TypeScript and Deno/npm/URL imports | Rewrite to plain single-file JavaScript; inline reviewed pure helpers. No package-loading contract is documented. Host dependency-heavy work in an external service. |
| Supabase client SQL operations | Use `sql.execute`, `sql.query`, `sql.batch` and `sql.transaction` on the function's own database. Port statements against the documented SQL dialect and verify equivalent results. |
| `Deno.env.get`, Supabase secrets, third-party API calls | No Functions secret/environment or networking contract is documented. Keep secrets in an external backend; perform those calls there and invoke a narrow SQL handler with validated nonsensitive inputs. Never embed secrets in deployed source or event payloads. |
| Request headers, raw body, webhook signature verification, CORS or streaming | No equivalent request/response APIs are documented. Put a gateway in front to validate signatures, preserve raw input for validation, authorize callers, handle headers/CORS and invoke with JSON. |
| Supabase user JWT/RLS authorization | No forwarding of the caller's JWT or per-user SQL authority is documented. Validate identity and ownership at the application gateway before invocation. Never trust a caller-supplied user ID. |
| Scheduled invocation | Native `cron` deployment field, UTC; handler receives `{trigger:'cron', scheduledAt}`. Port only jobs fitting the supported handler model. |
| Database/webhook triggers | No automatic Functions database-trigger delivery is documented. Use a transactional outbox written by application mutations and an external delivery worker; migrate every writer, including admin and import paths. |
| Long tasks/background workers | Split into bounded jobs with durable application state and an external worker. No background continuation, durable queue or delivery/retry guarantee is documented. |

- [ ] Mark each function `port`, `gateway + port`, or `retain externally`, with
  owner, prerequisites and verification evidence. Unresolved auth, secrets or
  webhook delivery requirements block that function's cutover.
- [ ] Enforce the published target limits: 256 KiB source, 32 functions per
  database, 10 seconds including SQL, 32 MiB memory, 1 MiB request and response,
  256 SQL binding calls per invocation, and 50 log lines of 1 KiB each. SQL also
  has its [own limits](https://docs.terrascale.tech/limits).
- [ ] Use the [documented SQL](https://docs.terrascale.tech/concepts/sql) and
  parameter binding. Do not pass source PostgreSQL routines, extensions or SQL
  unchanged without checking them against that contract.

## 3. Rewrite handlers and deploy

- [ ] Port the worked example first, then each approved handler. Replace
  `request.json()` with `event.body`, query parsing with `event.query`, and Deno
  responses with the documented result shape. Handle HTTP/cron separately.
- [ ] Validate input before SQL, use positional `?` or named `:name` parameters,
  and use `sql.transaction(async tx => ...)` for atomic multi-step changes.
  `sql.query` returns objects; `sql.execute` returns array rows plus metadata;
  separate calls otherwise commit independently. See
  [SQL bindings](https://docs.terrascale.tech/guides/functions).
- [ ] Make a business-level idempotency key part of the data model for operations
  that may be delivered more than once. Check persisted state after an uncertain
  invocation; never replay a side effect solely because the response was lost.
- [ ] Set nonsecret `PROJECT_ID`, `DATABASE_NAME` and `FUNCTION_NAME`. With a fresh
  access token, deploy the plain JS file and capture only the safe status code:

```bash
set -o pipefail
jq -n --rawfile source handler.js '{source: $source}' |
  curl -sS --fail --header @/dev/fd/3 \
    -X PUT -H 'Content-Type: application/json' --data-binary @- \
    -o /dev/null -w '%{http_code}\n' \
    "https://api.terrascale.tech/v1/projects/$PROJECT_ID/databases/$DATABASE_NAME/functions/$FUNCTION_NAME" \
    3< <(printf 'Authorization: Bearer %s\n' "$TERRASCALE_ACCESS_TOKEN")
```

This is the [documented deploy route](https://docs.terrascale.tech/guides/functions).
PUT replaces the function: first archive existing target source/cron with
`GET` on the same URL into a protected file. Use `GET .../functions` to inventory
target names. On a failed deployment, check status, scope, source size and plain
JS compatibility; do not switch callers until a successful invocation verifies it.

## 4. Replace callers, schedules and webhooks

- [ ] Replace `supabase.functions.invoke(name, {body})` and direct `/functions/v1/`
  URLs with calls to the application's authorized backend. The backend invokes
  `POST .../functions/{name}/invoke` using a current read-authorized access token;
  see [deploy and call](https://docs.terrascale.tech/guides/functions).
  The [worked port](references/worked-port.md) includes the concrete request.
- [ ] Keep the caller's success/error contract, including non-2xx handling.
  Supabase invocation behavior is documented in
  [functions.invoke](https://supabase.com/docs/reference/javascript/functions-invoke).
  Do not ship a project key or project access token to a browser to replace a
  Supabase publishable-key flow; follow
  [server authentication](https://docs.terrascale.tech/guides/authentication).
- [ ] For a compatible schedule, include `cron` in the deployment payload:

```bash
jq -n --rawfile source handler.js --arg cron '*/5 * * * *' \
  '{source: $source, cron: $cron}' > deploy.json
```

Submit `deploy.json` to the same PUT route with `--data-binary @deploy.json` in
place of the source pipeline. This is a five-minute UTC schedule, per
[Functions cron](https://docs.terrascale.tech/guides/functions). First verify the
handler's cron branch by observing a real staged cron invocation in logs; an
HTTP request cannot fabricate the runtime's cron trigger. Do not enable the target
schedule while the source job is active. A source job with outbound HTTP/secrets
stays on an external scheduler invoking the approved gateway.
- [ ] For incoming third-party webhooks, the external gateway checks signatures
  against the original raw bytes, enforces replay protection and persists delivery
  IDs before calling the target. Native invocation requires read access and its
  documented event lacks raw headers/body, so a webhook provider cannot simply
  be pointed at the target invoke URL. Preserve the provider's retry contract.
- [ ] For database webhooks, write domain changes and an outbox record in one
  transaction, then let an external worker deliver and acknowledge each event.
  Reconcile pending source events before changing writers. Native cron can
  perform database-only periodic work, but is not a documented HTTP delivery
  worker. If a writer cannot emit the outbox record, retain its existing webhook
  path until that gap is resolved.

## 5. Verify behavior and invocation metrics

- [ ] Compare old/new behavior on a staging copy: valid input, invalid JSON shape,
  denied identity, cross-user access, missing row, duplicate business ID, SQL
  refusal, explicit transaction rollback and an uncertain-response reconciliation.
  Compare actual persisted rows and returned values, not just HTTP status.
- [ ] Verify limits with representative payloads and resource use. Check that
  importing a removed dependency or reading a missing secret is impossible in
  the shipped handler; secret-dependent work must remain in its external backend.
- [ ] Capture invocation logs and stats, as documented in
  [invocation metrics](https://docs.terrascale.tech/guides/functions):

```bash
curl -sS --fail --header @/dev/fd/3 \
  "https://api.terrascale.tech/v1/projects/$PROJECT_ID/databases/$DATABASE_NAME/functions/$FUNCTION_NAME/logs" \
  -o function-logs.json \
  3< <(printf 'Authorization: Bearer %s\n' "$TERRASCALE_ACCESS_TOKEN")
curl -sS --fail --header @/dev/fd/3 \
  "https://api.terrascale.tech/v1/projects/$PROJECT_ID/databases/$DATABASE_NAME/functions/$FUNCTION_NAME/stats?window=24h" \
  -o function-stats.json \
  3< <(printf 'Authorization: Bearer %s\n' "$TERRASCALE_ACCESS_TOKEN")
```

Logs retain the last 100 invocations with duration, SQL calls/time, result row
counts, response bytes, HTTP status, console lines and errors. Stats support
`1h`, `24h`, `7d`, with totals/errors and latency estimates; they cover the
answering regional writer and do not combine regions. A handler returning an
HTTP error still counts as a completed execution; inspect HTTP status as well
as execution errors. Do not log secrets or sensitive payloads in the handler.

## 6. Cut over and roll back

- [ ] Archive source code/configuration, source job IDs and target predecessor
  source/cron; record each release's nonsecret route and business-ID mapping.
- [ ] Pause the source schedule or webhook sender at a known boundary, drain and
  reconcile in-flight operations, then switch one caller/sender at a time.
  Enable the target cron only after the source is inactive. Verify at least one
  real scheduled run and one delivery through each webhook gateway.
  For pg_cron, save the approved job definition privately, then remove the
  selected job with `SELECT cron.unschedule(42);`, replacing 42 with its inventoried
  ID. Restore that saved job through `cron.schedule` on rollback, following
  [Supabase cron management](https://supabase.com/docs/guides/cron/manage-jobs).
- [ ] Monitor application errors, authorization denials, database outcomes,
  outbox backlog and the invocation metrics above. Keep a rollback write ledger.
- [ ] On regression, pause the new schedule/sender, reconcile writes already
  applied, redeploy the saved predecessor source/config if replacing a target
  function, and restore source callers/jobs. Do not run both schedules during
  rollback or automatically undo valid business transactions.
- [ ] After acceptance and the rollback window, retire old source functions and
  credentials deliberately. The target `DELETE .../functions/{name}` also deletes
  schedule, logs and aggregates, per
  [Functions lifecycle](https://docs.terrascale.tech/guides/functions); archive
  required evidence first. Do not use deletion as a temporary pause.
