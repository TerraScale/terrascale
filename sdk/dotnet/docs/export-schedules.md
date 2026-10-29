# Customer-bucket scheduled exports

Use `database.GetExportScheduleAsync(cancellationToken)` with `databases:read` project authority.
`database.SetExportScheduleAsync("0 3 * * *", cancellationToken)` requires `databases:write` and a
configured project backup destination. The producer validates and normalizes standard five-field UTC
cron expressions. It returns 400 for invalid cron and 409 when a nonempty schedule lacks a destination.
The SDK sends each request once and starts no background polling or export jobs.

Read `LastRun` for attempt times, `Status`, `Key`, `Documents`, `Bytes` and `Error`. Before the first
attempt it is null; an unsaved schedule also has null `Cron` and `UpdatedAt`. Failures retain the
producer's sanitized error. Setting cron to null, empty or whitespace removes the schedule and retains
the previous attempt. These NDJSON exports contain live document collection/id/body lines; snapshot
backups and their restore format are separate APIs.

Qualification requires an authorized own destination: create known documents, save a due schedule,
observe a successful LastRun, read its exact object key, compare every NDJSON collection/id/body/count,
then remove the schedule and own object/data. HTTP fixture tests establish SDK contract behavior;
production export and bucket cleanup remain pending. Install from source/local packs; NuGet publication
remains on hold.
