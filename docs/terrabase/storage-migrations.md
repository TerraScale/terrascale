# Storage migrations

The storage format is an integer. Schema digests live in
`src/TerraBase/Engine/Turso/storage-format.lock`, and the storage gate compares them with the schema
constants. Schema changes require a new version, a new lock entry and a consecutive SQL migration.
Keep each migration unchanged for at least 60 days. Do not edit previous lock entries.

A SQL step runs with full synchronization in one transaction, together with its `user_version` update.
The first step, 23 to 24, changes only this version. Existing format-23 files have `user_version=1`;
that value identifies the starting format. New files receive the current format directly.
Restored files follow the same migration path when opened. There is no dump-and-restore migration.

Startup migrates the catalog first, then the storage intent database, then every client database.
The process holds the data directory lock throughout. A durable `.terrabase.migration` record contains
`migrating:N->M` before any database changes. It also recovers an interrupted write to the lock record.
Completed files are skipped on restart. After every file finishes, the lock records M, then the journal
is removed and the directory is synchronized.

The catalog gate opens only after migration finishes. Its 10-second object-store anchor deadline
starts afterwards; migration has no application startup deadline. The deployment readiness budget
is 1800 seconds. A migration that cannot finish within that budget on the largest node needs a
deployment budget adjustment before release. This first step updates one page per file and never scans
customer rows. This change does not establish a duration bound for future steps.

Directories newer than this build or without a retained forward path are refused before migration.
Rollback after migration requires a newer fixed build or the pre-upgrade backup and previous image.

Page pull requires equal format headers in both directions. Missing headers from previous builds are
refused before pages are consumed. Revision page notifications use a format-qualified subject; previous
builds reject the additional subject component, and this build rejects unqualified or different-format
subjects. During a region-by-region release, page transfer waits for both peers to upgrade. Logical
replication protocol evolution is separate from page-format compatibility.
