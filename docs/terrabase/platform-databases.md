# Platform databases

TerraScale services store their state in TerraBase SQL databases. TerraBase seeds their catalog bindings; each service provisions its own schema and owns its records.

| Resource | ULID | Name |
|---|---|---|
| Organization | `00000000000000000000000001` | TerraScale |
| Auth project | `00000000000000000000000002` | ts-auth |
| Auth database | `00000000000000000000000003` | ts-auth |
| Dashboard project | `00000000000000000000000005` | ts-dashboard |
| Dashboard database | `00000000000000000000000004` | ts-dashboard |

`Features/Platform/PlatformDatabases.cs` defines these identities. Startup seeds missing fixed catalog bindings conditionally and refuses conflicting identities. Seeding storage does not grant customer access or replace ts-auth's independent identity bootstrap. Verify placement and data convergence against the serving revision rather than inferring them from a catalog entry.

Auth and dashboard use the .NET SDK's SQL endpoint, the dashboard through the TerraBase EF Core provider. Every call requires the listener's credential and authorization boundary. Service credentials never belong in a customer browser, tool output or logs. Missing or incompatible issuer/audience metadata fails closed.

- [Auth storage](../auth/storage.md).
- [Dashboard SQL storage](../dashboard/state-storage.md).
- [SQL catalog](sql-catalog.md).
- [Management API](management-api.md).

Platform-admin inspection remains read-only and must redact credential material before returning data. All writes go through the owning service's API. The generated reference is authoritative for the currently exposed inspection operations.

A push stream that returns NATS error 10074 is not retried, because the stream wants more replicas than the cluster has.
