---
title: Backups and restore
---

<!-- i18n:fallback:de -->
> **Übersetzung ausstehend.** Diese Seite wurde noch nicht auf Deutsch geprüft und zeigt den englischen Originalinhalt.

<div lang="en" dir="ltr">

# Backups and restore

TerraBase backs up every database continuously and on a schedule. A restore never overwrites: it always creates a new database in the same project, with `branchedFrom` set to the source database's id. The source database is unchanged.

## In the dashboard and over MCP

Open a database and choose **Backups**. The page lists the database's backups, newest first, with the current schedule and retention. Owners and admins can **Back up now**, save the full-backup schedule and retention, and restore any listed backup into a new database. The dashboard [MCP server](/de/guides/mcp) offers the same actions as `list_backups`, `create_backup`, `set_backup_policy` and `restore_backup`.

A new database has no backups until its first one runs: either **Back up now**, or the next time its schedule names (by default 02:00 UTC). Incremental schedules aren't offered; leave that field empty. Full backups plus point-in-time restore cover recovery.

A restore always creates a new database, so it counts toward your plan's database limit (see [limits](/de/limits)). On Free, which allows one database, a restore needs a plan with room for a second one.

Deleting a database also deletes its backups: they expire with it and their files are removed. Restore anything you need before you delete it.

## Placement and residency

A restore allocates an independent database; it does not promise to preserve the source's home, explicit replica list or `allRegions` setting. The restore requests below do not accept a destination-placement override. Confirm that the current creation policy and available regions meet your residency requirements **before** starting a restore. Do not restore sensitive data first and rely on removing an unwanted copy afterward.

After an allowed restore, inspect the returned `homeRegion`, `replicaRegions` and `allRegions`, and verify the new database's placement before sending application traffic. An empty replica list with `allRegions: true` means copies follow the serving catalog. A writable target requires at least two regions; a successful creation response alone does not prove that every regional copy is current. [Branches](/de/guides/branches) are also independent databases, with their own returned placement.

For routing refusals, follow the [421 placement and pin guidance](/de/guides/errors#region-routing-errors-421). Database placement controls live copies, while encrypted backup and object-storage locations follow [DPA Annex 3](/de/legal/dpa#annex-3-authorized-subprocessors), including United States processing. See [replication and residency](/de/concepts/replication#residency-and-backups) before selecting a recovery destination.

## Over the HTTP API

The published `@terrascale/sdk@0.0.3` does not wrap these routes yet. The examples use `curl` against the HTTP API.

## Before you start

You need a project access token for this project. Reads need `databases:read`; triggers, restores and policy or destination changes need `databases:write`. A missing or invalid token answers `401`, a missing scope `403`, and another project or a database outside the token's narrowing `404`. See [authentication](/de/guides/authentication).

```bash
export TERRASCALE_API_URL='https://api.terrascale.tech'
export PROJECT='YOUR_PROJECT_ID' DATABASE='orders'
# TERRASCALE_ACCESS_TOKEN comes from your secret manager, never a command-line argument.
api() { curl -sS --fail-with-body -H @<(printf 'Authorization: Bearer %s\n' "$TERRASCALE_ACCESS_TOKEN") "$@"; }
```

`$DATABASE` is the database name. Errors use the body `{"error":{"code":"…","message":"…"}}`.

## Read the restore window

```bash
api "$TERRASCALE_API_URL/v1/projects/$PROJECT/databases/$DATABASE/point-in-time"
```

```json
{ "earliest": "2026-10-15T09:00:00+00:00", "latest": "2026-10-22T14:31:07+00:00" }
```

`earliest` is the oldest continuous backup snapshot, limited by your plan's PITR window (see [plan entitlements](/de/limits#plan-entitlements)). `latest` is the newest time the backup proves restorable. `404` means the backup holds no data of this database within that retention.

## Preview a restore

The plan checks backup metadata without restoring anything.

```bash
api -X POST -H 'Content-Type: application/json' \
  -d '{"pointInTime":"2026-10-22T14:00:00Z"}' \
  "$TERRASCALE_API_URL/v1/projects/$PROJECT/databases/$DATABASE/point-in-time/plan"
```

The response has `window` (as above), `pointInTime`, `snapshotAt`, `latestSegmentAt` (or `null`), `segmentCount` and `historyCheck` (always `metadata`). A real restore checks more, so it can still fail where the plan succeeded. The plan scans at most 10,000 objects within 10 seconds; beyond that it answers `503 pitr_plan_limit` or `503 pitr_plan_timeout`.

## Restore to a point in time

`POST …/point-in-time/restore` copies every transaction committed before `pointInTime` into a new database. Whole transactions only.

```bash
api -X POST -H 'Content-Type: application/json' \
  -d '{"pointInTime":"2026-10-22T14:00:00Z","name":"orders-before-import"}' \
  "$TERRASCALE_API_URL/v1/projects/$PROJECT/databases/$DATABASE/point-in-time/restore"
```

It answers `201` with the new database and `Location: /v1/projects/$PROJECT/databases/orders-before-import`:

```json
{
  "id": "01JB7Q9X5R3T1V7W9Y3Z5A7C0E",
  "projectId": "01JB7PZ4M6N8P0Q2R4S6T8V0W2",
  "name": "orders-before-import",
  "branchedFrom": "01JB7Q2C8D4F6G0H2J4K6M8N0P",
  "createdAt": "2026-10-22T14:35:12+00:00"
}
```

The response also carries placement fields (`homeRegion`, `replicaRegions`). Set `name` to `null` to get the first free of `<source>-restore`, `<source>-restore-2`, and so on. Names start with a lowercase letter, then lowercase letters, digits, `_` or `-`, at most 63 characters.

| Response | Meaning |
| --- | --- |
| `400 invalid_request` | `pointInTime` missing, in the future, or before your plan's retention; or an invalid `name` |
| `409 backup_history_incomplete` | The time is outside the restore window or the history is not complete yet. Retry later. |
| `409 restore_point_unproven` | A transaction committed within the same second; the message names the nearest earlier time |
| `409 backup_unreadable` | A backup object failed its integrity check |
| `409 name_taken` | Another database already has that name |
| `429` / `503` | `tenant_busy`, `tenant_memory`, `storage_busy`, `capacity`, `unavailable`: retry with backoff |

Nothing is created on `409`.

## List and trigger backups

```bash
api "$TERRASCALE_API_URL/v1/projects/$PROJECT/databases/$DATABASE/backups?limit=20"
api -X POST "$TERRASCALE_API_URL/v1/projects/$PROJECT/databases/$DATABASE/backups"
```

The list returns `{"items":[…],"next":null}` in creation order. Pass `next` as `after` for the next page; `limit` is 1 to 100 (default 20). `POST` takes no body and answers `201` with one backup:

```json
{ "id": "01JB7Q3V0Z8K4M2N6P9R5T1W6A", "databaseId": "01JB7Q2C8D4F6G0H2J4K6M8N0P", "status": "ready",
  "trigger": "manual", "sizeBytes": 1048576, "createdAt": "2026-10-22T14:40:00+00:00" }
```

`trigger` is `manual` or `scheduled`. A backup is kept until the policy's retention expires or the database is deleted.

## Restore from a backup

```bash
api -X POST -H 'Content-Type: application/json' -d '{"name":null}' \
  "$TERRASCALE_API_URL/v1/projects/$PROJECT/databases/$DATABASE/backups/01JB7Q3V0Z8K4M2N6P9R5T1W6A/restore"
```

The body, `201` response, naming and `name_taken` behave as for a point-in-time restore. An unknown backup id answers `404`.

## Backup policy

```bash
api "$TERRASCALE_API_URL/v1/projects/$PROJECT/databases/$DATABASE/backup-policy"
api -X PUT -H 'Content-Type: application/json' \
  -d '{"fullBackupCron":"0 3 * * *","retentionDays":14,"disabled":false}' \
  "$TERRASCALE_API_URL/v1/projects/$PROJECT/databases/$DATABASE/backup-policy"
```

Both return `fullBackupCron`, `retentionDays`, `disabled` and `updatedAt`. Until you save a policy, the default is a full backup daily at 02:00 UTC, kept 7 days, with `updatedAt: null`. `fullBackupCron` is a standard 5-field cron expression in UTC; `null` or empty means no scheduled backups. `retentionDays` (1 to 35) applies to manual and scheduled backups. `disabled: true` pauses scheduled backups. Invalid values answer `400`.

## Your own backup bucket

A project can also ship its continuous backup to one S3-compatible bucket you own. This route is project level and a token narrowed to one database answers `404`.

```bash
api -X PUT -H 'Content-Type: application/json' -d @destination.json \
  "$TERRASCALE_API_URL/v1/projects/$PROJECT/backup-destination"
```

`destination.json` holds `endpoint` (public HTTPS), `region` (1 to 128 characters), `bucket` (3 to 63 lowercase letters, digits, dots or hyphens), `accessKeyId`, `secretKey` and `mode`. Keep the file private and delete it after the call. `mode: "both"` ships to TerraScale and your bucket; `mode: "customer"` ships this project only to your bucket. The key pair needs read, write, list and delete under `v1/<organization>/<project>/` and read, write and list under `v1/keys/<organization>/<project>/`. Saving probes the bucket first.

`GET` on the same path returns `endpoint`, `region`, `bucket`, `mode` and `updatedAt`, never the credentials. `DELETE` answers `204`, resumes TerraScale shipping and keeps every object in your bucket. Deleting a project answers `409` while a destination is set.

To read or restore from your bucket, add `?destination=customer` to `GET …/point-in-time`, or `"destination":"customer"` to the plan and restore bodies. The default is `platform`; there is no fallback between buckets.

</div>
