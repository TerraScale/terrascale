---
title: C# quickstart
description: Create tables, insert rows and query typed rows with the .NET SDK.
---

<!-- i18n:fallback:de -->
> **Übersetzung ausstehend.** Diese Seite wurde noch nicht auf Deutsch geprüft und zeigt den englischen Originalinhalt.

<div lang="en" dir="ltr">

# C# quickstart

Run a complete SQL workflow against your TerraScale database in about ten minutes: create a table, insert rows with parameters, read typed rows back, run a multi-statement batch and handle a refused statement.

## Prerequisites

- The .NET SDK, .NET 10 or .NET 11
- A TerraScale account, a project with a database, and an API key. Follow steps 1–4 of [Getting started](/de/getting-started/): the key needs **Read and write** access to that database.
- The **Current project** ID (a ULID) from the dashboard's **Organization** page, and the API key. Export both:

```bash
read -rs TERRASCALE_API_KEY && export TERRASCALE_API_KEY   # keeps the key out of history
export TERRASCALE_PROJECT_ID=YOUR_PROJECT_ULID
export TERRASCALE_DATABASE_NAME=main                       # your database's name
```

## Install the SDK

The .NET SDK is not on NuGet yet. Build a package once from the monorepo and add it to a local package folder:

```bash
git clone --depth 1 https://github.com/TerraScale/terrascale.git
cd terrascale/sdk/dotnet
dotnet pack TerraScale.DotNet.slnx --configuration Release -o ~/.nuget/packages-terrascale
```

Then create the quickstart project and reference the package (the version is whatever `dotnet pack` just wrote to the folder):

```bash
mkdir -p quickstart && cd quickstart
dotnet new console
dotnet add package TerraScale.DotNet --source ~/.nuget/packages-terrascale
```

## Run the quickstart

Replace `Program.cs` with this. `TerraBaseSqlClient` exchanges the dashboard key for short-lived project access tokens itself; every operation goes through the SQL client.

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using TerraScale.DotNet;
using TerraScale.DotNet.TerraBase;

public static class Program
{
    public static async Task<int> Main()
    {
        string project = Required("TERRASCALE_PROJECT_ID");
        string apiKey = Required("TERRASCALE_API_KEY");
        string databaseName = Required("TERRASCALE_DATABASE_NAME");

        using var http = new HttpClient(TerraBaseHttp.CreateHandler());
        var sql = new TerraBaseSqlClient(http, new TerraBaseSqlClientOptions { ApiKey = apiKey });
        TerraBaseSqlDatabase db = sql.Database(project, databaseName);
        var ids = new TerraScaleIdGenerator();
        string ada = ids.NewId();
        string grace = ids.NewId();

        // One statement, one transaction. IF NOT EXISTS makes the quickstart safe to run twice.
        await db.ExecuteAsync("CREATE TABLE IF NOT EXISTS people (id TEXT PRIMARY KEY, name TEXT NOT NULL)");

        // Insert with parameters; NewId() mints a ULID primary key.
        await db.ExecuteAsync("INSERT INTO people (id, name) VALUES (?, ?)", [ada, "Ada"]);

        // Select into typed rows: QueryAsync<T> maps each row through source-generated JSON metadata.
        IReadOnlyList<Person> people = await db.QueryAsync(
            "SELECT id, name FROM people WHERE id = ?", [ada], AppJson.Default.Person);
        Console.WriteLine($"query matched {people[0].Id} {people[0].Name}");

        // A batch of 1 to 256 statements runs as one transaction: all commit or none do.
        var batch = await db.BatchAsync([
            new("INSERT INTO people (id, name) VALUES (:id, :name)",
                Named: new Dictionary<string, TerraBaseSqlValue> { ["id"] = grace, ["name"] = "Grace" }),
            new("SELECT count(*) AS n FROM people"),
        ]);
        Console.WriteLine($"batch inserted, people count: {batch[1].Rows[0].GetInt64("n")}");

        // A refused statement throws TerraBaseSqlException; the whole request rolled back.
        try
        {
            await db.ExecuteAsync("INSERT INTO people (id, name) VALUES (?, 'dup')", [ada]);
        }
        catch (TerraBaseSqlException refused)
        {
            Console.WriteLine($"refused: {refused.Code} statement {refused.Statement} sqlstate {refused.Sqlstate}");
        }
        return 0;
    }

    private static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value : throw new InvalidOperationException($"Missing {name}.");
}

public sealed record Person(string Id, string Name);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(Person))]
public sealed partial class AppJson : JsonSerializerContext;
```

Run it with the key exported in your shell:

```bash
dotnet run
```

Expected output (ids vary):

```text
query matched 01JDV7QZGK0DMB3W3QV2AB5CDM Ada
batch inserted, people count: 2
refused: unique_violation statement 0 sqlstate 23505
```

## How it works

- **The key never reaches TerraBase.** `TerraBaseSqlClient` exchanges it at ts-auth (`client_credentials`) for a short-lived access token to the project, caches it and renews it before it expires; concurrent calls share one exchange. Keep the key on your server; see [authentication](/de/guides/authentication).
- **Every call is one request and one transaction.** `ExecuteAsync`, `QueryAsync` and `BatchAsync` each post to `/sql/execute`; a batch commits all of its statements or none, and `BEGIN`/`COMMIT`/`ROLLBACK` are refused. See [SQL over HTTP](/de/guides/sql-http).
- **Arguments are parameters, not string interpolation.** Positional (`?`) and named values are sent as typed JSON; blobs (`TerraBaseSqlValue.FromBytes`) travel as base64. `TerraBaseSqlRecord` reads by ordinal or column name, and `QueryAsync<T>` maps rows through source-generated JSON metadata.
- **Refused statements throw `TerraBaseSqlException`.** It carries `Code` (one of `TerraBaseSqlErrorCodes`), `Statement` (zero-based) and `Sqlstate`; the request rolled back. A `serialization_conflict` is resent at most twice; nothing else is.

## Next steps

- [SDK reference](/de/guides/sdks) — the five SDKs and their install commands
- [JavaScript](/de/getting-started/javascript), [Go](/de/getting-started/go) and [Python](/de/getting-started/python) quickstarts
- [SQL over HTTP](/de/guides/sql-http) for the raw HTTP interface and [concepts](/de/concepts/sql) for the SQL dialect

</div>
