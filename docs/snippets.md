# Runnable snippets

Every snippet below runs against a real sandbox branch pre-filled with the
reader's key (D-56). In CI they run against the stub sandbox
(`scripts/stub-sandbox.mjs`, same interface, no credentials):

```bash
npm test
TERRASCALE_SANDBOX_URL=https://<branch>.<region>.api.terrascale.tech \
TERRASCALE_API_KEY=ts_... npm test -- --sandbox=remote
```

## Document CRUD

<<< ./snippets/crud.ts

## SQL query

<<< ./snippets/sql.ts

## Live subscription

<<< ./snippets/subscribe.ts

## Database per tenant

<<< ./snippets/tenants.ts
