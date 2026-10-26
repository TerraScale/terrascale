`Database::export_database::<Box<RawValue>>(None)` reads live documents in
collection/ID order from one snapshot, retaining exact document JSON. This is a
bounded complete-response reader, not a streaming API: the existing client
response-size limit applies. Empty exports succeed; malformed or incomplete
lines and interrupted HTTP bodies fail without returning partial documents.
Pass `Some((collection, id))` to resume after a previously received line; a resume
uses a new snapshot. This bounded read retains the existing GET retry policy.
`documents:read` is required; project/database token restrictions remain in force.
