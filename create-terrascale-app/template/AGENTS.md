# AGENTS.md (starter-owned, D-74)

This file ships with every app scaffolded by `create-terrascale-app` so an
AI-assisted builder gets a working assistant on minute one.

## Zero configuration

- Every feature and region is always on. Do not add feature flags, activation settings, deploy toggles or tuning configuration.
- Discover service origins and regions automatically. NetBird network membership admits production nodes; a node derives its region from where it runs.
- OCI Vault automatically creates and delivers production service credentials. Vendor-issued keys are stored once in Vault and picked up automatically; customer credentials still authorize customer data access.
- Fakes, demo stores and skip-auth belong only in test code and test binaries. Existing SDK configuration remains removal inventory; do not invent an unpublished replacement API.
- APIs and subjects are v1. CI rejects new switches and review requests changes.

## SDK

- Import from `@terrascale/sdk`. The public factory is
  `createTerraScaleClient(config)`; create one client per request/server action.
- Database management calls always include the `databaseId`, for example
  `client.databases.get(databaseId)`.
- Use the published v1 subscription contract; do not invent constructors,
  collection facades or callback-per-document APIs.
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
- Use the typed builder ending in `.subscribe()` for live updates or
  `.execute()` for a one-shot query. Transport discovery is automatic.
- Raw parameterized SQL is available; no SQL DML (writes stay document ops).

## terrascale dev

- `terrascale dev <database>` creates or reuses a
  cloud dev branch (no emulator). Seed execution is pending its CLI contract.
- `terrascale diff`, `terrascale doctor`, fault injection, codegen, and seed
  execution become available as their public CLI contracts are published.

## MCP setup

- The MCP server is a client of `@terrascale/sdk` with per-call authorization
  and audit logging. Point it at this project's database and API key via the
  dashboard's MCP panel; scope keys per tenant for customer data.
