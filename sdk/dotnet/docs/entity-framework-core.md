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
| `Endpoint` | Origin of the TerraBase client listener. Set this or `Region`. |
| `Region` | A region id such as `gru`; requests go to `https://api.terrascale.tech` pinned to that region. |
| `Project` | The project ULID. Required. |
| `Database` | The database name or ULID. Required. |
| `ApiKey` | An API key with `sql:read` and `sql:write`. Required. |

The provider builds one `HttpClient` and SQL client per distinct connection string and reuses it for every context.
For an `AccessTokenProvider`, or a platform service's peer credential on an injected `HttpClient`, build the
`TerraBaseSqlDatabase` yourself and pass it: every way the SDK authenticates works through that overload.

Databases are created and deleted through the TerraBase database API (`TerraBaseDatabaseClient`), never by EF Core:
`EnsureCreatedAsync` creates the model's tables in an existing database, and `EnsureDeletedAsync` throws.

## Options

`UseTerraBase(database, terraBase => …)` exposes EF Core's relational options: `MigrationsAssembly`,
`MigrationsHistoryTable`, `CommandTimeout`, `MaxBatchSize` (at most 256, the statements one request carries),
`UseQuerySplittingBehavior` and `ExecutionStrategy`.

## Saving changes

`SaveChangesAsync` without an explicit transaction sends the whole change set as one `/sql/execute` request, which
TerraBase runs as one transaction: everything is written or nothing is. A request holds at most 256 statements and
1 MiB; a larger change set is split into several requests that run inside one WebSocket transaction instead.

Every `UPDATE` and `DELETE` must change exactly one row. The provider follows each one with a guard statement that
fails the request when the row was not changed, so a stale concurrency token or a row deleted by someone else throws
`DbUpdateConcurrencyException` and the rest of the save is rolled back with it. A constraint violation
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
back and retry the unit of work.

## Migrations

`MigrateAsync`, `EnsureCreatedAsync`, `GetAppliedMigrationsAsync` and script generation (`GenerateScript`,
`dotnet ef migrations script`) work against TerraBase. `dotnet ef migrations add` works with
`Microsoft.EntityFrameworkCore.Design` in the startup project; the provider ships its design-time services.

- Each migration runs in one WebSocket transaction, so it must finish within the 5-second transaction limit. Split
  large data moves into their own migrations.
- The migrations lock is a row in `__EFMigrationsLock` written inside the migration transaction; concurrent
  migrators serialize on it and the lock ends with the transaction.
- Idempotent scripts (`--idempotent`) need procedural SQL that TerraBase does not have and are refused.
- Some changes (altering or dropping a column, adding or dropping a foreign key, primary key, unique or check
  constraint on an existing table) make EF Core rebuild the table: create a copy, move the rows, drop the old table
  and rename. TerraBase always enforces foreign keys, so the provider runs the rebuild with them on, inside the
  migration transaction. It refuses to rebuild a table that other tables (or the table itself) reference, because
  dropping it would delete or refuse the referencing rows; change such tables in steps that run in place (add
  columns and indexes), or write the change as explicit SQL with `migrationBuilder.Sql`.

## Types

| .NET | Column | Stored as |
|---|---|---|
| `string` (ULID keys) | `TEXT` | text |
| `bool` | `INTEGER` | 1 or 0 |
| `int`, `long`, enums | `INTEGER` | integer |
| `double`, `float` | `REAL` | real |
| `decimal` | `TEXT` | `0.0###…` text |
| `DateTime` | `TEXT` | `yyyy-MM-dd HH:mm:ss.FFFFFFF` |
| `DateTimeOffset` | `TEXT` | `yyyy-MM-dd HH:mm:ss.FFFFFFF+HH:mm` |
| `DateOnly`, `TimeOnly`, `TimeSpan` | `TEXT` | ISO text |
| `Guid` | `TEXT` | upper-case text |
| `byte[]` | `BLOB` | blob |
| complex types and owned types with `ToJson()` | `TEXT` | JSON, queried with `->>` and `json_each` |

Unique indexes, foreign keys (always enforced) and check constraints map as in SQLite.

## Limits

- The provider is async-only, like the SDK: `SaveChanges()`, `ToList()`, `Migrate()` and the other synchronous
  calls throw `NotSupportedException`. Use the `…Async` methods.
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
