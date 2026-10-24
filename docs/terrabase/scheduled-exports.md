# Scheduled exports to the customer's bucket

A database can be exported on a schedule to its project's own S3-compatible bucket, the
[backup destination](backup.md) (`PUT /v1/projects/{project}/backup-destination`). Its credentials are write-only and
held only sealed with the TerraBase master key; exports reuse them and never return them.

- `PUT /v1/projects/{project}/databases/{database}/export-schedule` with `{"cron":"0 3 * * *"}` (standard 5-field cron,
  UTC) saves the schedule; `null` or `""` stops it. Scope `databases:write`, a writer of the organization. Without a
  backup destination the answer is `409 conflict`; an invalid cron is `400 invalid_request`.
- `GET …/export-schedule` (scope `databases:read`) returns `cron`, `updatedAt` and `lastRun`: `startedAt`, `finishedAt`,
  `status` (`succeeded` or `failed`), `key`, `documents`, `bytes` and `error`.
- Each run writes every live document from one read snapshot, in the [export](export.md) format, to
  `terrabase-exports/{project}/{database}/{yyyyMMddTHHmmssZ}.ndjson`. [NDJSON import](import-ndjson.md) reads that
  object back into a new database.
- A pass runs once a minute. The member that ships a database's backups exports it, so a run happens once across
  regions. A run is due at the first cron occurrence after the later of the save and the last attempt; a failed attempt
  waits for the next occurrence. Bucket errors are recorded as a fixed message, never with bucket responses or credentials.
- The schedule lives in the catalog collection `export_schedules`; a deleted database's entry is dropped by the next pass.
  Objects already written stay in the customer's bucket; TerraBase never deletes them.
