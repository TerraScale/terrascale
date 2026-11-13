# Storage migrations

TerraBase changes its on-disk format in place, forward only, and resumably. Every build declares two things in
`StorageFormat`:

- **Supported** (`SupportedFrom` to `SupportedTo`, a contiguous range): the formats it opens and operates on as
  they are. A file or directory in a supported format is read and written in that format; nothing is rewritten.
- **Target**: the format it creates new files in and migrates older files to. Target lies inside Supported.

A format below Target is migrated to Target. A format above `SupportedTo` or below `SupportedFrom` is refused; the
message names the format and the supported range and never asks to empty the data directory.

Today Supported is 23 to 24 and Target is 23, so this build rewrites nothing that production holds.

## Format identities

| Format | Directory record (`.terrabase.lock`) | File `user_version` | Page header and notice subject |
| --- | --- | --- | --- |
| 23 | `23-41a88901325a` | 1 | none: no `TerraBase-Storage-Format` header, 5-token subject |
| 24 | `24` | 24 | `TerraBase-Storage-Format: 24`, 6th subject token `24` |

Format 23 keeps the exact identity earlier builds wrote, so a build that targets 23 is byte-compatible on the wire
and on disk with them. Format 24 has the same layout as 23; only the stamp differs. Format 24 is frozen as this
no-op step, including its lock entry and migration step; the first real layout change will be format 25.

The directory record names the highest format written into the directory. While a migration runs, it reads
`migrating:<from>-><to>`; that record is the migration journal.

## Releasing a format change: two steps

1. Ship a build that adds the new format to Supported, with its migration step and lock entry, and keeps Target.
   It opens data in either format and writes the old one.
2. Before raising Target, every node in every region and the image the release system would reinstall must
   already support the new format. Otherwise previous-build followers refuse the new format and drop its notices.
   Then ship the one-line change that raises Target. After a rehearsal, label that pull request `storage-format`.

Before ever raising the lowest supported format (`SupportedFrom`), `terrabase.storage.old_format_files` must be 0
on every node.

If step 2 does not become ready, the automatic reinstall of the previous image lands on the step 1 build, which
supports the migrated directory and simply runs. No undo is needed for an ordinary rollback. The undo below is for
going back two releases and for rehearsals.

`scripts/check-storage-format.sh` enforces this in CI against the base branch: Target may only move to a format
the base build already supports, Target never moves back, the base build's Target stays supported, every adjacent
pair of supported formats has a migration step, every supported format has a lock entry, the schema text matches
the lock entry of Target, existing lock entries never change, and a step stays unchanged for at least 60 days.

A change to any file that shapes the bytes on disk needs either a new format step or the `storage-format-unchanged`
label, applied once a reviewer confirms the bytes on disk stay the same. Those files are everything in the storage
engine's directory (the one holding `storage-format.lock`: schema, change log, key record, cipher and page settings),
the stored-file and revisions sidecar records (`Engine/StoredDatabaseFile.cs`, `Engine/Revisions/RevisionManifest*.cs`),
the snapshot bundle (`Features/Backup/SnapshotBundle.cs`), the vendored storage client and the pin of the native
storage library. The labels `storage-format` and `storage-format-unchanged` contradict
each other. `storage-format` with `Needs format bump` in the description is the pending case the old gate allowed.

A SQL step runs with `synchronous = FULL` in one transaction together with its `user_version` update.

## What crosses nodes

| Crossing | How the format travels | What the receiver does |
| --- | --- | --- |
| Page pulls | The puller sends `TerraBase-Storage-Formats: <from>-<to>` (absent: 23 only). The server answers with the format of its file in `TerraBase-Storage-Format` (absent: 23). | The server refuses with retryable 503 `storage_format_unsupported` only when the puller does not support its file's format. The puller accepts any supported format. |
| Revision notices | The 6th subject token is the format of the home's file (absent: 23). | A supported format is handled as usual; another is counted in `terrabase.replica.format_refused` and shown in `terrabase.replica.format_blocked`, and the periodic pull catches up later. |
| Replica caches | The cache holds the home's pages, so it keeps the format they arrived in. | A cache in a supported format is reopened as it is. A cache in an unsupported format is refused and kept on disk; it is never deleted for its format. |
| Handoffs | The promoted replica file opens through the normal store open. | Supported: used as it is, or migrated to Target if older. Unsupported: refused, file kept. |
| Snapshots and backups | A snapshot copies the file's `user_version`. | Restoring opens the file through the store open, with the same rule. |

The replica copy keeps the format it arrived in rather than migrating, because it mirrors the home's pages; a local
rewrite would make it diverge from the pages the home sends next.

## Start sequence

1. Lock the data directory and read its record. An unsupported or unknown record refuses the start.
2. If the record is below Target, or is a `migrating:` record whose target is not above Target, migrate the two
   mandatory files, `_system.db` and `_intents.db`, before they open (durable order below).
3. Open the system and intent stores.
4. Recover cleanup intents and audit files. A failed attempt, or one that finds a dependency unavailable, is
   retried with backoff (1 s doubling to 60 s) on the host's stopping token. Readiness waits for this step only.
5. When there is work (step 2's condition), migrate tenant files in the background, after readiness. A file whose
   store is open or opening is skipped: its own open migrates it. A file being migrated makes its own opens wait
   for that file; nothing reads it half-migrated. A file that fails is logged, counted in
   `terrabase.storage.migration_failures` and `terrabase.storage.old_format_files`, and migrated when it is next
   opened. Only a file the migration changed is checkpointed. At the end the record becomes Target.
6. When nothing needs migrating (the usual case), step 5 reads the record and returns.

Stopping the node cancels steps 4 and 5; each file's migration is one transaction, so the next start resumes.

## Durable order for the mandatory files

For each of `_system.db` and `_intents.db`:

1. Checkpoint the original with `wal_checkpoint(TRUNCATE)`.
2. Delete any earlier `.migrating` copy, then copy the file and its key record to `<file>.migrating`.
3. Migrate the copy (one transaction per step, `synchronous = FULL`), checkpoint it, then `fsync` it.

Then:

4. Write `migrating:<from>-><to>` to the directory record and `fsync` it.
5. For each file: delete the original's `-wal` and `-shm` and the copy's, `fsync` the directory, rename the copy
   over the original, delete the copy's key record, `fsync` the directory.

A stop or power cut before step 4 leaves the originals and the old record; the next start begins again. After step 4
the next start repeats the sequence from the current files, which is idempotent. Deleting the stale `-wal` before
the rename means old WAL frames can never be replayed over a migrated file.

## Going back two releases

The undo stamps a stopped node's directory from format 24 back to 23. It works only because 24 changes no data.
With the node stopped and its master key in `TERRABASE_MASTER_KEY`:

    nice -n 19 python3 scripts/storage-format-undo.py <build directory> <data directory>

It takes the directory lock, stamps every database file it can to `user_version = 1`, and lists each file it
could not stamp with the reason. If any remain, it exits non-zero and leaves the record unchanged, so builds that
support only 23 still refuse the directory. Fix the listed files and run it again: stamped files are skipped.
When every file is at 23, it writes `23-41a88901325a` to the record and syncs the directory.

A future step that changes data cannot be undone by stamps; it needs a copy per file or a volume backup first.

## Restores

A backup in an older supported format is migrated to Target when it is opened after the restore. A backup in a
format this build does not support is refused, and the file is kept.

Known gap: the backup manifest does not record its format yet. The format is read from the restored file's
`user_version`. Formats 22 and earlier also stamped 1, so a backup taken before format 23 would be read as 23; such
backups predate the current layout and must not be restored by this build.

## Proof

`e2e/storage/format-upgrade.py <previous build> <candidate build> <work directory>` runs two built TerraBase
directories in Development on loopback ports 18580 to 18587 and checks: an upgrade rewrites nothing, going back
needs no undo, a directory stamped 24 by a scratch build whose Target is 24 (made from the same source with that one
constant changed) works on the candidate, kills during the migration resume, a previous-build replica cache is
kept on a candidate restart and applies new pages without a rebootstrap, and the undo with one unstampable file.

A node with a failed recovery attempt stays unready; the release system reinstalls the previous image, which
runs on the directory. This release-system recovery is not covered by the script.
