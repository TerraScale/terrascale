`Database::get_export_schedule()` reads the saved UTC cron and last attempt.
`set_export_schedule(Some("0 3 * * *"))` saves a five-field UTC schedule;
`set_export_schedule(None)` or an empty expression stops scheduled exports.
The server validates cron and requires the project's backup destination to save
an active schedule. Each run writes live documents as NDJSON to that destination.
Counters accept exact int64 JSON integers or canonical decimal strings. A refused
save retains its HTTP error; uncertain writes are not automatically replayed.
