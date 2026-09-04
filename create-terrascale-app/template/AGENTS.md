# AGENTS.md (starter-owned, D-74)

This file ships with every app scaffolded by `create-terrascale-app` so an
AI-assisted builder gets a working assistant on minute one.

## SDK

- Import from `@terrascale/sdk`. The public factory is
  `createTerraScaleClient(config)`; create one client per request/server action.
- Database management calls always include the `databaseId`, for example
  `client.databases.get(databaseId)`.
- The current SDK's reactive client is `TerraScaleSyncV2Client`. Call
  `await sync.subscribe({ databaseId, subscription, query, parameters })` and
  observe `live.view`; do not use the removed legacy constructor,
  collection facade, or callback-per-document APIs.
- The checked-in SDK currently has no typed document CRUD family. Do not invent
  `client.from`, `client.documents`, or a generic request helper; wait for the
  published Database API contract before adding writes to application code.

## Schema builder

- Author the schema with `defineSchema` plus field builders such as `string()`
  and `timestamp()` in code; `terrascale.schema.json`
  is the contract that `terrascale push` applies and codegen reads.
- After editing the schema: `terrascale push`, then `terrascale codegen`.

## SQL builder

- Queries are read-only SQL over extracted columns + `body` JSON paths.
- Happy path: typed builder ending in `.subscribe()` (live, CDN-backed HTTP
  by default) or `.execute()` (one shot). `.subscribe({ transport: "websocket" })`
  opts into the direct transport.
- Raw parameterized SQL is available; no SQL DML (writes stay document ops).

## terrascale dev

- `terrascale dev <database> --watch=false --no-log-tail` creates or reuses a
  cloud dev branch (no emulator). Seed execution is pending its CLI contract.
- `terrascale diff`, `terrascale doctor`, fault injection, codegen, and seed
  execution become available as their public CLI contracts are published.

## MCP setup

- The MCP server is a client of `@terrascale/sdk` with per-call authorization
  and audit logging. Point it at this project's database and API key via the
  dashboard's MCP panel; scope keys per tenant for customer data.
