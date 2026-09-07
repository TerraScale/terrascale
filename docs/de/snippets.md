---
---
<!-- i18n:fallback:de -->
> **Übersetzung ausstehend.** Diese Seite wurde noch nicht auf Deutsch geprüft und zeigt den englischen Originalinhalt.

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
Database-scoped branch listing/creation, backup listing/restoration, and
logical export routes are in the pinned public contract. The fixtures below
that require project-scoped producer operations remain skipped until those
operations are published and qualified. Tenant provisioning is also still
unavailable because its producer contract is absent. Downloads return
plaintext artifact bytes with the declared length
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
Database-scoped branch listing and creation are published separately.

<<< @/snippets/branches.ts

## Backups

This fixture exercises project-scoped backup policy and confirmation routes,
which are not in the pinned public contract yet. It is intentionally skipped
in both modes. Database-scoped backup listing and restoration are published
separately.

<<< @/snippets/backups.ts

## Logical export

The database-scoped typed export operation is available in the pinned public
contract, but this fixture's producer qualification is still pending. It
remains in the source tree and is intentionally skipped by both runner modes.
The API returns plaintext artifact
bytes plus the declared length and BLAKE3 checksum metadata from response
headers.

<<< @/snippets/exports.ts
