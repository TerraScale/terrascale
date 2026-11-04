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
- Each area's Fast suite must finish in under 30 s wall time; each single test must finish in under 10 s. A Fast suite over 30 s or a test over 10 s is a bug. Keep default parallelism.
- Test only the happy path and critical paths in Fast; move heavy critical tests to Slow and delete everything else. Delete at least 80% of tests against the workspace `.cos/verify-20261103/test-policy.md` baseline (2026-11-04). New features add at most one happy-path and one critical-failure test.
