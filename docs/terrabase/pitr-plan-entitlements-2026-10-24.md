# Point-in-time restore plan admission

Point-in-time restore creates a new database in the source project. It uses the
same organization-wide database admission as create and branch, including the
transactional recheck when registering the result. Free (`hobby`) permits one
live database across the organization's projects; Starter permits five. A restore
at capacity returns `409 database_limit` and registers no new database. Restored
databases consume a slot; deleting one releases that slot.

Restore windows and continuous-backup retention follow the stored organization's
plan: Free 1 day, Starter 3 days, Pro 7 days, Team 14 days, Enterprise 30 days.
`backup-retention-days` is no longer a process setting. The old ts-orch service
spec's `TERRABASE_BACKUP_RETENTION_DAYS=hobby=7,pro=7,enterprise=7` widened Free's
window; remove that obsolete environment entry when rolling this change.
Environment settings are read only for recognized keys, so this entry cannot
change retention after the roll. Explicit command-line or dictionary settings
using the removed key are rejected as unknown settings.

For a database with backup history, a point before the plan's retention floor or
after now returns `400 invalid_request`. Its message includes the earliest and
latest available restore timestamps, in round-trip ISO 8601 format. A point
within the plan's time range that the backup cannot prove still returns
`409 backup_history_incomplete`; missing history must never become a successful
restore. Missing or fully expired backups continue to return `404 not_found`.
The response schema and persisted storage format are unchanged.

Fast integration coverage uses enrolled project tokens, the real engine and
continuous backup against a test object store. It covers Free quota rejection,
Starter restores and concurrent competition for the final slot, and older
history rejected by Free but accepted within Starter's and Pro's windows.
Production validation must use fresh tagged accounts after the FR1 roll. A live
restore of history older than one day additionally requires an account/database
whose own backup history has aged that long; a fresh account cannot prove it.
