# Database rename

Rename sends only `{"name":"production"}` to
`PATCH /v1/projects/{project}/databases/{database}` with `databases:write`.
The selector accepts an immutable ULID or the current name; prefer the ULID so
handles survive renames. Success returns 200 with database metadata. Never combine
name and placement changes in one request.

The database keeps its ULID, data, placement, backups and keys. Update name-based
connection strings and handles. After convergence, the old name has no alias: it
returns 404 or resolves to a database that later claims it.

Invalid names or mixed placement fields return 400 `invalid_request`; a known
same-region duplicate returns 409 `name_taken`; a platform database returns 409
`platform_placement_locked`; an unknown database returns 404 `unknown_database`.
Only the home region accepts rename; other regions return 421 `wrong_region`
with `X-TerraBase-Database-Home`. Reconcile uncertain mutations by ULID before an
explicit retry. The SDK does not automatically replay uncertain renames.

Concurrent cross-region claims can both succeed before convergence. The smallest
ULID keeps the name; the loser keeps its data under a conflict name. Database
metadata includes nullable `previousName` (the contested name) and `conflictOf`
(the winner's ULID). A successful rename clears both fields.

Database restrictions on keys/tokens use the immutable database ULID, never its
name. Use the project's database list to map an ID to its display name.

The producer implementation is on `terrascale` branch `fix/ts-1050` and is not
merged yet. Merge, release and activation remain held pending producer availability
and green CI. HTTP fixtures verify SDK transport and decoding only; live rename
and cross-region convergence require producer rollout evidence.
