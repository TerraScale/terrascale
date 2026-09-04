---
---
<!-- i18n:fallback:ar -->
> **الترجمة قيد المراجعة.** لا تتوفر بعد مراجعة بشرية لهذه الصفحة بالعربية، لذلك يظهر المحتوى الأصلي بالإنجليزية.

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

## Document CRUD

<<< @/snippets/crud.ts

## SQL query

<<< @/snippets/sql.ts

## Live subscription

<<< @/snippets/subscribe.ts

## Database per tenant

Tenant = database (D-51), but the tenant provisioning route is not published
yet. The snippet is retained as a deterministic stub fixture and is skipped in
both qualification modes; no remote runner calls `/api/v1/tenants`.

<<< @/snippets/tenants.ts
