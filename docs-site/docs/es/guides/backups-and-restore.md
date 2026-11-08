---
title: Backups and restore
description: Back up a database and restore or rewind it over HTTP.
---

<!-- i18n:fallback:es -->
> **Traducción pendiente.** Esta página aún no tiene revisión humana en español y muestra el contenido original en inglés.

<div lang="en" dir="ltr">

# Backups and restore

Every database gets continuous backup plus scheduled and manual snapshots, and you restore over plain HTTP: create and list backups, read and set the backup policy, restore a backup or a point in time into a new database, or rewind a database in place. The same operations run through the [SDKs](/es/guides/sdks), the `ts` CLI (`ts backup …`) and the dashboard; for the dashboard and MCP recovery flow see [whole-database recovery](/es/guides/whole-database-recovery).

All routes are under `/v1/projects/{project}/databases/{database}`, where `{database}` is the database's ULID or its current name. Authenticate with a ts-auth project access token (`Authorization: Bearer …`); the project must match the token's `project_id`. Reads need the `databases:read` scope, writes and restores `databases:write`. A restore to a new database never overwrites the source; an in-place restore is the explicit rewind below.

## Create a backup

```
POST /v1/projects/{project}/databases/{database}/backups
```

No request body. Takes a consistent snapshot of the database now; the backup is kept until the policy's retention expires or the database is deleted. Answers `201` with the backup:

```json
{"id":"01ARZ3NDEKTSV4RRFFQ69G5FA4","databaseId":"01ARZ3NDEKTSV4RRFFQ69G5FA1","status":"ready","trigger":"manual","sizeBytes":40960,"createdAt":"2026-10-07T12:00:00Z"}
```

## List backups

```
GET /v1/projects/{project}/databases/{database}/backups
```

Paged like other management lists (`after` and `limit` query parameters, `next` cursor in the response):

```json
{"items":[{"id":"01ARZ3NDEKTSV4RRFFQ69G5FA4","databaseId":"01ARZ3NDEKTSV4RRFFQ69G5FA1","status":"ready","trigger":"manual","sizeBytes":40960,"createdAt":"2026-10-07T12:00:00Z"}],"next":null}
```

## Read and set the backup policy

```
GET /v1/projects/{project}/databases/{database}/backup-policy
```

Until a policy is saved this returns the default: a full backup daily at 02:00 UTC, kept 7 days, with `updatedAt` null. The scheduler applies the default from the database's creation, as it applies a saved policy from its save. The response also reports whether scheduled backups are actually succeeding:

```json
{"fullBackupCron":"0 2 * * *","retentionDays":7,"disabled":false,"updatedAt":null,"lastSucceededAt":"2026-10-07T02:00:04Z","lastFailedAt":null,"lastError":null,"nextDueAt":"2026-10-08T02:00:00Z"}
```

| Field | Meaning |
| --- | --- |
| `lastSucceededAt` | When the newest scheduled backup completed; null if none ever did. |
| `lastFailedAt` | When the newest scheduled attempt failed; null if none ever failed. |
| `lastError` | `{code, message}` of that failed attempt, or null; codes are `snapshot_failed`, `source_missing` (the database's files could not be read) and `abandoned` (the attempt did not finish). |
| `nextDueAt` | The next cron occurrence in UTC; null with no `fullBackupCron` or while `disabled` is true. |

```
PUT /v1/projects/{project}/databases/{database}/backup-policy
```

```json
{"fullBackupCron":"0 2 * * *","retentionDays":7,"disabled":false}
```

`fullBackupCron` is a standard 5-field cron expression evaluated in UTC, or null or empty for no scheduled backups. `retentionDays` (1 to 35) applies to manual and scheduled backups. `disabled` pauses scheduled backups. The saved policy is returned with its `updatedAt`:

```json
{"fullBackupCron":"0 2 * * *","retentionDays":7,"disabled":false,"updatedAt":"2026-10-07T12:00:00Z"}
```

## Restore a backup into a new database

```
POST /v1/projects/{project}/databases/{database}/backups/{backup}/restore
```

```json
{"name":"restored"}
```

Restore never overwrites: it creates a new database in the same project holding the backup's data, with `branchedFrom` set to the backed-up database and the same primary, replica regions and `allRegions` choice. `name` follows the database name rules; without it the first free of `<source>-restore`, `<source>-restore-2`, … is used. The copy runs in the background: the answer is `202` with the restore operation, and `GET …/restores/{operation}` reports `running`, then `completed` with `database` naming the new database, or `failed`:

```json
{"id":"01ARZ3NDEKTSV4RRFFQ69G5FA7","operationId":"01ARZ3NDEKTSV4RRFFQ69G5FA7","status":"running","kind":"backup","database":"restored","errorCode":null,"errorMessage":null,"createdAt":"2026-10-07T12:00:00Z","finishedAt":null}
```

A request sent to the wrong region answers `421` and names the snapshot region; an unknown snapshot region answers `503 backup_region_unknown`, and a snapshot outside current placement `409 backup_region_not_placed`. Platform organization sources answer `409 recovery_erasure_fenced`; use the separately reviewed operator recovery procedure for platform recovery.

## Restore a point in time into a new database

Read the window first, then restore. The window comes from continuous backup, bounded by the plan's retention (see [limits](/es/limits)):

```
GET /v1/projects/{project}/databases/{database}/point-in-time?destination=platform
```

`destination` selects the backup bucket, `platform` (default) or `customer`; each has its own window and there is no fallback between buckets.

```json
{"earliest":"2026-10-07T12:00:00Z","latest":"2026-10-07T12:00:16Z"}
```

`earliest` is the oldest continuous snapshot within the plan's retention; `latest` is the newest time the backup proves restorable. `404` when continuous backup is off or holds no data within the retention window.

Optionally preview the restore first:

```
POST /v1/projects/{project}/databases/{database}/point-in-time/plan
```

```json
{"pointInTime":"2026-10-07T12:00:13Z","destination":"platform"}
```

```json
{"window":{"earliest":"2026-10-07T12:00:00Z","latest":"2026-10-07T12:00:16Z"},"pointInTime":"2026-10-07T12:00:13Z","snapshotAt":"2026-10-07T12:00:10Z","latestSegmentAt":null,"segmentCount":0}
```

The plan is advisory metadata only: it does not verify replay bytes or guarantee a successful restore.

```
POST /v1/projects/{project}/databases/{database}/point-in-time/restore
```

```json
{"pointInTime":"2026-10-07T12:00:13Z","name":"restored","destination":"customer","ttlSeconds":3600}
```

Restores every transaction committed before `pointInTime` (to the millisecond) and none whose commit was recorded at or after it, whole transactions only, into a new database in the same project; the source database itself is unchanged. `name` is optional as for backup restore; `ttlSeconds` is optional. The replay and copy run in the background: the answer is `202` with the restore operation, as backup restore does.

A taken name answers `409 name_taken` and a full database allowance `409 database_limit`, before the backup is read. `400` only when `pointInTime` is after now or before the plan's retention window; the error names the earliest and latest available restore timestamps. A time inside that range but outside the proven restore window answers `409 backup_history_incomplete`, retryable once the backup holds it. When the backup cannot prove the history (`409 backup_history_incomplete` or `restore_point_unproven`) or an object fails its integrity check (`409 backup_unreadable`), nothing is created. Restores execute in the primary region; requests sent elsewhere answer `421`.

## Check restore status

```
GET /v1/projects/{project}/databases/{database}/restores/{operation}
```

Reads a background restore operation with the `databases:read` scope. `status` is `running` while the copy runs, then `completed`, with `database` naming the restored database, or `failed` with `errorCode` and `errorMessage` (for example `interrupted`, when the process stopped while the restore ran). Unknown ids, and ids of another project's or database's operations, answer `404`.

```json
{"id":"01ARZ3NDEKTSV4RRFFQ69G5FA7","operationId":"01ARZ3NDEKTSV4RRFFQ69G5FA7","status":"completed","kind":"backup","database":"restored","errorCode":null,"errorMessage":null,"createdAt":"2026-10-07T12:00:00Z","finishedAt":"2026-10-07T12:00:05Z"}
```

The SDKs poll this route for you and return the restored database once the operation completes.

## Rewind a database in place

```
POST /v1/projects/{project}/databases/{database}/point-in-time/rewind
```

```json
{"pointInTime":"2026-10-07T12:00:13Z","destination":"platform"}
```

Rewind replaces every table, index, view and trigger of the database with its state as of `pointInTime`, in one transaction, keeping the database's id, name, credentials and allowance; changes committed after `pointInTime` are discarded. The time follows the same rules as point-in-time restore: `400` outside the plan's window, `409 backup_history_incomplete`, `restore_point_unproven` or `backup_unreadable` when the backup cannot prove it, and nothing changes on an error. `destination` selects `platform` (default) or `customer`; platform organization sources answer `409 recovery_erasure_fenced`. Answers `200` with the database.

A rewind is an ordinary write: it is itself backed up, so a later rewind can undo it. Because it discards data, every surface asks for explicit consent:

- In the dashboard, after previewing the plan you must type the database's exact name into the confirmation field before **Restore in place** runs.
- In the CLI, `ts backup rewind <database> <time> [platform|customer]` restores the database in place to an RFC3339 instant and requires `-yes`.
- Over HTTP, the request is one authenticated call: obtain the user's explicit approval for the exact database and time before sending it.

## Errors

Errors share the platform's `error` object with a `code` and `message`; see [errors](/es/guides/errors). Backup and restore refusals include `unknown_database`, `invalid_request`, `name_taken`, `database_limit`, `backup_history_incomplete`, `restore_point_unproven`, `backup_unreadable`, `backup_region_unknown`, `backup_region_not_placed`, `recovery_erasure_fenced` and `forbidden_scope`. Beyond the request or quota limits in [limits](/es/limits) a restore answers `429`.

</div>
