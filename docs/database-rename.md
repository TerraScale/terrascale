# Database rename (unreleased 0.0.9)

Planned SDK/CLI patch: **v0.0.9**, after the producer API and release order are
approved. Go module versions come from Git tags; this PR does not publish a tag.

```go
renamed, err := client.RenameDatabase(ctx, projectID, databaseID, "orders_new")
```

```sh
ts -project my-project databases rename orders orders_new
# Existing CLI spelling also works:
ts -project my-project db rename orders orders_new
ts -project my-project --json databases rename 01ARZ3NDEKTSV4RRFFQ69G5FAW orders_new
```

The SDK takes an immutable database ULID. The CLI accepts a current name or ULID,
resolves a name once, then sends PATCH by ULID with only `{"name":"orders_new"}`.
The home region accepts the mutation; other regions return `421 wrong_region`.
Errors retain the server code, including `name_taken`, `unknown_database` and
`platform_placement_locked`. An uncertain write is not automatically replayed.

The ID, data, placement, backups and keys stay with the database. Update
name-based connection strings and handles; after convergence the old name has
no alias and can be reused. Concurrent cross-region claims converge with the
smallest ULID keeping the name. The other database keeps its data under a conflict
name. `ManagedDatabase.PreviousName` and `ConflictOf` expose that conflict; a
successful rename clears both. CLI JSON metadata includes these fields.

A database-scoped key always takes a canonical uppercase database ULID, never a
name. Find it with `ts db list`, then use `ts keys create ci <database-ULID>`.
`tsauth.APIKeyRequest.Database` enforces the same rule. Omitting it creates a
project-wide key; key listings retain the immutable ULID in `database` so a
rename cannot move the credential to a different database. Legacy
`ProjectAPIKeyRequest.DatabaseID` also requires a ULID.
