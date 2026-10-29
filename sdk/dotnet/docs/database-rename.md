# Rename a database

Use `await documents.Project(projectId).RenameDatabaseAsync(database.Id, "production", cancellationToken)`
with a `databases:write` token. The immutable database ULID targets a name-only JSON
`PATCH /v1/projects/{project}/databases/{database}` at the home region.

The response preserves ID, data, placement, backups and keys. Update name-based
connection strings; after convergence the old name stops resolving unless another
database claims it. There is no alias. Existing handles keep their original route.
A database-restricted API key or token must carry the database ULID, never its name.

Invalid names return 400 `invalid_request`, observed duplicates 409 `name_taken`,
platform databases 409 `platform_placement_locked`, unknown databases 404
`unknown_database`, and non-home regions 421 `wrong_region` with
`X-TerraBase-Database-Home`. The SDK surfaces typed exceptions. Only documented
pre-execution 503 refusals may be resent; reconcile uncertain outcomes with a GET
by immutable ID before retrying.

Concurrent cross-region claims may both succeed. After convergence the smallest
ULID keeps the name; the other database retains its identity and data under a
conflict name. Nullable `PreviousName` and `ConflictOf` report the contested name
and winning database ULID. A successful rename clears this metadata.

This implements the producer contract in the `fix/ts-1050` branch, not evidence of
live availability. Merge, release and activation remain held pending the producer.
