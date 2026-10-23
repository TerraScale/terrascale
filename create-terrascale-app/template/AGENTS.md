# AGENTS.md (starter-owned)

Ships with every app scaffolded by `create-terrascale-app`.

## Zero configuration

- Every feature and region is always on. Do not add feature flags, activation settings, deploy toggles or tuning configuration.
- Fakes, demo stores and skip-auth belong only in test code and test binaries.

## SDK

- Import from `@terrascale/sdk`. Create clients with `createTerraBaseV1Client({ baseUrl, project, token })`; `terrascale.ts` does this on the server.
- Address data as `client.database(name).collection(name)`, then `create`, `get`, `put`, `patch`, `delete`, `query`, `pages` or `items`.
- Build filters with `v1Filter` (for example `v1Filter.eq("/authorId", id)`). Sort and filter paths are JSON Pointers or `$id`.
- Pass `ifMatch: version` to `patch` and `delete` for conditional writes; a stale version throws `V1PreconditionFailedError`.
- Do not invent APIs. Check the package's `types/index.d.ts` for the exact surface.

## Credentials

- `TERRASCALE_API_KEY` is server-only. Never expose it through `NEXT_PUBLIC_` settings, client components or logs.
