# Deletion conditioned on database identity

`DELETE /v1/projects/{project}/databases/{database}/if-id/{expectedId}` deletes
only the immutable database ID the caller inspected. The caller supplies a
canonical uppercase ULID as `expectedId`. Authentication, `databases:write`,
project restrictions, database restrictions, placement and deletion authority
remain enforced.

Project catalog middleware resolves the database name once and replaces the
route's database value with that ID. The endpoint compares `expectedId` with
this resolved value before placement or deletion. It never looks up the name
again. Malformed IDs return `400 invalid_request`; a different ID returns
`409 database_identity_mismatch`. Neither error echoes the supplied ID.

A matching request runs the existing deletion service using the resolved ID.
If the old record disappears after resolution, the request can fail with 404;
it cannot delete a later same-name replacement. Successful deletion remains
204, with the existing logical deletion and durable replica cleanup behavior.
Replicated and `allRegions` databases use permanent purge authority directly;
no placement PATCH or de-replication step is needed or performed.

Older servers return 404 for the new route. Clients must fail closed and never
fall back to unconditional deletion. The existing database DELETE is unchanged.
This prerequisite does not provide atomic branch reset, name swap, provenance
validation or recovery after delete-before-create failure.
