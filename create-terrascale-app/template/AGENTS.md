# AGENTS.md (starter-owned)

Ships with every app scaffolded by `create-terrascale-app`.

## Zero configuration

- Every feature and region is always on. Do not add feature flags, activation settings, deploy toggles or tuning configuration.
- Service origins and regions are discovered automatically. A node derives its region from where it runs.
- Service and vendor credentials are provisioned automatically. Customer credentials still authorize customer data access.
- Fakes, demo stores and skip-auth belong only in test code and test binaries. Existing SDK configuration is removal inventory; do not invent an unpublished replacement API.
- APIs and subjects are v1. CI rejects new switches; review requests changes.

## SDK

- Import from `@terrascale/sdk`. Use the public factory `createTerraScaleClient(config)`, and create one client per request or server action.
- Database management calls always include the `databaseId`, e.g. `client.databases.get(databaseId)`.
- Use the published v1 subscription contract. Do not invent constructors, collection facades or callback-per-document APIs.
- The SDK has no typed document CRUD yet. Do not invent `client.from`, `client.documents` or a generic request helper; wait for the published Database API contract before adding writes.

## Schema builder

- Author the schema in code with `defineSchema` and field builders such as `string()` and `timestamp()`. `terrascale.schema.json` is the contract that `terrascale push` applies and codegen reads.
- After editing the schema, run `terrascale push`, then `terrascale codegen`.

## SQL builder

- Queries are read-only SQL over extracted columns and `body` JSON paths. No SQL DML; writes stay document operations.
- Use the typed builder ending in `.subscribe()` for live updates, or `.execute()` for a one-shot query. Raw parameterized SQL also works. Transport discovery is automatic.

## terrascale dev

- `terrascale dev <database>` creates or reuses a cloud dev branch. There is no emulator.
- Seed execution, `terrascale diff`, `terrascale doctor`, fault injection and codegen become available as their public CLI contracts are published.

## MCP setup

- The MCP server is a client of `@terrascale/sdk` with per-call authorization and audit logging. Point it at this project's database and API key from the dashboard's MCP panel. Scope keys per tenant for customer data.
