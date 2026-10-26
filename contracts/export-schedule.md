# Scheduled export contract

Producer: TerraScale/terrascale@1d823d8534fc5035859a47e263691d0c7cb7b744.
The three scheduled-export sources are unchanged from audit a3cbf8b1.

- `Features/Management/Backups/GetExportScheduleEndpoint.cs`: GET
  `/v1/projects/{project}/databases/{database}/export-schedule`, getExportSchedule,
  databases:read; 200 JSON schedule, 404/503 error envelopes.
- `SetExportScheduleEndpoint.cs`: PUT same path, setExportSchedule,
  databases:write plus organization writer; JSON `{ "cron": ... }`.
  Null/empty/whitespace stops; server trims and validates standard five-field UTC
  cron. Nonempty cron requires the customer's project backup destination,
  otherwise 409 conflict. Invalid cron is 400 invalid_request; 403/404/503 also
  propagate. No SDK mutation replay, pagination, checkpoint or idempotency added.
- `Features/Management/ProjectApiRoute.cs`: Bearer project access token, project
  equality and optional database restriction; database selector is its name.
  Another project/database answers 404. Scopes are never bypassed by the SDK.
- `Backups/ScheduledExports.cs`: camelCase cron/updatedAt/lastRun and
  startedAt/finishedAt/status/key/documents/bytes/error; timestamps, nullable
  values and signed 64-bit counts. Failed runs retain partial document counts,
  return zero bytes/no key and sanitized errors. Missing/default nullable fields
  and zero counters remain readable. No failure-error whitelist is invented.
- Fixed bucket failure: `the export could not be written to the customer bucket`;
  missing destination: `the project's backup destination is not set`.
  Disk capacity failures use the producer's `disk_capacity: ` prefix.
- `Hosting/TerraBaseJsonContext.cs` uses Web JSON defaults and registers schedule
  DTOs; `tests/TerraBase.Fast/Platform/ApiReferenceTests.cs` pins both operation
  identities/routes/scopes. This is source/route-reference evidence; generated
  OpenAPI and deployed parity have not been claimed.

Runs write live documents as importable NDJSON to
`terrabase-exports/{project}/{database}/{yyyyMMddTHHmmssZ}.ndjson`. They are
separate from snapshot backups. Failed attempts wait until the next occurrence.
Previously written customer objects remain after scheduling stops. The SDK adds
no scheduler, polling loop, configuration knobs or destination credential fields.

HTTP fixtures cover fields/null/defaults, exact int64 counts, sanitized failure
results, stopping, errors, cancellation and mutation nonreplay without server
binaries. Live own-bucket NDJSON proof remains pending an authorized customer-bucket fixture.
