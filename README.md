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

## Public API Surface

The public client follows the backed `TerraScale.Platform` REST surface:

- Project databases: list/create/get/delete under
  `/api/v1/projects/{projectId}/databases`, with environment list/get/create/delete
  under `/api/v1/projects/{projectId}/databases/{databaseId}/environments`.
- Project queues and topics: create/list/get/delete, publish/peek/purge, and
  at-least-once receive/complete/abandon/dead-letter/renew-lock under
  `/api/v1/projects/{projectId}/queues`. Received messages carry an opaque,
  time-limited regional receipt; pass `{ receipt, region }` back to the
  acknowledgement methods so the SDK preserves regional affinity.
- Repositories: list/create/get/delete under `/api/v1/repositories`.
- Documents: list/create/get/put/patch/delete/batch under
  `/api/v1/repositories/{repositoryId}/documents`; document write bodies are
  wrapped as `{ data }`, and document responses use `id`, `data`,
  `lastModified`, and `changeToken`.
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
  `/api/v1/projects/{projectId}/databases/{databaseId}/backups/{backupId}`,
  `/api/v1/projects/{projectId}/databases/{databaseId}/restore-points`, and
  `/api/v1/projects/{projectId}/databases/{databaseId}/restores`; responses
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
- Usage: project totals, workload, latency, and history plus organization totals.
- Notifications: `/api/v1/users/me/notifications` returns a bare
  `Notification[]`; `/api/v1/users/me/notifications/{notificationId}:mark-read`
  returns a `Notification`; and
  `/api/v1/users/me/notifications:mark-all-read` returns `{ markedRead }`.
- Security settings: backed get and upsert routes at
  `/api/v1/projects/{projectId}/security/settings`.

Unbacked public SDK methods are intentionally omitted until matching API
endpoints exist. This currently excludes repository update, database
update/region switching/environment switching, saved queries, query history,
project settings, security webhooks, SSO/SCIM settings, and public project switching.

## Development

```bash
npm install
npm run typecheck
npm run build
npm test
```

The SDK route manifest is generated from the route-capture tests:

```bash
npm run generate:route-manifest
npm run check:openapi-drift -- ../Terrascale.Documentation/public/openapi/server.openapi.json
```

The drift checker accepts any OpenAPI JSON file path or live spec URL, including
a DevHost-served `/openapi/v1.json`. Refresh the Documentation snapshot from
the current Internals OpenAPI artifact before treating a snapshot failure as an
SDK defect.

Integration tests are expected to run against a real TerraScale DevHost stack
and its emitted API/Auth URLs:

```bash
TERRASCALE_API_BASE_URL=http://127.0.0.1:... \
TERRASCALE_AUTH_API_BASE_URL=http://127.0.0.1:... \
npm run test:integration
```

When those URLs are not set, integration tests skip instead of substituting
mocked HTTP.

## Releases

Main pushes publish `@terrascale/sdk` to the public npm registry through
Forgejo CI after the release gates pass. See `docs/release.md` for the required
checks and release/versioning flow.
