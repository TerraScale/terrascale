# Entity Framework Core provider

`TerraScale.EntityFrameworkCore` runs Entity Framework Core 10 against a TerraBase database. TerraBase speaks the
SQLite dialect, so the provider reuses EF Core's SQLite query translation, type mappings and migrations SQL, and sends
every command through this SDK: SQL REST (`POST …/sql/execute`) outside a transaction and the SQL WebSocket session
inside one.

## Setup

```csharp
using Microsoft.EntityFrameworkCore;
using TerraScale.DotNet.TerraBase;

builder.Services.AddSingleton(_ => new HttpClient(TerraBaseHttp.CreateHandler()));
builder.Services.AddSingleton(services => new TerraBaseSqlClient(
        services.GetRequiredService<HttpClient>(),
        new TerraBaseSqlClientOptions { Region = "gru", ApiKey = apiKey })
    .Database(projectId, "app"));
builder.Services.AddDbContextPool<AppDbContext>((services, options) =>
    options.UseTerraBase(services.GetRequiredService<TerraBaseSqlDatabase>()));
```

Pool contexts with `AddDbContextPool` or `PooledDbContextFactory`: a pooled context makes a one-row query about three
times cheaper on the client ([benchmark](../../../benchmark/bench/2026-11-09-efcore-provider.md)).

`UseTerraBase` takes either a `TerraBaseSqlDatabase` or a connection string:

```csharp
options.UseTerraBase("Region=gru;Project=01JA…;Database=app;ApiKey=ts_…");
options.UseTerraBase("Endpoint=https://api.terrascale.tech;Project=01JA…;Database=app;ApiKey=ts_…");
```

| Key | Meaning |
|---|---|
| `Endpoint` | Origin of the TerraBase client listener. Without it and `AuthEndpoint`, requests go to `https://api.terrascale.tech`. |
| `Region` | A region id such as `gru`; requests go to `https://api.terrascale.tech` pinned to that region. |
| `Project` | The project ULID. Required. |
| `Database` | The database name or ULID. Required. |
| `ApiKey` | An API key with `sql:read` and `sql:write`. Required. |
| `AuthEndpoint` | Origin of the ts-auth that exchanges the API key. Defaults to `https://auth.terrascale.tech` only when requests go to `https://api.terrascale.tech` (no `Endpoint`, `Region`, or that `Endpoint`); any other `Endpoint` needs it, so the key never goes to production by accident. |

All connection strings share one connection pool. The provider keeps the SQL client of the 64 most recently used
connection strings, so each API key's access token is reused across contexts.

For an `AccessTokenProvider`, or a platform service's peer credential on an injected `HttpClient`, build the
`TerraBaseSqlDatabase` yourself and pass it. REST requests and SQL WebSocket sessions both go through that
`HttpClient` and its handlers with the same credential, so all three ways the SDK authenticates (API key, access-token
provider, a header your handler adds) work for queries, saves, transactions and migrations. A platform service on
TerraBase's internal listener (HTTP/2 only, peer credential) builds its client without a credential, as ts-auth does:

```csharp
builder.Services.AddSingleton(services => new TerraBaseSqlClient(services.GetRequiredService<HttpClient>(),
        new TerraBaseSqlClientOptions { BaseAddress = internalListener })
    .Database(projectId, "app"));
```

Databases are created and deleted through the TerraBase database API (`TerraBaseDatabaseClient`), never by EF Core:
`EnsureCreatedAsync` creates the model's tables in an existing database, and `EnsureDeletedAsync` throws.

## Options

`UseTerraBase(database, terraBase => …)` exposes EF Core's relational options: `MigrationsAssembly`,
`MigrationsHistoryTable`, `CommandTimeout`, `MaxBatchSize` (at most 256, the statements one request carries),
`UseQuerySplittingBehavior` and `ExecutionStrategy`. `CommandTimeout` bounds each command in seconds and throws
`TimeoutException` when it runs out; without it, the SDK's 30-second request deadline applies, and it also caps longer
timeouts.

## Saving changes

`SaveChangesAsync` without an explicit transaction sends the whole change set as one `/sql/execute` request, which
TerraBase runs as one transaction: everything is written or nothing is. A request holds at most 256 statements and
1 MiB; a larger change set is split into several requests that run inside one WebSocket transaction instead.

One row must fit in one request. A row's values travel as JSON: text costs its UTF-8 bytes (up to 6 bytes per
control character), and a `byte[]` is base64, so a single blob can hold about 760 KiB. A larger value fails with
`too_large`; keep big files in object storage and store their key. The SQL EF Core generates for one row must also stay
under TerraBase's 16 KiB statement limit, which only very wide tables reach; the provider refuses such a row with
`NotSupportedException`, and the save writes nothing.

Every `UPDATE` and `DELETE` must change exactly one row. The provider follows each one with
`SELECT terrascale_expect_changes(changes(), 1)`, which fails the request with `409 row_conflict` when the row was not
changed, so a stale concurrency token or a row deleted by someone else throws `DbUpdateConcurrencyException` (its inner
exception is the `TerraBaseSqlException` with code `row_conflict`) and the rest of the save is rolled back with it. A constraint violation
(`unique_violation`, `foreign_key_violation`, `check_violation`, …) throws `DbUpdateException` whose inner
exception is the `TerraBaseSqlException`, and likewise writes nothing.

Concurrency tokens work best as a client-generated ULID:

```csharp
modelBuilder.Entity<Account>().Property(account => account.Version).IsConcurrencyToken();

account.Email = email;
account.Version = ids.NewId();
await context.SaveChangesAsync(ct);
```

An update or delete that reads back store-generated values cannot be guarded inside one request; a save that
contains one together with other changes runs inside a WebSocket transaction.

## Transactions and retries

The default execution strategy retries the whole operation when TerraBase answers `serialization_conflict`, with
EF Core's exponential backoff (6 attempts). Like every retrying EF Core strategy, it requires explicit transactions
to run inside it:

```csharp
await context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
{
    await using var transaction = await context.Database.BeginTransactionAsync(ct);
    context.Sessions.Add(session);
    await context.SaveChangesAsync(ct);
    await transaction.CommitAsync(ct);
});
```

`BeginTransactionAsync` opens a SQL WebSocket session and begins a Serializable transaction on it. A transaction lasts
at most 5 seconds. After a failed statement, including a concurrency conflict, the transaction is aborted: roll it
back and retry the unit of work. Disposing the transaction without committing rolls it back: `DisposeAsync` waits up to
5 seconds for the rollback, and a synchronous `Dispose` (a `using var` declaration) closes the session at once, which
rolls it back on the server, and never hides the exception that left the block.

The execution strategy retries nothing else. Below it, the SDK's HTTP client resends a SQL request up to 2 more
times, after `Retry-After` when TerraBase sends one, only when TerraBase answers `serialization_conflict` or a `503` it
marks retry-safe (`remote_region_unavailable`, `confirmation_busy`, `catalog_stale`). SQL requests are POSTs, so a
`502`, a `504` or a lost connection is not resent: the write may already have committed. A `421 wrong_region` is not
retried at any level either; it means nothing was written, so catch `TerraBaseWrongRegionException` and send the work to
`HomeRegion`. A region that forwards the request to the database's writer answers `421` only when the request never
left it. When the writer was sent the request and did not answer, the region answers
`503 write_outcome_unknown` instead (`TerraBaseException.ErrorCode`, the writer's region in `OutcomeRegion`): the write
may have committed, so nothing resends it. Read what it would have written before you run it again.

## Writers and replicas

- A database has one writer at a time. A save that fits one request holds it only while TerraBase runs that request.
  An explicit transaction, a split save and each migration hold it from their first write until they end, up to
  5 seconds, and every other write to the database waits for them. Keep hot paths to saves that fit one request, and
  keep explicit transactions short.
- In a region that holds a replica of the database, reads outside a transaction run on the local replica, so a read
  right after a write made through another region can miss it. Writes, and reads inside an explicit transaction, run
  at the database's home. To read your own writes everywhere, add `TerraScale-Write: home` to every request with a
  `DelegatingHandler` on the `HttpClient` you build the `TerraBaseSqlDatabase` with; the provider has no per-query
  switch for it.

## Migrations

`MigrateAsync`, `EnsureCreatedAsync`, `GetAppliedMigrationsAsync` and script generation (`GenerateScript`,
`dotnet ef migrations script`) work against TerraBase. The provider ships its design-time services for
`dotnet ef migrations add`.

- Never edit a merged migration; add a new one. EF Core records each applied migration by its id in the history
  table (`__EFMigrationsHistory` by default): an edited migration never runs again where it was applied, and a renamed
  or regenerated one runs again against the tables it already created.
- Each migration runs in one WebSocket transaction, so it must finish within the 5-second transaction limit. Split
  large data moves into their own migrations.
- The migrations lock is a row in `__EFMigrationsLock` that each migration transaction writes first, so migrations
  from concurrent migrators run one at a time. EF Core commits each migration on its own; when another migrator took
  the lock between two of them, the next one throws `TerraBaseMigrationConflictException` before it changes anything,
  and the default execution strategy runs the migration again from a fresh read of the history. Concurrent
  `MigrateAsync` calls therefore apply every pending migration exactly once.
- Idempotent scripts (`--idempotent`) need procedural SQL that TerraBase does not have and are refused.
- Some changes (altering or dropping a column, adding or dropping a foreign key, primary key, unique or check
  constraint on an existing table) make EF Core rebuild the table: create a copy, move the rows, drop the old table
  and rename. TerraBase always enforces foreign keys, so the provider runs the rebuild with them on, inside the
  migration transaction. It refuses to rebuild a table that other tables (or the table itself) reference, because
  dropping it would delete or refuse the referencing rows; change such tables in steps that run in place (add
  columns and indexes), or write the change as explicit SQL with `migrationBuilder.Sql`.

### Generating migrations

`dotnet ef` needs `Microsoft.EntityFrameworkCore.Design`, which brings Roslyn, MSBuild and Newtonsoft.Json. In the
monorepo those stay out of every service: a design host, a project named `<Service>.EntityFrameworkDesign`, carries
them. It references the project with the `DbContext` and holds an `IDesignTimeDbContextFactory`; the package-graph guard
allows Newtonsoft.Json only there, and only through the Design package, and refuses a design host that is packed,
published or referenced by another project. `src/TerraScale.Authentication.EntityFrameworkDesign` is the
example:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <IsPackable>false</IsPackable>
    <IsPublishable>false</IsPublishable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.EntityFrameworkCore.Design" PrivateAssets="all" />
    <ProjectReference Include="../TerraScale.Authentication/TerraScale.Authentication.csproj" />
  </ItemGroup>
</Project>
```

```csharp
public sealed class AuthDbContextFactory : IDesignTimeDbContextFactory<AuthDbContext>
{
    public AuthDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<AuthDbContext>().UseTerraBase("Region=gru;Project=design;Database=design;ApiKey=design").Options);
}
```

The factory's connection string is never used: generating a migration does not connect. Add the design host to the
area's solution and solution filter so CI builds it. The repository pins `dotnet-ef` in `dotnet-tools.json`; from the
repository root, ts-auth generates a migration with:

```sh
dotnet tool restore
dotnet ef migrations add <Name> --project src/TerraScale.Authentication \
    --startup-project src/TerraScale.Authentication.EntityFrameworkDesign --output-dir Storage/Migrations
dotnet format whitespace src/TerraScale.Authentication/TerraScale.Authentication.csproj
```

`dotnet ef` writes a UTF-8 byte order mark that the repository's `.editorconfig` refuses; `dotnet format whitespace`
removes it. Commit the generated files as they are otherwise, including EF Core's `// <auto-generated />` headers.

## Types

| .NET | Column | Stored as |
|---|---|---|
| `string` (ULID keys) | `TEXT` | text |
| `bool` | `INTEGER` | 1 or 0 |
| `int`, `long`, enums | `INTEGER` | integer |
| `double`, `float` | `REAL` | real |
| `decimal` | `TEXT` | `0.0###…` text |
| `DateTime` | `TEXT` | `yyyy-MM-dd HH:mm:ss.FFFFFFF`, UTC |
| `DateTimeOffset` | `TEXT` | `yyyy-MM-dd HH:mm:ss.FFFFFFF+HH:mm` |
| `DateOnly`, `TimeOnly`, `TimeSpan` | `TEXT` | ISO text |
| `Guid` | `TEXT` | upper-case text |
| `byte[]` | `BLOB` | blob |
| complex types and owned types with `ToJson()` | `TEXT` | JSON, queried with `->>` and `json_each` |

Unique indexes, foreign keys (always enforced) and check constraints map as in SQLite.

`DateTime` columns hold UTC. Every `DateTime` property without its own value converter gets the provider's UTC
converter: a `Local` value is converted to UTC when it is written, an `Unspecified` value is taken as UTC as it is,
and every value reads back as `DateTimeKind.Utc`. The stored text is unchanged, so comparisons and ordering still run
in SQL (`session.ExpiresAt > now`).

## Limits

- The provider is async-only, like the SDK: `SaveChanges()`, `ToList()`, `Migrate()`, `BeginTransaction()` and the
  other synchronous calls throw `NotSupportedException`. Use the `…Async` methods. Synchronous `Dispose` of a
  transaction, context or connection works.
- EF Core's SQLite rules apply to queries: comparing or ordering `DateTimeOffset`, `TimeSpan` and `ulong` values is
  not translated. Store instants you compare as UTC `DateTime`, or convert them to `long`
  (`HasConversion<DateTimeOffsetToBinaryConverter>()`).
- `decimal` arithmetic and comparisons, and `Regex.IsMatch`, use functions EF Core registers in a local SQLite
  connection. TerraBase has no such functions, so these queries fail with `no such function`. Store money as `long`
  minor units.
- Integer keys get `AUTOINCREMENT` by EF Core's SQLite convention, and TerraBase sends inserts into such tables to
  the database's home region. Use client-generated ULID string keys.
- Regions route writes by statement shape ([write routing](../../../docs/terrabase/sql/write-routing.md)). Inserts of
  ULID-keyed rows can commit in the region; an update with a concurrency token is not "by key", and a save that
  mixes an update or delete with later writes reads between writes, so those saves run at the database's home.
- `sqlite_master` lists table names in lower case.
- There are no sequences, schemas or spatial types.
- EF Core's SQLite query pipeline reads the version of the bundled native SQLite library when it starts; the
  provider ships that library for this reason only and never opens a local database.

## Changing the provider

ts-auth and ts-dashboard run this provider and are released on every main push that changes `sdk/dotnet/`, but
production TerraBase is released by hand later. A provider change that emits SQL or reads a code only a newer TerraBase
serves (such as a new SQL function) merges in its own PR after production TerraBase runs a build with that server
change; until then the provider keeps the SQL production TerraBase already serves.
