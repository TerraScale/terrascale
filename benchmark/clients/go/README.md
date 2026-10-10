# Go client prerequisite (TS-761)

The Go benchmark client is blocked on the official `ts-go` SDK's JWT data-plane
surface. This directory deliberately contains no fake runner or API-key fallback.

Verified against ts-go main `05ae299` and its plain-ULID PR
[TerraScale/ts-go#35](https://git.terrascale.tech/TerraScale/ts-go/pulls/35),
revision `49fb07f5b86680037ddf79eac907679ae7d5b8b2`:

- `v1.New` only accepts `^tb_[A-Za-z0-9_-]{43}$`; a JWT is rejected locally.
- `tsauth.NewAPIKeySource` obtains project-scoped JWTs but cannot be supplied to
  `v1.Config` (it has no token source).
- The TerraBase producer supports project-token routes; its source explicitly
  separates them from `LegacyManagementRoute` and `/v1/management`.
- Service-client tokens have no `org_id` or `project_id`. Registering a service
  slug alone cannot grant tenant data access. The dedicated benchmark OAuth
  credential must be a project client with database read/write and document
  read/write scopes.

Required SDK surface: a concurrent-safe JWT token source on the public project
client, database creation/deletion, collection creation, index creation/readiness,
point CRUD, queries and atomic transactions, with regional endpoint resolution.
The benchmark must use those methods for temporary `bench-<ULID>` databases and
for the persistent `benchmark` database. No header-rewriting transport, dummy
`tb_` key, internal administrative listener or compatibility membership/key API.

Once admitted, implement the client using automatic eligible-region discovery
and local region inference under [the protocol](../../docs/protocol.md), and
`internal/benchmark.RunPhase`. Setup, warmup, measured phases and deletion must
share the bounded run lifecycle; final summary is emitted after cleanup.
