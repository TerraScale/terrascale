# Platform databases

TerraScale services store their state in TerraBase SQL databases. TerraBase seeds their catalog bindings; each service provisions its own schema and owns its records.

| Resource | ULID | Name |
|---|---|---|
| Organization | `00000000000000000000000001` | TerraScale |
| Auth project | `00000000000000000000000002` | ts-auth |
| Auth database | `00000000000000000000000003` | ts-auth |
| Dashboard project | `00000000000000000000000005` | ts-dashboard |
| Dashboard database | `00000000000000000000000004` | ts-dashboard |

`Features/Platform/PlatformDatabases.cs` defines these identities. Each region seeds its own platform databases, homed in that region with no replicas ([placement lock](platform-placement-lock.md)). Seeding storage does not grant customer access or replace ts-auth's independent identity bootstrap. Verify placement and data convergence against the serving revision rather than inferring them from a catalog entry.

## Seed and readiness

Startup seeds missing fixed catalog bindings conditionally and refuses conflicting identities. The seed waits for the catalog's restore gate, logs one warning when it starts waiting, and starts the moment the gate opens. When the catalog already holds every platform record as the seed writes it, the seed answers from catalog reads and opens no catalog write.

`/readyz` answers 503 while the seed holds readiness: until each platform database exists or is in a state that only an operator or the file audit changes. Until then a closed restore gate, an unavailable answer (a catalog with an unsupported format, busy storage, unavailable storage accounting, a cleanup intent that still owns a platform database) or a failed attempt keeps readiness held; the seed tries again every 5 seconds and logs a warning each time the reason changes. Once released, readiness is never held by the seed again. These are the states only an operator or the file audit changes:

- `platform_organization_taken`: another record holds the platform organization id. It stops both databases.
- `platform_project_taken`: another organization holds a platform project id. It stops both databases.
- `platform_project_closed`: the platform project is closed.
- `platform_database_taken`: the name holds another database, or the platform database with another home region or with replicas.
- `platform_database_purged`: the platform database id is permanently deleted.
- `platform_database_orphaned`: a file that would make creating the platform database fail exists in its project folder and no catalog record, cleanup intent, purge record or deletion authority holds it: the database's `.db` or one of its sidecars, or a temporary file that a create or a rewrite left when it did not finish (`.<id>.<nonce>.creating` or `.<id>.<nonce>.rewriting`, with or without a sidecar suffix), each also in another letter case. The file audit counts it as unowned and deletes it 24 hours after it first sees it ([orphan-files-audit.md](orphan-files-audit.md)).

The seed logs each of these codes as one error line when an attempt first finds it, also when that attempt then fails on the other database. When the seed ends with some of these states, one information line names the platform databases it seeded. A state on one database does not stop the other from being seeded. Once every database is seeded or in one of the first five states, the seed ends. An orphaned file releases readiness as well, but the seed keeps trying that database every 5 seconds and creates it as soon as its files are gone, without a restart.

While the seed is still trying, the `terrabase.platform.seed_pending` gauge (`terrabase_platform_seed_pending` on `/metrics`) is 1 with the reason's stable code as its `reason` label: `catalog_unverified` until the restore gate opens; `seeding` while an attempt runs and readiness is still held; the code of the unavailable answer that keeps the seed pending, among them `platform_database_raced` (another database took a platform name during the attempt) and `platform_project_unready` (the platform project could not be read for the add); `internal_error` when an attempt fails with an exception, whose warning carries the exception; and `platform_database_orphaned` while the node serves and the seed retries an orphaned file. The gauge has no series once the seed ends. The `/readyz` body carries no reason, as for its other causes of 503.

## Repairs only an operator makes

- `platform_database_orphaned`: the file may be the only copy of that service's data in this region. To keep it, copy the database's `.db` with its `.db.key` and its other sidecars (`-wal`, `-shm`, `-journal` and the revision manifests), under `<organization>/<project>/` in the data directory, out of the data directory before the audit deletes them. Only this node's `TERRABASE_MASTER_KEY` can read the copy, and no TerraBase call registers an existing file. A `.creating` or `.rewriting` leftover is a temporary file of a create or a rewrite that did not finish: move it, with its sidecars, out of the project folder the same way. Once the files are gone, deleted by the audit or moved out by hand, the seed creates an empty platform database within 5 seconds and the service provisions its schema again.
- `platform_database_taken` because another database holds the name: that database is the service's live store in this region, since ts-auth and the dashboard open their database by project and name. Never delete it: a delete purges the service's data. Rename it through the management API as the platform organization's creator (the internal listener, the peer credential and `X-TerraBase-User-Id: 00000000000000000000000001`), which keeps it readable under its new name, then restart TerraBase: the next start seeds the empty platform database under the name, and the service provisions its schema there. The service's earlier data stays in the renamed database: copy the rows the service still needs into the platform database with SQL through the peer credential, and keep the renamed database until nothing needs it.
- `platform_database_taken` because the platform database has another home region or replicas, and `platform_project_closed`, `platform_database_purged`, `platform_organization_taken` and `platform_project_taken`: no API call clears them. Closure fences and purge records are permanent, and no route rewrites the platform organization, a platform project, or a platform database's placement or home. Only a production reset of that region's catalog does, which needs the owner's approval.

## Refused calls

TerraBase refuses every call that would take a platform database away from its service before it writes anything. Deleting, renaming or setting a TTL on a platform database (`00000000000000000000000003` or `00000000000000000000000004`), or renaming another database onto `ts-auth` in the auth project or onto `ts-dashboard` in the dashboard project, answers 409 `platform_database_protected`. Closing a platform project answers 409 `platform_project_protected`. Changing the platform organization's plan, on the management or the administrative route, answers 403 `platform_plan_locked`. Moving a platform database's home answers 409 `platform_placement_locked`. Every other database of the platform organization stays deletable and renamable by the operator. Before the seed created them, enrolling a platform project, creating the platform organization or a platform project from a project token, and adopting one from another region's catalog answer 404, so only the seed creates them; afterwards the existing records answer, and enrolling a platform project into the platform organization answers 200. The TTL expiry sweep and a closing project's batch erasure skip the platform database ids, so a TTL that an older build wrote, or that a regional handoff imported, cannot purge them. A closure record of a platform project from an older build or a handoff import never settles and changes nothing here: the closure pass skips that project, so its databases, TerraBase API keys, backup destination and file buckets stay, and logs one error line `platform_project_protected` naming the project once per process. Its fence still holds: the seed reports `platform_project_closed`, the project refuses new databases and new TerraBase API keys, and its platform database keeps answering the service. Customer organizations, projects and databases are unaffected.

One path from older builds remains here and is handled in a separate pull request on `ObserveShreddedAsync`: a region on an older build that deletes its platform database shreds that database's backup key scope, which carries no region, and every node that then sees the shred record, through the shredded-database sweep or a peer's revocation, unregisters and purges its own copy. Its next start ends with `platform_database_purged`.

Auth and dashboard use the .NET SDK's SQL endpoint, the dashboard through the TerraBase EF Core provider. Every call requires the listener's credential and authorization boundary. Service credentials never belong in a customer browser, tool output or logs. Missing or incompatible issuer/audience metadata fails closed.

- [Auth storage](../auth/storage.md).
- [Dashboard SQL storage](../dashboard/state-storage.md).
- [SQL catalog](sql-catalog.md).
- [Management API](management-api.md).

Platform-admin inspection remains read-only and must redact credential material before returning data. All writes go through the owning service's API. The generated reference is authoritative for the currently exposed inspection operations.

A push stream that returns NATS error 10074 is not retried, because the stream wants more replicas than the cluster has.
