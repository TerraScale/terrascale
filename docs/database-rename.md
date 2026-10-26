# Rename a database

> **Availability hold:** this SDK operation is prepared for the pending API rename contract.
> Rename remains unavailable: the producer currently returns 400 for name changes.
> TerraScale/terrascale#698 is being rescoped to additive ULID addressing and name/ID claims; it does not enable rename.
> Keep merge, release and activation held until TS-1120 merges, the owning rename API is available, and required CI passes.
> Local HTTP fixtures prove SDK transport and decoding only; they do not prove live rename, namespace exclusivity, data preservation or cross-region completion.

Use the project client with a ts-auth access token authorized for `databases:write`.
Address the rename with the database's immutable ULID:

```csharp
TerraBaseManagedDatabase renamed = await documents.Project(projectId)
    .RenameDatabaseAsync(database.Id, "production", cancellationToken);
```

The SDK sends one `PATCH /v1/projects/{project}/databases/{database}` request with
`Content-Type: application/json` and body `{"name":"production"}`. The project is
its ULID; the database must be its immutable ULID. The response is
the updated `TerraBaseManagedDatabase`, including its unchanged ID and placement.
Send the request to the database's home region; a different region answers 421
`wrong_region` through `TerraBaseWrongRegionException`.

Names must pass the server's validation and be unique within the project. Invalid
names return 400 `invalid_request` (`TerraBaseValidationException`); duplicates return
409 `name_taken` (`TerraBaseConflictException`). The pending API contract preserves data and identity.
The old name returns 404 after rename. Update name-based connection strings and
create a fresh database handle using the returned name or immutable ULID:

```csharp
var databaseAfterRename = documents.Database(projectId, renamed.Id);
```

Previously created handles retain their original path. The SDK does not retry
rename requests or edit stored credentials. Follow the owning API's credential
scope rules; a renamed database does not grant access to a different database.
