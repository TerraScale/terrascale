# Platform databases

TerraScale services store their state in TerraBase SQL databases. TerraBase seeds their catalog bindings; each service provisions its own schema and owns its records.

| Resource | ULID | Name |
|---|---|---|
| Organization | `00000000000000000000000001` | TerraScale |
| Auth project | `00000000000000000000000002` | ts-auth |
| Auth database | `00000000000000000000000003` | ts-auth |
| Dashboard project | `00000000000000000000000005` | ts-dashboard |
| Dashboard database | `00000000000000000000000004` | ts-dashboard |

`Features/Platform/PlatformDatabases.cs` defines these identities. Startup seeds missing fixed catalog bindings conditionally and refuses conflicting identities. `/readyz` answers 503 while that seed is pending. When the catalog already holds every platform record as the seed writes it, the seed answers from catalog reads and opens no catalog write. The seed ends when both platform databases exist, or when it meets a state that only an operator can change: another record holds a platform organization, project or database id, a platform project is closed, or a platform database id is permanently deleted. It logs that state as an error with a stable code such as `platform_organization_taken`, `platform_project_taken`, `platform_database_taken`, `platform_project_closed` or `platform_database_purged`, and does not retry before the next start; either way readiness stops waiting for it. Any other failure keeps the seed pending: it tries again every 5 seconds and logs a warning each time the reason changes. Such failures include a catalog that has not passed its restore gate or has an unsupported format, busy storage or unavailable storage accounting, a cleanup intent that still owns a platform database, and a platform database file without its catalog record, which the file audit deletes 24 hours after it first sees it. Seeding storage does not grant customer access or replace ts-auth's independent identity bootstrap. Verify placement and data convergence against the serving revision rather than inferring them from a catalog entry.

Auth and dashboard use the .NET SDK's SQL endpoint, the dashboard through the TerraBase EF Core provider. Every call requires the listener's credential and authorization boundary. Service credentials never belong in a customer browser, tool output or logs. Missing or incompatible issuer/audience metadata fails closed.

- [Auth storage](../auth/storage.md).
- [Dashboard SQL storage](../dashboard/state-storage.md).
- [SQL catalog](sql-catalog.md).
- [Management API](management-api.md).

Platform-admin inspection remains read-only and must redact credential material before returning data. All writes go through the owning service's API. The generated reference is authoritative for the currently exposed inspection operations.

A push stream that returns NATS error 10074 is not retried, because the stream wants more replicas than the cluster has.
