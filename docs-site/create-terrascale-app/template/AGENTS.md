# AGENTS.md (starter-owned)

Ships with every app scaffolded by `create-terrascale-app`.

Follow the TerraScale workspace Shipping code rules when working in a TerraScale repo; downstream apps use their own shipping policy.

Build: `npm run build`. The template has no separate test script; validate scaffold changes with `npm test --prefix create-terrascale-app` from ts-docs.

## Zero configuration

- Every feature and region is always on. Do not add feature flags, activation settings, deploy toggles or tuning configuration.
- Fakes, demo stores and skip-auth belong only in test code and test binaries.

## SDK

- Import from `@terrascale/sdk`. Create clients with `createTerraBaseV1Client({ baseUrl, project, token })`; `terrascale.ts` does this on the server.
- Address data as `client.database(name)`, then run SQL with `execute(sql, args)`, `query(sql, args)` or `batch(statements)`. Each call is one request and one transaction; a batch commits all of its statements or none.
- Pass values as `?` or named arguments, never by string interpolation. A refused statement throws `SqlError`.
- Primary keys are ULIDs from `generateId()`.
- Do not invent APIs. Check the package's `types/index.d.ts` for the exact surface.

## Credentials

- `TERRASCALE_API_KEY` is server-only. Never expose it through `NEXT_PUBLIC_` settings, client components or logs.
