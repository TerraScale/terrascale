# .NET / C#

`TerraScale.DotNet` supports .NET 10/11; source builds use a .NET 11 toolchain.
It is a preview and NuGet publication is on hold. Use source or a local package.
Sources: [C# quickstart](https://docs.terrascale.tech/getting-started/csharp),
[SDK reference](https://docs.terrascale.tech/guides/sdks).

## Install and configure

From an immutable TerraScale monorepo checkout, build the package into a local
folder. Record that revision alongside the application dependency:

```sh
dotnet pack /absolute/path/to/terrascale/sdk/dotnet/TerraScale.DotNet.slnx --configuration Release -o /absolute/path/to/local-packages
dotnet new console -o terrascale-quickstart
cd terrascale-quickstart
dotnet add package TerraScale.DotNet --source /absolute/path/to/local-packages
```

Use an isolated `NUGET_PACKAGES` location for local preview packages so an older
pack with the same version is not reused. Restore sources may also need nuget.org
for transitive dependencies; retain the application's normal configured sources.
Source install follows the [C# quickstart](https://docs.terrascale.tech/getting-started/csharp).

`TerraBaseSqlClient.FromEnvironment()` reads `TERRASCALE_API_KEY`, optional
`TERRASCALE_BASE_URL` and `TERRASCALE_REGION`; project is supplied to
`Database(project, name)`. For explicit construction use a caller-owned
`HttpClient(TerraBaseHttp.CreateHandler())` so redirects are disabled.
`TerraBaseSqlClientOptions` has `ApiKey` or `AccessTokenProvider`, `BaseAddress`
or `EndpointResolver`, `Region`, and `AuthAddress`. With an API key and a custom
API origin/resolver, supply `AuthAddress`; it is an origin, not an OAuth path.
Sources: [C#](https://docs.terrascale.tech/getting-started/csharp),
[authentication](https://docs.terrascale.tech/guides/authentication).

## Complete small example

Replace `Program.cs` and run `dotnet run` with the three environment variables
from the skill. All SQL calls receive cancellation; the caller owns HTTP disposal.

```csharp
using TerraScale.DotNet;
using TerraScale.DotNet.TerraBase;

using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
var ct = deadline.Token;
using var http = new HttpClient(TerraBaseHttp.CreateHandler());
var client = new TerraBaseSqlClient(http, new TerraBaseSqlClientOptions
{
    ApiKey = Required("TERRASCALE_API_KEY"),
});
var db = client.Database(Required("TERRASCALE_PROJECT_ID"), Required("TERRASCALE_DATABASE_NAME"));
var ids = new TerraScaleIdGenerator();
var id = ids.NewId();
await db.ExecuteAsync("CREATE TABLE IF NOT EXISTS sdk_people (id TEXT PRIMARY KEY, name TEXT NOT NULL)", cancellationToken: ct);
await db.BatchAsync([
    new("INSERT INTO sdk_people (id, name) VALUES (?, ?)", [id, "Ada"]),
    new("INSERT INTO sdk_people (id, name) VALUES (:id, :name)",
        Named: new Dictionary<string, TerraBaseSqlValue> { ["id"] = ids.NewId(), ["name"] = "Grace" }),
], ct);
var rows = await db.QueryAsync("SELECT id, name FROM sdk_people WHERE id = ?", [id], ct);
Console.WriteLine(rows[0].GetString("name"));

await using var session = await db.ConnectAsync(ct);
await session.TransactionAsync(async (tx, token) =>
{
    var people = await tx.QueryAsync("SELECT name FROM sdk_people WHERE id = ?", [id], token);
    if (people[0].GetString("name") == "Ada")
    {
        await tx.ExecuteAsync("UPDATE sdk_people SET name = ? WHERE id = ?", ["Ada Lovelace", id], token);
    }
}, ct);

try
{
    await db.ExecuteAsync("INSERT INTO sdk_people (id, name) VALUES (?, ?)", [id, "duplicate"], ct);
}
catch (TerraBaseSqlException error) when (error.Code == TerraBaseSqlErrorCodes.PrimaryKeyViolation)
{
    Console.WriteLine($"{error.Code} {error.Statement} {error.Sqlstate}");
}

static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
    ? value : throw new InvalidOperationException($"Missing {name}");
```

Expected: `Ada`, then `primary_key_violation 0 23505`. Sources:
[C#](https://docs.terrascale.tech/getting-started/csharp),
[SQL HTTP](https://docs.terrascale.tech/guides/sql-http).

## Typed rows, cancellation and errors

`ExecuteAsync` returns `TerraBaseSqlStatementResult`; `BatchAsync` returns one
per statement. Untyped `QueryAsync` returns `IReadOnlyList<TerraBaseSqlRecord>`;
read columns with `GetInt64`, `GetString`, `GetBytes` and `IsNull`.
For typed rows use `QueryAsync(sql, args, JsonTypeInfo<T>, ct)` with a
source-generated `JsonSerializerContext`, not an invented reflection mapper:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

public sealed record Person(string Id, string Name);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(Person))]
public sealed partial class AppJson : JsonSerializerContext;
```

Place these declarations after top-level statements and usings at the file's top.
Then call `db.QueryAsync("SELECT id, name FROM sdk_people", args: null,
typeInfo: AppJson.Default.Person, cancellationToken: ct)`. Named `args: null`
selects the positional overload. Values use `TerraBaseSqlValue`, with blobs
from `TerraBaseSqlValue.FromBytes`.

Use a `CancellationToken` on every async SQL operation; it limits caller waiting
and does not certify rollback. `BeginAsync`, `CommitAsync`, `RollbackAsync`
and `TransactionAsync` are session methods. `await using` closes a session.
`TerraBaseSqlException` carries `Code`, `Statement`, `Sqlstate`, `StatusCode`
and `IsTransient`; session errors have no HTTP status. Non-SQL HTTP refusals use
`TerraBaseException` subclasses, including `TerraBaseWrongRegionException`.
On `write_outcome_unknown`, later session calls keep throwing the same error;
read from a new connection before deciding whether to repeat a write.
Sources: [C#](https://docs.terrascale.tech/getting-started/csharp),
[errors](https://docs.terrascale.tech/guides/errors).
Source audit: `sdk/dotnet/README.md`, `src/TerraScale.DotNet/TerraBase/Sql/`
(`TerraBaseSqlClient.cs`, `TerraBaseSqlSession.cs`, `TerraBaseSqlResults.cs`,
`TerraBaseSqlException.cs`, `TerraBaseSqlValue.cs`) and `TerraBaseHttp.cs` at
the revision in [the skill](../SKILL.md).
