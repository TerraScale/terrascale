# TerraScale TypeScript SDK

This repository contains the `@terrascale/sdk` package.

## Idempotent mutations

Public clients opt in with `idempotencyKeyFactory` on the client or `idempotencyKey` on an individual request. Set the request value to `false` to opt out of global generation. Keys must be nonblank and no longer than 128 characters. Replayable API results expire after 24 hours; replays include `Idempotency-Replayed: true`, while reuse with different input returns `409 idempotency_key_reused`.

The package is the customer-facing TypeScript client for TerraScale public REST
APIs. Role-protected owner/admin APIs use a separate explicit public entrypoint.

## Entry Points

- `@terrascale/sdk` exports the SDK client, REST models, ULID helpers, runtime
  config, HTTP primitives, and result types.
- `@terrascale/sdk/admin` exports role-protected platform administration.
  Dashboard business flows use the same root clients as customer applications.

The SDK has no Dashboard-specific entrypoint, DTO, method, or route. Dashboard
Next composes these typed public product clients inside its own full-stack route
handlers as an ordinary external user and never receives special headers,
authorization, tenant access, direct database access, or private API access.

## Regional and session consistency

The default client targets `https://api.terrascale.tech`. A preferred region on
the global endpoint is a failover-capable hint. Strict affinity uses the
selected regional hostname and does not silently fall back:

```ts
const session = createTerraScaleClient({
  accessToken,
  projectId,
  preferredRegion: "fra",
  regionAffinity: "strict",
  sessionConsistency: "automatic",
});

const current = await session.documents.get({ repositoryId }, documentId);
if (current.ok) {
  await session.documents.put(
    { repositoryId },
    documentId,
    { ...current.value.data, status: "approved" },
    { expectedChangeToken: current.value.changeToken ?? undefined },
  );
}
```

Automatic session consistency keeps the latest opaque write token inside that
client instance and sends it on later single-document reads. Set
`allowStale: true` on an individual read to opt out of the server wait. A
conditional mismatch returns a normal `TerraScaleResult` conflict and changes
no document. `result.routing` reports the public edge and serving regions when
the API supplies them; it marks a cross-region response as degraded without
exposing operator details.

Transient retries are disabled by default. Set `maxRetryAttempts` and, when
needed, `retryDelayMs` to enable bounded retries for reads. Mutations retry only
when they carry an idempotency key, and the same key is reused for each attempt.

## Sync

`TerraScaleSyncSession` is the framework-neutral live-query client. Browser
sessions use a fresh single-use ticket for every connection; server runtimes
use a bearer-token provider. Both use the native `WebSocket` implementation.

```ts
import { TerraScaleSyncSession } from "@terrascale/sdk";

const sync = new TerraScaleSyncSession({
  Url: "wss://api.example.com/api/v1/sync",
  DatabaseId: databaseId,
  EnvironmentId: environmentId,
  Tenant: tenantId,
  AuthorizationScope: authorizationHash,
  TicketProvider: getSingleUseSyncTicket,
});

const tasks = sync.subscribeQuery({
  Query: "from Tasks where State = $state",
  Arguments: { state: "open" },
  SchemaVersion: "1",
});

const unsubscribe = tasks.subscribe(render);
await sync.connect();
```

`tasks.getSnapshot()` and `tasks.getServerSnapshot()` implement the
`useSyncExternalStore` contract without a React dependency. Identical queries
share one wire registration until their last handle is released. Call
`executeMutation` with a stable idempotency key and optional optimistic patches;
call `disconnect` or `dispose` with the owning application lifetime.

The v1 WebSocket session above stays available behind its compat flag for the
deprecation window; it speaks `terrascale.sync.v1` and is the legacy surface.

## Sync v2 (`terrascale.sync.v2`)

`TerraScaleSyncV2Session` is the native TypeScript client for the
`terrascale.sync.v2` contract (PD-2026-08-26-12: native-language SDK clients,
no wasm/wasi artifacts). It speaks to the TerraBase API host's HTTP/SSE
endpoints: session setup with durable checkpoint resume, checkpoint-bounded
delta pulls, snapshot adoption on resync-required, and the trigger-notification
channel that drives immediate pulls — push the hint, pull the delta.

```ts
import { TerraScaleSyncV2Session } from "@terrascale/sdk";

const sync = new TerraScaleSyncV2Session({
  Url: "https://api.terrascale.test",
  Collection: "orders",
  Principal,   // backend-supplied context (X-TB-Principal)
  Tenant,      // backend-supplied context (X-TB-Tenant)
  ResumeCheckpoint: persisted, // base64, from getCheckpointBase64()
  CheckpointStore: { save: persist },
});

await sync.connect();
const applied = await sync.executeMutation(
  { Document: "d-01", Put: { status: "approved" } },
  "stable-idempotency-key",
);
await sync.disconnect();
```

Mutations are server-ack-only (PD-2026-08-26-12 D2, the recorded v1/v2
behavioral difference): `executeMutation` returns the durable receipt or the
typed rejection and there is no optimistic-patch surface — the mutation
reaches the session's state only through the journal-to-delta path, and a
retry with the same idempotency key is the same mutation. Stores are
memory-only this wave; `loro-delta/1`/`loro-snapshot/1` payload bytes stay
opaque to the protocol and reach documents through an optional materializer.

Conformance to the engine's committed golden vectors runs in this package's
test suite: the SE-04 corpus and the SE-05 integrity manifest are vendored
under `test/fixtures/terrascale.sync.v2/` (digest-verified), and
`runSyncV2Conformance` executes the per-language harness contract against
the shipped codec, emitting the `terrascale.sync.v2/conformance-report@1`
report. The vendored corpus is a deliberate pin of one engine revision —
see `test/fixtures/terrascale.sync.v2/README.md` for the pinned source,
its digests, and how to re-vendor.

## Public API Surface

The public client follows the backed `TerraScale.Platform` REST surface:

- Project databases: list/create/get/delete under
  `/api/v1/projects/{projectId}/databases`, with environment list/get/create/delete
  under `/api/v1/projects/{projectId}/databases/{databaseId}/environments`.
- Physical database branching: `client.databaseBranches.getCapability` reads
  `/api/v1/projects/{projectId}/database-branches/capability` and
  `requestClone` sends the typed clone request to `/api/v1/projects/{projectId}/database-branches`.
  The currently admitted state is fail-closed (`"unavailable"`): clone has no success response and returns
  the typed `503` failure until Platform reports otherwise.
- Project queues and topics: create/list/get/delete, publish/peek/purge, and
  at-least-once receive/complete/abandon/dead-letter/renew-lock under
  `/api/v1/projects/{projectId}/queues`. Received messages carry an opaque,
  time-limited regional receipt; pass `{ receipt, region }` back to the
  acknowledgement methods so the SDK preserves regional affinity.
- Repositories: list/create/get/delete under `/api/v1/repositories`.
- Documents: list/create/get/put/patch/delete/batch under
  `/api/v1/repositories/{repositoryId}/documents`; document write bodies are
  wrapped as `{ data }`, and document responses use `id`, `data`,
  `lastModified`, `changeToken`, and write-only `sessionConsistencyToken`.
  Put, patch, and delete accept `expectedChangeToken`; single-document reads accept
  `allowStale`. A client configured with `sessionConsistency: "automatic"`
  forwards the last successful write token on later reads.
- TQL and schema: `/api/v1/repositories/{repositoryId}/tql`,
  `/api/v1/repositories/{repositoryId}/schema`, and
  `/api/v1/repositories/{repositoryId}/schema/validate`.
- Project auth API keys: `/api/v1/projects/{projectId}/auth/api-keys`,
  including create/list/rotate/revoke wrappers.
- Project auth providers: typed listing under
  `/api/v1/projects/{projectId}/auth/providers`.
- Project auth M2M applications:
  `/api/v1/projects/{projectId}/auth/m2m-applications`, including
  create/list/rotate/revoke and OAuth token creation.
- Backups and restores:
  `/api/v1/projects/{projectId}/databases/{databaseId}/backups/policy`,
  `/api/v1/projects/{projectId}/databases/{databaseId}/backups`,
  `/api/v1/projects/{projectId}/databases/{databaseId}/backups:plan`, and
  `/api/v1/projects/{projectId}/databases/{databaseId}/backups:execute`,
  `/api/v1/projects/{projectId}/databases/{databaseId}/backups/{backupId}`,
  `/api/v1/projects/{projectId}/databases/{databaseId}/restore-points`, and
  `/api/v1/projects/{projectId}/databases/{databaseId}/restores`; on-demand backups require
  an explicit `planBackup` then `executeBackup` confirmation flow, with no unconfirmed trigger.
  Responses
  use logical ULID fields such as `backupId`, `restoreId`, and
  `restoredDatabaseId`.
- Account identity: typed external login and email-change initiation/verification
  through the public Auth API.
- Onboarding: state, account information, MFA choice, checkout creation with
  explicit success/cancel URLs, payment validation, and initial
  organization/project creation under `/api/v1/onboarding`.
- Organizations and projects: organization create/list/get/update/deletion,
  membership and invitation lifecycle, plus project create/list/get/update/delete
  and region placement.
- Billing and pricing: organization checkout sessions accept `planId`,
  `billingInterval`, `provider`, optional `quantity`, or deprecated `priceId`.
  Deprecated `successUrl`, `cancelUrl`, and `customerEmail` properties remain
  source-compatible but are ignored and never sent; organization checkout
  redirects and customer identity are deployment/authentication configured.
- Usage: project totals, workload, latency, and history plus organization totals.
- Notifications: `/api/v1/users/me/notifications` returns a bare
  `Notification[]`; `/api/v1/users/me/notifications/{notificationId}:mark-read`
  returns a `Notification`; and
  `/api/v1/users/me/notifications:mark-all-read` returns `{ markedRead }`.
- Security settings: backed get and upsert routes at
  `/api/v1/projects/{projectId}/security/settings`.
- Saved queries: `client.savedQueries.list/create/update/delete` provides typed
  v1 CRUD under `/api/v1/projects/{projectId}/saved-queries`, with revision-aware v2
  `client.savedQueries.v2.list/get/update`, and mutation-safe snapshot pagination with
  `client.savedQueries.v3.list` under `/api/v3/projects/{projectId}/saved-queries`.
  V2 revisions and v3 cursors are opaque; reload a v3 collection after a cursor conflict.

### Onboarding target contract

The onboarding entry above describes the currently released typed surface. The
target self-registration contract will begin with identity and email verification
already counted as one completed step, then cover the first Organization, Project,
and logical Database plus card verification for signup abuse control. It will let
the User explicitly continue with no paid plan and no automatic renewal, retaining
the Developer allowance, or select Starter or Pro with monthly automatic renewal
and an eligible first 30 days at no charge. Invited-user onboarding remains
separate and requires neither signup card verification nor a plan choice. The
missing typed operations are tracked in
[issue 6](https://git.terrascale.tech/TerraScale/TerraScale.TypeScript/issues/6),
with the product implementation tracked in
[TerraScale.Internals issue 30](https://git.terrascale.tech/TerraScale/TerraScale.Internals/issues/30).
Do not invent routes or present this target flow as released until both contracts
ship.

Unbacked public SDK methods are intentionally omitted until matching versioned
OpenAPI request and response schemas are published. This currently excludes account-security
extensions, repository update, database
update/region switching/environment switching, query history, project settings,
security webhooks, SSO/SCIM settings, and public project switching.

The OpenAPI drift check carries a reviewed typed-contract debt baseline. It prevents
new or changed untyped SDK contracts, while the listed debt still blocks production
qualification until Platform publishes the missing schemas and removes the baseline.

## Development

```bash
npm install
npm run typecheck
npm run build
npm test
npm run test:browser
```

Node.js 22 and 24 are the supported server runtimes. The package supports the
current stable major versions of Chrome, Firefox, Safari, and Edge. CI runs
the public contract suite on both Node versions and the browser build against
the current Playwright Chromium, Firefox, and WebKit engines. Branded-browser
and live API evidence still belongs to the protected release qualification;
local engine results do not stand in for evidence that was not run.

The supported public API `/api/v1` contract applies to both package entry
points. Release qualification installs the exact packed tarball into clean
Node and browser consumers. It does not rebuild the package for those checks.

The SDK route manifest is generated from the route-capture tests:

```bash
npm run generate:route-manifest
npm run check:openapi-drift -- ../Terrascale.Documentation/public/openapi/server.openapi.json
npm run generate:openapi-contract-descriptor -- ../Terrascale.Documentation/public/openapi/server.openapi.json
npm run check:openapi-contract-descriptor -- ../Terrascale.Documentation/public/openapi/server.openapi.json
```

`sdk-openapi-contract-descriptor.json` is the reviewable, deterministic pin for
every captured SDK route: OpenAPI operation ID, parameters, request schema, and
each successful response schema. `void` and `never` SDK types are explicit in
the descriptor; any OpenAPI identity, parameter, nullability, field-shape, or
schema-constraint drift fails the descriptor check.
The route manifest also records the actual captured JSON body shape plus sorted
query and header name/value pairs (including repeated query values), so changes
to pagination or destructive-confirmation semantics cannot pass unnoticed.
Only an explicit deterministic test-value allowlist is retained; all other pair
values are redacted. Schema entries retain every nested OpenAPI
`$ref`, its legal sibling constraints, and canonical resolved shape.
Headers are captured after the Fetch `Headers` boundary as one effective value
per lowercase name; repeated SDK header values are intentionally coalesced by
that boundary and are not represented as distinct wire fields.

The drift checker accepts any OpenAPI JSON file path or live spec URL, including
a DevHost-served `/openapi/v1.json`. Refresh the Documentation snapshot from
the current Internals OpenAPI artifact before treating a snapshot failure as an
SDK defect.

Integration tests are expected to run against a real TerraScale DevHost stack
and its emitted Platform/Auth URLs:

```bash
TERRASCALE_API_BASE_URL=http://127.0.0.1:... \
TERRASCALE_AUTH_API_BASE_URL=http://127.0.0.1:... \
npm run test:integration
```

When those URLs are not set, integration tests skip instead of substituting
mocked HTTP.

The saved-query CRUD smoke test additionally requires
`TERRASCALE_E2E_ACCESS_TOKEN` and `TERRASCALE_E2E_SAVED_QUERIES_PROJECT_ID`.
Set the latter only to a dedicated disposable DevHost project for which the
token has saved-query permissions; it never falls back to
`TERRASCALE_E2E_PROJECT_ID`.

## Releases

Main pushes publish `@terrascale/sdk` to the public npm registry through
Forgejo CI after the release gates pass. See `docs/release.md` for the required
checks and release/versioning flow.
