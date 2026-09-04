# Snippets

Runnable snippets run against a real sandbox branch pre-filled with the
reader's key (D-56). `npm test` is the remote, fail-closed qualification mode.
It requires all three environment variables below. `npm run test:stub` is the
explicit local mode and does not qualify the remote contract.

```bash
TERRASCALE_SANDBOX_URL=https://<branch>.<region>.api.terrascale.tech \
TERRASCALE_API_KEY=ts_... \
TERRASCALE_DATABASE_ID=db_... \
npm test
npm run test:stub
```

The runnable examples target the published `@terrascale/sdk` 0.3.0 surface.
Tenant, branch, backup, and export fixtures are retained below but skipped
until their producer contracts are published. The stub mode is deterministic
and never counts as remote qualification.

## Document CRUD

<<< ./snippets/crud.ts

## SQL execute

<<< ./snippets/sql.ts

## Live subscription

<<< ./snippets/subscribe.ts

## Database per tenant

Tenant = database (D-51). The producer route is not published yet, so this
fixture is intentionally skipped in both modes.

<<< ./snippets/tenants.ts

## Cloud branch

The project-scoped branch producer route is not published yet, so this fixture
is intentionally skipped in both modes.

<<< ./snippets/branches.ts

## Backups

The project-scoped backup producer routes are not published yet, so this
fixture is intentionally skipped in both modes.

<<< ./snippets/backups.ts

## Logical export

The typed export operation is not in the pinned SDK yet. Its fixture remains in
the source tree but is intentionally skipped by both runner modes.

<<< ./snippets/exports.ts
