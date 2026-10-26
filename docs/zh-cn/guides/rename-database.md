---
---
<!-- i18n:fallback:zh-cn -->
> **翻译待审。** 此页面尚无经人工审校的中文译文，暂显示英文原文。

<div lang="en" dir="ltr">

# Rename a database

This guide describes the upcoming database rename release. Renaming preserves the database ULID, collections, documents, files, backups, keys and placement. Choose a valid name within its project; reserved device names are invalid.

Names are unique per project after convergence; concurrent cross-region claims resolve to one winner. A same-region duplicate returns `409`; conflicting requests accepted in different regions may both succeed. During convergence, the smallest database ULID keeps the target name. The loser keeps its ULID and data under a deterministic conflict name such as `<name>-conflict-<ulidtail>`, with a visible conflict event. Reconciliation brings name indexes into agreement with current labels across regions; every database identity, document, file, backup and key survives. Renames of the same database are serialized in its home region.

In the dashboard, open your project, select the database and open **Settings**. Enter **New database name**, read the connection-string warning, type the current database name exactly into **Type … to confirm**, then select **Rename database**. After success, open **View connection details** and update applications that use the name. If the result is uncertain, reload Settings and check the current name before retrying.

After rename converges, the previous name returns `404` unless it is claimed again; there is no alias or redirect. Accepted cross-region conflicts can change the final label, so reconcile metadata by ULID before updating name-based clients. Update name-based connection strings, saved configuration and database handles. Strings and handles using the immutable database ULID remain stable. Existing keys stay restricted to the same database identity.

Use an authorized project access token with `databases:write`. Address the project and database by immutable ULIDs:

```http
PATCH /v1/projects/{projectId}/databases/{databaseId}
Authorization: Bearer <project-access-token>
Content-Type: application/json

{"name":"orders_new"}
```

An accepted rename returns `200` with the existing managed database metadata and the locally accepted name, which may later reconcile after a cross-region conflict. Invalid names return `400`; an already observed project-duplicate name returns `409`. A `200` response does not establish exclusive ownership of the name across regions. SDKs may reject malformed input locally. Authentication and project/database restrictions still apply.

SDK calls: Go `client.RenameDatabase(ctx, projectId, databaseId, "orders_new")`; JavaScript `client.databases.rename(databaseId, "orders_new")`; Rust `client.rename_database(database_id, "orders_new").await`; Python `client.rename_database(database_id, "orders_new")` (await with `AsyncTerraScale`). Each returns its existing database metadata type.

.NET exposes `Task<TerraBaseManagedDatabase> TerraBaseProjectClient.RenameDatabaseAsync(string databaseId, string name, CancellationToken cancellationToken = default)`: call `await project.RenameDatabaseAsync(databaseId, "orders_new", cancellationToken)` on the client bound to your project ULID.

The shipped Go `ts` CLI gains `db rename` in this upcoming release. Use its existing [sign-in and project selection](/zh-cn/cli#sign-in):

```sh
ts auth login
ts -project <projectULID> db rename <databaseULID> orders_new
```

With one accessible project, omit `-project`; otherwise pass a project name or ID before `db rename`. The database operand accepts a canonical uppercase ULID or a resolvable current name. A current name is resolved once through database metadata, then the CLI sends PATCH to the returned immutable ID with only the new name in the body. Name resolution also requires `databases:read`. The CLI prints the new name and unchanged ID on success.

For automation, use the CLI's existing `TERRASCALE_API_KEY` authentication described in [CLI sign-in](/zh-cn/cli#sign-in). It exchanges that key for a scoped project token; organization keys require `-project <projectULID>`. Do not pass credentials as command arguments. Rename does not read an API key from stdin.

Do not automatically retry validation or duplicate-name conflicts. After a lost response or uncertain mutation, GET metadata by immutable ID before explicitly retrying: Go `client.Database(projectId, databaseId).Info(ctx)`, JavaScript `client.databases.get(databaseId)`, Python `client.database(database_id).info()` (await with the async client), Rust `client.database(database_id).get_database().await`, or .NET `await project.GetDatabaseAsync(databaseId)`. Existing-resource selectors accept a current name or canonical ULID; new names must pass name validation.

</div>
