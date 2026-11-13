# Storage migrations

The storage format is an integer. Schema digests live in
the storage implementation’s `storage-format.lock`, and the storage gate compares them with the schema
constants. Schema changes require a new version, a new lock entry and a consecutive SQL migration.
Keep each migration unchanged for at least 60 days. Do not edit previous lock entries.

A SQL step runs with full synchronization in one transaction, together with its `user_version` update.
The first step, 23 to 24, changes only this version. Existing format-23 files have `user_version=1`;
the opener interprets that value as format 23. Format 22 and earlier also used 1, so the stamp
alone cannot establish a restored backup's format. Backup manifests currently carry no storage
format. An older restored file stamped 1 is treated as 23 and receives this no-data step; earlier
schema differences are not checked or migrated. Establish a pre-23 backup's format separately
before restoring it. New files receive the current format directly.
PARKED: next slice: format in the backup manifest.

Startup checkpoints the catalog and storage intent database, migrates temporary copies of both,
and checkpoints and flushes those copies before publishing a migration journal or changing the lock.
A failure preparing either copy leaves both originals at their previous versions and the directory
available to the previous build. The intent copy is reported as `_intents`, independently of `_system`.
After both copies succeed, a durable `.terrabase.migration` record contains `migrating:N->M`;
it also recovers an interrupted write to the lock. Publishing the copies can resume after a crash:
a file already at M skips the SQL step.

The catalog and intent stores then open for write-intent recovery. Local create/delete cleanup and
the file ownership audit finish before the tenant pass. The crash-safe create/delete change must
preserve the call to `MigrateDatabasesAsync` after successful `RecoverAsync`, including deferred
recovery after catalog verification. Cleanup of unpublished files must stay before that call.
A cleanup failure keeps readiness closed. The tenant pass runs on every start, including when the
directory already records M, so files previously left behind are retried.

A tenant failure logs the database id once during the pass and increments
`terrabase.storage.migration_failures`; `terrabase.storage.old_format_files` counts files awaiting
successful migration, including files whose format cannot be read. Other tenants continue.
Opening an affected file tries migration again or refuses that tenant. Successful opening removes
it from the gauge. After the pass, the lock records M and the journal is removed and synchronized.
Readiness waits for the whole pass. Cached replica copies at another file version are discarded
and downloaded again from their home; they are never served at an old version.
Catalog verification follows mandatory-store migration. Its 10-second object-store anchor deadline
starts afterwards; migration has no application startup deadline. The deployment readiness budget
is 1800 seconds. A migration that cannot finish within that budget on the largest node needs a
deployment budget adjustment before release. This first step updates one page per file and never scans
customer rows. This change does not establish a duration bound for future steps.

Directories newer than this build or without a retained forward path are refused before migration.
For rollback instructions see Going back below.

Page pull requires equal format headers in both directions. Missing headers from previous builds are
refused before pages are consumed. Revision page notifications use a format-qualified subject; previous
builds reject the additional subject component, and this build rejects unqualified or different-format
subjects. During a region-by-region release, page transfer waits for both peers to upgrade. Logical
replication protocol evolution is separate from page-format compatibility.

Format-qualified revision notices record commit time before checking the format. The pull waits
while the formats differ. `terrabase.replica.format_refused` counts refused notices and
`terrabase.replica.format_blocked` counts blocked followers, both labeled `peer_region`; replica
lag keeps increasing while blocked and clears after a compatible pull or confirmation that the revision is already held. Periodic pulls\ncontinue to discover an upgraded peer even if it sends no further writes.

## Going back

A migrated directory cannot be opened by the previous build. Never empty a data directory or
discard affected data in response to the previous build's refusal message. Keep the stopped
directory and roll forward, restore a pre-upgrade backup, or use the version-only undo below.

The 23 to 24 step changes no data. Stop the node and verify that no process holds its directory
before undoing it. Use the same service identity, directory and master key as that node.
The helper first restores the directory lock, then sets every encrypted database's version stamp
(including `_system.db`, `_intents.db` and cached copies under `.replicas`) to 1, checkpoints the
files, removes the migration journal and synchronizes the directory. Do not restart either build
between these operations. Keep shell tracing off and supply the existing master key securely
through `TERRABASE_MASTER_KEY`; the helper never prints it.

Run from this repository on a Linux node with Python 3 and Node. `BUILD` is the candidate's
build directory containing its assemblies and native library; `DATA` is the stopped directory.

```sh
export PATH="$HOME/.local/bin:$HOME/.local/share/mise/shims:$PATH"
nice -n 19 python3 scripts/storage-format-undo.py "$BUILD" "$DATA"
```

Start the previous image only after every file succeeds and the helper prints
`undo complete; start the previous image`. If any key or database is damaged, keep the node
stopped until that file is repaired; do not ignore a failed undo. The PR includes the local
rehearsal commands and outputs. A future step that changes data must keep a copy per file or
require a volume backup before migration; changing version stamps alone cannot undo data changes.
