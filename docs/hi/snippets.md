---
---
<!-- i18n:fallback:hi -->
> **अनुवाद लंबित।** इस पृष्ठ का मानव-समीक्षित हिंदी अनुवाद अभी उपलब्ध नहीं है, इसलिए मूल अंग्रेज़ी सामग्री दिखाई जा रही है।

# Snippets

Runnable snippets run against a real cloud sandbox branch pre-filled with the
reader's key. `npm test` is the remote, fail-closed launch gate. It
requires all three environment variables below and exits non-zero when the
sandbox is unavailable; an absent sandbox is a failed gate, not a passing
fixture run. `npm run test:stub` is an explicit local-development harness and
never qualifies the remote contract or launch.

```bash
TERRASCALE_SANDBOX_URL=https://<branch>.<region>.api.terrascale.tech \
TERRASCALE_API_KEY=ts_... \
TERRASCALE_DATABASE_ID=db_... \
npm test
# Local-only smoke test; this is not launch qualification.
npm run test:stub
```

Do not put a production credential in a snippet fixture. Use a disposable
cloud dev branch and rotate its key after qualification. The runner sends API
credentials only to the configured API origin; reactive snapshot and log
requests use the short-lived view capability header.

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
