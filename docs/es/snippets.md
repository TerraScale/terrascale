---
---
<!-- i18n:fallback:es -->
> **Traducción pendiente.** Esta página aún no tiene revisión humana en español y muestra el contenido original en inglés.

# Snippets

Runnable snippets run against a real cloud sandbox branch pre-filled with the
reader's key (D-56). `npm test` is the remote, fail-closed launch gate. It
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
Tenant, branch, and backup fixtures are retained below but skipped until their
producer contracts are published. The typed export API is available in the
SDK, but its producer qualification remains pending, so its fixture is still
skipped. Downloads return plaintext artifact bytes with the declared length
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

Tenant = database (D-51). The producer route is not published yet, so this
fixture is intentionally skipped in both modes.

<<< @/snippets/tenants.ts

## Cloud branch

The project-scoped branch producer route is not published yet, so this fixture
is intentionally skipped in both modes.

<<< @/snippets/branches.ts

## Backups

The project-scoped backup producer routes are not published yet, so this
fixture is intentionally skipped in both modes.

<<< @/snippets/backups.ts

## Logical export

The typed export operation is available in the pinned SDK, but producer
qualification is still pending. Its fixture remains in the source tree and is
intentionally skipped by both runner modes. The API returns plaintext artifact
bytes plus the declared length and BLAKE3 checksum metadata from response
headers.

<<< @/snippets/exports.ts
