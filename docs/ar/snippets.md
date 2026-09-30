---
---
<!-- i18n:fallback:ar -->
> **الترجمة قيد المراجعة.** لا تتوفر بعد مراجعة بشرية لهذه الصفحة بالعربية، لذلك يظهر المحتوى الأصلي بالإنجليزية.

# Snippets

Runnable snippets can be qualified against a real cloud sandbox branch
pre-filled with the reader's key. `npm test` is the deterministic local stub
check used by CI. The explicit remote, fail-closed launch gate is
`npm run test:remote`; it requires all three sandbox environment variables and
exits non-zero when the sandbox is unavailable. An absent sandbox is a failed
gate, not a passing fixture run.

```bash
TERRASCALE_SANDBOX_URL=https://<region>.api.terrascale.tech \
TERRASCALE_SANDBOX_API_KEY=ts_... \
TERRASCALE_SANDBOX_DATABASE_ID=db_... \
npm run test:remote
# Local-only smoke test; this is not launch qualification.
npm test
```

Do not put a production credential in a snippet fixture. Use a disposable
cloud dev branch and rotate its key after qualification. The runner sends API
credentials only to the configured API origin; reactive snapshot and log
requests use the short-lived `X-TerraScale-Capability` header returned by view
attach. The capability is valid for at most 1,200 seconds; after expiry,
attach the view again. Do not send an API key on reactive cache requests.

The runnable examples target the published `@terrascale/sdk` 0.3.0 surface.
Tenant and project-scoped branch/backup fixtures await producer contracts.
Database-scoped branch, backup, and export routes are published; the export
fixture remains source-only pending qualification. Downloads return plaintext
artifact bytes with the declared length
and BLAKE3 checksum from the `X-TerraScale-Artifact-Length` and
`X-TerraScale-Artifact-Checksum` headers. The stub mode is deterministic and
never counts as remote qualification.

## Document CRUD

<<< @/snippets/crud.ts

## SQL execute

<<< @/snippets/sql.ts

## Live subscription

<<< @/snippets/subscribe.ts

## Database per tenant

Tenant = database. Tenant provisioning is not published yet, so this fixture
is intentionally skipped in both modes. Ordinary database CRUD remains
available after a database is provisioned through its published control-plane
workflow.

<<< @/snippets/tenants.ts

## Cloud branch

This fixture exercises the project-scoped branch producer, which is not in the
pinned public contract yet. It is intentionally skipped in both modes.
Database-scoped branch listing and creation are published; the connected
cloud workflow still requires qualification.

<<< @/snippets/branches.ts

## Backups

This fixture exercises project-scoped backup policy and confirmation routes,
which are not in the pinned public contract yet. It is intentionally skipped
in both modes. Database-scoped backup listing and restoration are published;
the connected recovery workflow still requires qualification.

<<< @/snippets/backups.ts

## Logical export

The database-scoped typed export operation is available, but its real-service
qualification remains pending. This fixture remains in the source tree and is
intentionally skipped by both runner modes. The intended API returns plaintext
artifact bytes plus declared length and BLAKE3 checksum metadata from response
headers.

<<< @/snippets/exports.ts
