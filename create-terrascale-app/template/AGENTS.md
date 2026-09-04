# AGENTS.md (starter-owned, D-74)

This file ships with every app scaffolded by `create-terrascale-app` so an
AI-assisted builder gets a working assistant on minute one.

## SDK

- Import from `@terrascale/sdk`. Create one client per request/server action.
- Document CRUD: `ts.from(collection).create/update/patch/delete`.
- Never concatenate SQL strings on the happy path; use the typed builder.

## Schema builder

- Author the schema with `defineSchema` + `s` in code; `terrascale.schema.json`
  is the contract that `terrascale push` applies and codegen reads.
- After editing the schema: `terrascale push`, then `terrascale codegen`.

## SQL builder

- Queries are read-only SQL over extracted columns + `body` JSON paths.
- Happy path: typed builder ending in `.subscribe()` (live, CDN-backed HTTP
  by default) or `.execute()` (one shot). `.subscribe({ transport: "websocket" })`
  opts into the direct transport.
- Raw parameterized SQL is available; no SQL DML (writes stay document ops).

## terrascale dev

- `terrascale dev --seed ./seed.ts` opens a personal cloud dev branch in the
  nearest region (no emulator, nothing to install but the CLI).
- Useful flags: `--faults` to test injected failures; `terrascale diff` to
  preview schema changes; `terrascale doctor` when stuck.

## MCP setup

- The MCP server is a client of `@terrascale/sdk` with per-call authorization
  and audit logging. Point it at this project's database and API key via the
  dashboard's MCP panel; scope keys per tenant for customer data.
