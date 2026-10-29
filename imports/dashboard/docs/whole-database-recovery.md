# Whole-database point-in-time recovery

Backups → Restore database at UTC time reads the actual continuous PITR window from the chosen platform or customer backup source. Customer selects existing project backup history; it does not configure the restored database's storage. Manual backups are independent of this window.

Enter a retained UTC timestamp (latest by default) and a new database name. The advisory plan is **metadata only**: it shows the chosen snapshot and applicable segment count/latest time. It neither reserves history nor proves replay bytes, object integrity, segment continuity or transaction timestamps. The actual restore can still refuse incomplete, unreadable or unproven history.

Type the new name exactly to confirm. Restore creates a separate database in the same project, consumes one organization database slot and normal storage quota, and preserves the source. The dashboard checks live management/provisioning access, name availability and quota, refreshes UTC bounds, and confirms the returned identity via managed database readback before recording success. Fixed errors preserve nonsecret form fields; upstream bodies and messages are never shown. Interrupted requests can have an unknown outcome: inspect the database list before submitting another restore. Operation lifecycle, placement and snapshot locality remain backend-owned.

The existing packaged ts-dotnet methods call GET `…/point-in-time?destination=platform|customer`, POST `…/point-in-time/plan` and POST `…/point-in-time/restore`. Window and plan use current-user `databases:read`; restore uses `databases:write`, then readback uses `databases:read`. All remain pinned to the source home region. No backend contract, storage format or credential changes.

MCP tools (each accepts optional organizationId/projectId):

- `get_point_in_time_window(databaseId, destination="platform")`: actual window and organization database allowance.
- `plan_point_in_time_recovery(databaseId, pointInTime, destination="platform")`: metadata-only advisory plan. Pass a UTC ISO timestamp ending in Z.
- `restore_database_point_in_time(databaseId, pointInTime, name, confirmName, destination="platform")`: explicit user-approved restore as a new database; confirmName must match name.

Selective recovery remains separate: compare historical/current records and approve individual identities for restoration into the original database. Whole-database PITR never replaces that approval mechanism.

HTTP/MCP application journeys use isolated test providers and historical snapshot copies, not a TerraBase binary. They prove dashboard transport, authority, validation, safe errors, readback and source preservation. They do not prove the backend replay engine, customer bucket contents, multi-day retention, production release or live restoration; those require the owning release/live proof.
