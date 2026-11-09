using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;

namespace TerraScale.EntityFrameworkCore.Migrations;

internal sealed class TerraBaseHistoryRepository(HistoryRepositoryDependencies dependencies) : HistoryRepository(dependencies)
{
    private const string LockTableName = "__EFMigrationsLock";

    public override LockReleaseBehavior LockReleaseBehavior => LockReleaseBehavior.Transaction;

    protected override string ExistsSql =>
        $"""SELECT COUNT(*) FROM "sqlite_master" WHERE "name" = {Dependencies.TypeMappingSource.GetMapping(typeof(string)).GenerateSqlLiteral(TableName)} COLLATE NOCASE AND "type" = 'table';""";

    protected override bool InterpretExistsResult(object? value) => value is long count && count != 0;

    public override string GetCreateIfNotExistsScript()
    {
        var script = GetCreateScript();
        return script.Insert(script.IndexOf("CREATE TABLE", StringComparison.Ordinal) + "CREATE TABLE".Length, " IF NOT EXISTS");
    }

    public override string GetBeginIfNotExistsScript(string migrationId) => throw IdempotentScripts();

    public override string GetBeginIfExistsScript(string migrationId) => throw IdempotentScripts();

    public override string GetEndIfScript() => throw IdempotentScripts();

    public override IMigrationsDatabaseLock AcquireDatabaseLock() =>
        throw new NotSupportedException("TerraBase is async-only; use MigrateAsync.");

    public override async Task<IMigrationsDatabaseLock> AcquireDatabaseLockAsync(CancellationToken cancellationToken = default)
    {
        Dependencies.MigrationsLogger.AcquiringMigrationLock();
        var timestamp = Dependencies.TypeMappingSource.GetMapping(typeof(DateTimeOffset)).GenerateSqlLiteral(DateTimeOffset.UtcNow);
        await Dependencies.RawSqlCommandBuilder.Build(
            $"""
            CREATE TABLE IF NOT EXISTS "{LockTableName}" ("Id" INTEGER NOT NULL CONSTRAINT "PK_{LockTableName}" PRIMARY KEY, "Timestamp" TEXT NOT NULL);
            INSERT OR REPLACE INTO "{LockTableName}" ("Id", "Timestamp") VALUES (1, {timestamp});
            """).ExecuteNonQueryAsync(
                new RelationalCommandParameterObject(Dependencies.Connection, null, null, Dependencies.CurrentContext.Context, Dependencies.CommandLogger, CommandSource.Migrations),
                cancellationToken).ConfigureAwait(false);
        return new TransactionLock(this);
    }

    private static NotSupportedException IdempotentScripts() =>
        new("Idempotent migration scripts need procedural SQL that TerraBase does not have; apply migrations with MigrateAsync or a plain script.");

    private sealed class TransactionLock(IHistoryRepository historyRepository) : IMigrationsDatabaseLock
    {
        public IHistoryRepository HistoryRepository => historyRepository;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
