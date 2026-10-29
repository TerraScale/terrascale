# E4a: protocol and collection-hook removal

Pivot decisions D1 and D3 remove the public Hrana/libSQL pipeline and cursor endpoints under `/libsql`, the custom
document TCP protocol, and collection change webhooks. Their feature folders, dedicated tests, protocol proof clients,
configuration, startup registrations and API-reference entries are removed. PostgreSQL, SQL over REST and shapes
remain. A new SQL WebSocket protocol belongs to E3; this deletion does not implement it.

This is an intentional breaking API change, with no compatibility endpoint or migration. The custom listener's
`wire-port` setting and default port 7464 are removed. Function change triggers and their delivery-history endpoint
are removed with their collection-hook dependency; HTTP invocation and cron schedules remain. This deletion does not
introduce a storage format or rewrite existing files.

## Residual-term inventory

The following exact paths explain the remaining case-insensitive `Hrana|libsql|webhook` matches in `src` and `docs`.
They do not allow a public route or an active dependency on a removed TerraBase feature. This inventory itself is
allowed as the record of the removal.

| Path | Allowed term and reason |
| --- | --- |
| `docs/terrabase/e4a-removal.md` | Removal names and this residual-term inventory. |
| `docs/terrabase/encryption-rebased-fixtures-2026-10-24.md` | `webhook`: dated CI and five-repeat fixture evidence at `839065c23`; preserves the meaning of the linked historical report, not current test coverage. |
| `docs/auth/README.md` | `webhook`: ts-auth's own outbound identity events and routes, not TerraBase collection hooks. |
| `docs/auth/competitive-review.md` | `webhook`: ts-auth's identity-provider capability inventory. |
| `docs/auth/parity.md` | `webhook`: historical ts-auth porting inventory. |
| `docs/terrabase/management-api.md` | `webhook`: ownership of Stripe billing callbacks in the dashboard. |
| `docs/terrabase/request-identity.md` | `webhook`: generic warning against sending first-party correlation proofs to third parties. |
| `docs/terrabase/import.md` | `libSQL`: third-party SQLite-format import sources, not a served endpoint. |
| `docs/terrabase/vector-index.md` | `libsql_vector_idx`: names an unsupported third-party index capability. |
| `src/TerraScale.Endpoints/RequestIdentity.cs` | `webhook`: generic first-party credential-propagation restriction for third-party HTTP clients. |
| `src/vendor/Turso.Data.Sqlite/src/Turso.Data/TursoConnectionOptions.cs` | `libsql`: vendored upstream URL parsing. |
| `src/vendor/Turso.Data.Sqlite/src/Turso.Data/TursoSyncDatabaseOptions.cs` | `libsql`: vendored upstream sync URL parsing and validation. |
| `src/vendor/Turso.Data.Sqlite/src/Turso.Data/TursoSyncDatabase.cs` | `libsql`: vendored upstream sync URL normalization. |
| `src/vendor/Turso.Data.Sqlite/src/Turso.Data/TursoRemoteClient.cs` | `HRANA_STREAM_EXPIRED`, `HRANA_STREAM_NOT_FOUND`: vendored upstream remote error classification. |

The E1-owned SQL slice is outside E4a's edit boundary. Its remaining references are pre-pivot comments, not registrations
or routes: `src/TerraBase/Features/Sql/SqlParse.cs` mentions client pragmas; `SqlModels.cs` describes an obsolete integer
encoding alternative; `ISqlService.cs` names old client error mapping, script execution and shared consumers. E1 owns
removing those stale comments. This exception is limited to those comments and does not permit executable protocol code.

Historical measured reports under `benchmark/bench/` retain the original surface names so their results remain
interpretable. They are outside the `src` and `docs` scan.
