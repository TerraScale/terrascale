---
title: Use Dapper and ADO.NET with .NET
description: Run Dapper and ADO.NET against your TerraScale database through the .NET SDK's DbConnection provider.
---

<!-- i18n:fallback:fr -->
> **Traduction en attente.** Cette page n’a pas encore été relue en français et affiche le contenu original en anglais.

<div lang="en" dir="ltr">

# Use Dapper and ADO.NET with .NET

The .NET SDK ships an ADO.NET provider: `TerraBaseDbConnection` wraps a `TerraBaseSqlDatabase` in a `DbConnection`, so every .NET tool built on ADO.NET works against your TerraScale database. Dapper accepts that connection directly. The SQL dialect is SQLite; everything the SQL API refuses (see [SQL](/fr/concepts/sql)) is refused through the provider too.

## Install

The .NET SDK is not on NuGet yet; follow the [C# quickstart](/fr/getting-started/csharp) to pack it into a local package folder, then add Dapper to your project:

```bash
dotnet add package TerraScale.DotNet --source ~/.nuget/packages-terrascale
dotnet add package Dapper
```

## Connect

Export your project ID and API key as in the [C# quickstart](/fr/getting-started/csharp), then build the client and wrap the database handle:

```csharp
using TerraScale.DotNet;
using TerraScale.DotNet.TerraBase;

using var http = new HttpClient(TerraBaseHttp.CreateHandler());
var sql = new TerraBaseSqlClient(http, new TerraBaseSqlClientOptions
{
    ApiKey = Environment.GetEnvironmentVariable("TERRASCALE_API_KEY"),
    Region = Environment.GetEnvironmentVariable("TERRASCALE_REGION"),
});
TerraBaseSqlDatabase db = sql.Database(Environment.GetEnvironmentVariable("TERRASCALE_PROJECT_ID")!, "main");
TerraScaleIdGenerator ids = new();

await using var connection = new TerraBaseDbConnection(db);
await connection.OpenAsync();
```

The connection has no connection string: it is built from the database handle, so the key never appears in a string. Outside a transaction, each command is its own request and commits on its own.

## Dapper

Dapper's `QueryAsync` maps rows to your types and `ExecuteAsync` returns the rows a write changed. Parameters are named (`@name`, `:name` or `$name`) or positional (`?`), and values are sent as typed parameters, never interpolated text:

```csharp
using Dapper;

public sealed record Person(string Id, string Name);

var people = await connection.QueryAsync<Person>(
    "SELECT id, name FROM people WHERE name = @name", new { name = "Ada" });

var inserted = await connection.ExecuteAsync(
    "INSERT INTO people (id, name) VALUES (@id, @name)", new { id = ids.NewId(), name = "Ada" });
```

Blobs bind to `byte[]` parameters, `DateTime` values travel as UTC text, and integers arrive as exact 64-bit numbers.

## Plain ADO.NET

Without Dapper, commands and readers are the standard ADO.NET types:

```csharp
await using var command = connection.CreateCommand();
command.CommandText = "SELECT count(*) AS n FROM people";
await using var reader = await command.ExecuteReaderAsync();
reader.Read();
long count = reader.GetInt64(0);
```

## Transactions and limits

`BeginTransactionAsync` opens a transaction over the SDK's SQL WebSocket session; commit and rollback close it, and every command in between runs in that one server-side transaction. Pass the transaction to Dapper with `transaction:`:

```csharp
await using (var tx = await connection.BeginTransactionAsync())
{
    await connection.ExecuteAsync(
        "INSERT INTO people (id, name) VALUES (@id, @name)",
        new { id = ids.NewId(), name = "Grace" }, transaction: tx);
    await connection.ExecuteAsync(
        "INSERT INTO people (id, name) VALUES (@id, @name)",
        new { id = ids.NewId(), name = "Linus" }, transaction: tx);
    await tx.CommitAsync();
}
```

Dispose without a commit rolls the transaction back, and so does closing or disposing the connection asynchronously; a sync `Dispose` detaches the transaction instead, and the server rolls it back at the limit below. A server-side transaction lives at most 5 seconds (see [SQL](/fr/concepts/sql)); hold it only for the statements that must commit together. Only `Serializable` isolation exists, matching the strongest isolation the database provides.

While a transaction is open, every command on that connection must enlist in it: set `command.Transaction` (Dapper does this from its `transaction:` argument) or the command throws.

## Async only

The provider is async-only, like the rest of the SDK: the sync `ExecuteNonQuery`, `ExecuteScalar`, `ExecuteReader`, `BeginTransaction`, `Commit` and `Rollback` throw `NotSupportedException`. Use the async overloads and pass a `CancellationToken`.

## What isn't supported

- **EF Core** is not supported yet: it needs schema and savepoint facilities the SQL API does not expose. Dapper, hand-written ADO.NET and the SDK's own `TerraBaseSqlClient` cover queries, writes and transactions today.
- `BEGIN`/`COMMIT`/`ROLLBACK` statements are refused; use the transaction API above.
- Server-side cursors and prepared-statement handles do not exist; commands send their SQL text each time.

</div>
