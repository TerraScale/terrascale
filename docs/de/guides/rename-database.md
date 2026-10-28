---
---
<!-- i18n:fallback:de -->
> **Übersetzung ausstehend.** Diese Seite wurde noch nicht auf Deutsch geprüft und zeigt den englischen Originalinhalt.

<div lang="en" dir="ltr">

# Rename a database

This guide describes the upcoming database rename release. Renaming preserves the database ULID, collections, documents, files, backups, keys and placement. Choose a valid name within its project; reserved device names are invalid.

Names are unique per project after convergence; concurrent cross-region claims resolve to one winner. A same-region duplicate returns `409`; conflicting requests accepted in different regions may both succeed. During convergence, the smallest database ULID keeps the target name. The loser keeps its ULID and data under a deterministic conflict name such as `<name>-conflict-<ulidtail>`, with `previousName` set to the contested name, `conflictOf` set to the winner’s ULID, and a visible conflict event. The losing database’s earlier name is not restored; rename it again to choose another name. Reconciliation brings name indexes into agreement with current labels across regions; every database identity, document, file, backup and key survives. Renames of the same database are serialized in its home region.

In the dashboard, open your project, select the database and open **Settings**. Enter **New database name**, read the connection-string warning, type the current database name exactly into **Type … to confirm**, then select **Rename database**. After success, open **View connection details** and update applications that use the name. If the result is uncertain, reload Settings and check the current name before retrying.

After rename converges, the previous name returns `404` unless it is claimed again; there is no alias or redirect. Accepted cross-region conflicts can change the final label, so reconcile metadata by ULID before updating name-based clients. Update name-based connection strings, saved configuration and database handles. Strings and handles using the immutable database ULID remain stable. API keys narrowed to a database reference its immutable ULID, so they survive renames and never gain access to another database that later claims the old name. When creating a narrowed key, send the database ULID in `database`, not its display name; name-based restrictions are rejected. When displaying keys, map that ULID to the name from the project’s database list.

Use an authorized project access token with `databases:write`. Send the request to the database’s home region. The database selector accepts its immutable ULID or current name; prefer ULIDs for stable addressing:

```http
PATCH /v1/projects/{projectId}/databases/{databaseId}
Authorization: Bearer <project-access-token>
Content-Type: application/json

{"name":"orders_new"}
```

An accepted rename returns `200` with the existing managed database metadata and the locally accepted name, which may later reconcile after a cross-region conflict. Send `name` alone: combining it with `homeRegion`, `replicaRegions` or `allRegions`, or using an invalid name, returns `400 invalid_request`. An already observed project-duplicate name returns `409 name_taken`. A non-home region returns `421 wrong_region` with `X-TerraBase-Database-Home`; use that home region. An unknown database returns `404 unknown_database`. Platform databases cannot be renamed (`409 platform_placement_locked`). A `200` response does not establish exclusive ownership of the name across regions. SDKs may reject malformed input locally. Authentication and project/database restrictions still apply.

SDK calls: Go `client.RenameDatabase(ctx, projectId, databaseId, "orders_new")`; JavaScript `client.databases.rename(databaseId, "orders_new")`; Rust `client.rename_database(database_id, "orders_new").await`; Python `client.rename_database(database_id, "orders_new")` (await with `AsyncTerraScale`). Each returns its existing database metadata type.

.NET exposes `Task<TerraBaseManagedDatabase> TerraBaseProjectClient.RenameDatabaseAsync(string databaseId, string name, CancellationToken cancellationToken = default)`: call `await project.RenameDatabaseAsync(databaseId, "orders_new", cancellationToken)` on the client bound to your project ULID.

The Go `ts` CLI gains `databases rename <database> <new-name>` in this upcoming release. Use its existing [sign-in and project selection](/de/cli#sign-in):

```sh
ts auth login
ts -project <projectULID> databases rename <databaseULID> orders_new
```

With one accessible project, omit `-project`; otherwise pass a project name or ID before `databases rename`. The database operand accepts a canonical uppercase ULID or a resolvable current name. A current name is resolved once through database metadata, then the CLI sends PATCH to the returned immutable ID with only the new name in the body. Name resolution also requires `databases:read`. The CLI prints the new name and unchanged ID on success.

For automation, use the CLI's existing `TERRASCALE_API_KEY` authentication described in [CLI sign-in](/de/cli#sign-in). It exchanges that key for a scoped project token; organization keys require `-project <projectULID>`. Do not pass credentials as command arguments. Rename does not read an API key from stdin.

Do not automatically retry validation or duplicate-name conflicts. After a lost response or uncertain mutation, GET metadata by immutable ID before explicitly retrying: Go `client.Database(projectId, databaseId).Info(ctx)`, JavaScript `client.databases.get(databaseId)`, Python `client.database(database_id).info()` (await with the async client), Rust `client.database(database_id).get_database().await`, or .NET `await project.GetDatabaseAsync(databaseId)`. Existing-resource selectors accept a current name or canonical ULID; new names must pass name validation.

</div>
