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
        var owner = Literal(Guid.NewGuid().ToString("N"));
        await Command(
            $"""
            CREATE TABLE IF NOT EXISTS "{LockTableName}" ("Id" INTEGER NOT NULL CONSTRAINT "PK_{LockTableName}" PRIMARY KEY, "Owner" TEXT NOT NULL, "Timestamp" TEXT NOT NULL);
            INSERT OR REPLACE INTO "{LockTableName}" ("Id", "Owner", "Timestamp") VALUES (1, {owner}, {Timestamp()});
            """).ExecuteNonQueryAsync(Parameters(), cancellationToken).ConfigureAwait(false);
        return new TransactionLock(this, owner);
    }

    private async Task<IMigrationsDatabaseLock> RenewAsync(TransactionLock held, CancellationToken cancellationToken)
    {
        Dependencies.MigrationsLogger.AcquiringMigrationLock();
        var renewed = await Command(
            $"""
            UPDATE "{LockTableName}" SET "Timestamp" = {Timestamp()} WHERE "Id" = 1 AND "Owner" = {held.Owner};
            SELECT changes();
            """).ExecuteScalarAsync(Parameters(), cancellationToken).ConfigureAwait(false);
        return renewed is 1L ? held : throw new TerraBaseMigrationConflictException(
            "Another migrator applied migrations between two of this migrator's migrations; the execution strategy plans the remaining migrations again.");
    }

    private IRelationalCommand Command(string sql) => Dependencies.RawSqlCommandBuilder.Build(sql);

    private RelationalCommandParameterObject Parameters() =>
        new(Dependencies.Connection, null, null, Dependencies.CurrentContext.Context, Dependencies.CommandLogger, CommandSource.Migrations);

    private string Literal(string value) => Dependencies.TypeMappingSource.GetMapping(typeof(string)).GenerateSqlLiteral(value);

    private string Timestamp() => Dependencies.TypeMappingSource.GetMapping(typeof(DateTimeOffset)).GenerateSqlLiteral(DateTimeOffset.UtcNow);

    private static NotSupportedException IdempotentScripts() =>
        new("Idempotent migration scripts need procedural SQL that TerraBase does not have; apply migrations with MigrateAsync or a plain script.");

    private sealed class TransactionLock(TerraBaseHistoryRepository repository, string owner) : IMigrationsDatabaseLock
    {
        public string Owner => owner;

        public IHistoryRepository HistoryRepository => repository;

        public IMigrationsDatabaseLock ReacquireIfNeeded(bool connectionReopened, bool? transactionRestarted) =>
            throw new NotSupportedException("TerraBase is async-only; use MigrateAsync.");

        public Task<IMigrationsDatabaseLock> ReacquireIfNeededAsync(bool connectionReopened, bool? transactionRestarted, CancellationToken cancellationToken = default) =>
            transactionRestarted is true ? repository.RenewAsync(this, cancellationToken) : Task.FromResult<IMigrationsDatabaseLock>(this);

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
