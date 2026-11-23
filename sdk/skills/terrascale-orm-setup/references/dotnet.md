# .NET: Dapper and ADO.NET

Sources: [.NET ORM guide](https://docs.terrascale.tech/guides/orm-dotnet),
[C# quickstart](https://docs.terrascale.tech/getting-started/csharp).

## 1. Install the preview

The SDK is not on NuGet. From a TerraScale checkout:

~~~sh
cd sdk/dotnet
dotnet pack TerraScale.DotNet.slnx --configuration Release -o ~/.nuget/packages-terrascale
~~~

In the application project:

~~~sh
dotnet add package TerraScale.DotNet --source ~/.nuget/packages-terrascale
dotnet add package Dapper
~~~

Use the checkout's required .NET toolchain. Export TERRASCALE_API_KEY,
TERRASCALE_PROJECT_ID, TERRASCALE_DATABASE_NAME and TERRASCALE_REGION
(one region in the database's set).

## 2. Connect through the ADO.NET provider

Follow the public guide's connection block: import TerraScale.DotNet and
TerraScale.DotNet.TerraBase, create an HttpClient with TerraBaseHttp.CreateHandler,
and build TerraBaseSqlClient using TerraBaseSqlClientOptions with ApiKey and Region
from the environment. Select Database(projectId, databaseName), wrap that handle
in TerraBaseDbConnection, and await OpenAsync.

There is **no connection string**: credentials belong to the SDK database handle.
The async-only provider throws NotSupportedException for synchronous execution,
begin, commit and rollback. Use async disposal and pass CancellationToken where
the API accepts it.
Source: [.NET connection and async contract](https://docs.terrascale.tech/guides/orm-dotnet).

## 3. Define SQL schema and choose migrations

Dapper maps results to types; it does not own schema generation. Create a numbered
ts migration containing:

~~~sql
CREATE TABLE people (id TEXT PRIMARY KEY, name TEXT NOT NULL);
~~~

Apply to the preview with ts migrate up <preview>. Map id and name to a Person
record's Id and Name properties, then use Dapper QueryAsync<Person> with
SELECT id, name FROM people WHERE name = @name and an object containing name.
Use ExecuteAsync with parameters for inserts; generate IDs using
TerraScaleIdGenerator.NewId. Do not add a second runtime schema owner.
Sources: [.NET Dapper](https://docs.terrascale.tech/guides/orm-dotnet),
[migrations](https://docs.terrascale.tech/guides/migrations).

## 4. Verify boundaries

BeginTransactionAsync opens a SQL WebSocket transaction; pass it as transaction:
to every Dapper command, commit asynchronously, and keep it within five seconds.
Async disposal without commit rolls back. Sync disposal detaches and relies on
the server limit. Only Serializable isolation exists.

EF Core is unsupported: it needs schema/savepoint facilities this SQL API does not
expose. Use Dapper, handwritten ADO.NET or the SDK. Explicit SQL BEGIN/COMMIT/ROLLBACK
is refused through this provider; server cursors and prepared-statement handles
are unavailable. SQLite dialect/API refusals apply unchanged.
Source: [.NET supported features](https://docs.terrascale.tech/guides/orm-dotnet).
