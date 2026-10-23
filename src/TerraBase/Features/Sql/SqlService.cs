using System.Diagnostics;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.Documents;
using TerraBase.Features.Management.Usage;
using TerraBase.Features.Qos;
using TerraBase.Tenancy;

namespace TerraBase.Features.Sql;

/// <summary>Relational SQL over a database's collections (docs/terrabase/sql/relational-sql.md).</summary>
internal sealed class SqlService(IStorageEngine engine, IDocumentService documents, IStorageQuota quota, IUsageMeter usage, TimeProvider time) : ISqlService
{
    public ISqlSession Open(TenantContext database, bool canWrite)
    {
        ArgumentNullException.ThrowIfNull(database);
        return new SqlSession(engine, quota, usage, new SqlExecutor(documents, time), database, canWrite);
    }
}

/// <summary>One connection: every statement runs in its own storage transaction (autocommit).</summary>
internal sealed class SqlSession(IStorageEngine engine, IStorageQuota quota, IUsageMeter usage, SqlExecutor executor, TenantContext database, bool canWrite)
    : ISqlSession
{
    private static readonly SqlError UnknownDatabase = SqlErrors.Error("the database does not exist");

    public bool IsAutocommit => true;

    public async ValueTask<SqlOutcome> ExecuteAsync(SqlCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return SqlParse.Parse(command.Sql) switch
        {
            SqlError error => error,
            ParsedSql { IsWrite: true } when !canWrite => SqlErrors.Auth,
            ParsedSql statement => await RunAsync(statement, command, describe: false, cancellationToken).ConfigureAwait(false),
        };
    }

    public async ValueTask<SqlDescribeOutcome> DescribeAsync(SqlCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return SqlParse.Parse(command.Sql) switch
        {
            SqlError error => error,
            // Columns come from the statement run with no rows: names and declared types exactly as execution reports them.
            ReadSql read => await RunAsync(read, command with { Args = [], Named = new Dictionary<string, SqlValue>() }, describe: true, cancellationToken)
                .ConfigureAwait(false) switch
            {
                SqlResult result => new SqlDescription(read.Params, result.Columns, IsExplain: false, IsReadOnly: true),
                SqlError error => error,
            },
            ParsedSql statement => new SqlDescription(statement.Params, [], IsExplain: false, IsReadOnly: !statement.IsWrite),
        };
    }

    public ValueTask EndRequestAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // One statement in its own storage transaction; a write is admitted by the storage quota and committed.
    private async ValueTask<SqlOutcome> RunAsync(ParsedSql statement, SqlCommand command, bool describe, CancellationToken cancellationToken)
    {
        if (await engine.OpenDatabaseAsync(database, cancellationToken).ConfigureAwait(false) is not { } store)
        {
            return UnknownDatabase;
        }

        StorageQuotaLease? lease = null;
        if (statement.IsWrite)
        {
            var entered = await quota.EnterAsync(database, store, cancellationToken).ConfigureAwait(false);
            if (entered is not StorageQuotaLease admitted)
            {
                return entered switch
                {
                    Unavailable => SqlErrors.Busy("storage accounting is unavailable; retry"),
                    StorageQuotaLease => throw new UnreachableException(),
                };
            }

            lease = admitted;
        }

        using (lease)
        {
            try
            {
                var transaction = await store.BeginTransactionAsync(statement.IsWrite, cancellationToken).ConfigureAwait(false);
                await using (transaction.ConfigureAwait(false))
                {
                    var tally = new SqlTally(lease is { RequiresAccounting: true });
                    var outcome = await executor.ExecuteAsync(transaction, statement, command, new SqlKeys(), tally, describe, cancellationToken).ConfigureAwait(false);
                    if (outcome is not SqlResult result || lease is null)
                    {
                        return outcome;
                    }

                    return await CommitAsync(transaction, lease, tally, result, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception error) when (Failure(error) is { } failure)
            {
                return failure;
            }
        }
    }

    // Commits a write transaction under the storage quota, and meters it.
    private async ValueTask<SqlOutcome> CommitAsync(
        IStorageTransaction transaction, StorageQuotaLease lease, SqlTally tally, SqlResult result, CancellationToken cancellationToken)
    {
        switch (await lease.CheckAsync(tally.Delta, cancellationToken).ConfigureAwait(false))
        {
            case QuotaExceeded:
                return new SqlError("SQLITE_FULL", "the organization's storage quota is exhausted");
            case Unavailable:
                return SqlErrors.Busy("storage accounting is unavailable; retry");
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        lease.Committed(tally.Delta);
        usage.RecordWrites(database, (int)Math.Min(result.RowsAffected, int.MaxValue));
        return result;
    }

    // The SQL error an engine failure means, or null for an unexpected one.
    private static SqlError? Failure(Exception error) => error switch
    {
        StorageBusyException => SqlErrors.Busy("the database is busy; retry"),
        ChangeGroupCapacityException or StorageWriteLimitException => SqlErrors.TooBig("a transaction writes at most 1024 rows and 2 MiB"),
        QueryCapacityException => SqlErrors.Interrupt,
        SqlProgramException program => SqlErrors.Error(program.Message),
        _ => null,
    };
}
