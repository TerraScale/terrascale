using TerraBase.Engine;
using TerraBase.Primitives;
using TerraBase.Tenancy;

namespace TerraBase.Features.Sql;

/// <summary>Relational SQL over a database's collections (docs/terrabase/sql/relational-sql.md).</summary>
internal sealed class SqlService(IStorageEngine engine, TimeProvider time) : ISqlService
{
    public ISqlSession Open(TenantContext database, bool canWrite)
    {
        ArgumentNullException.ThrowIfNull(database);
        return new SqlSession(engine, time, database, canWrite);
    }
}

/// <summary>One connection: every statement runs in its own storage transaction (autocommit).</summary>
internal sealed class SqlSession(IStorageEngine engine, TimeProvider time, TenantContext database, bool canWrite) : ISqlSession
{
    private static readonly SqlResult Empty = new([], [], 0, null);
    private static readonly SqlError UnknownDatabase = SqlErrors.Error("the database does not exist");

    public bool IsAutocommit => true;

    public async ValueTask<SqlOutcome> ExecuteAsync(SqlCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return SqlParse.Parse(command.Sql) switch
        {
            SqlError error => error,
            ParsedSql { IsWrite: true } when !canWrite => SqlErrors.Auth,
            ParsedSql statement => await RunAsync(statement, cancellationToken).ConfigureAwait(false),
        };
    }

    public ValueTask<SqlDescribeOutcome> DescribeAsync(SqlCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return ValueTask.FromResult<SqlDescribeOutcome>(SqlParse.Parse(command.Sql) switch
        {
            SqlError error => error,
            ParsedSql statement => new SqlDescription([], [], IsExplain: false, IsReadOnly: !statement.IsWrite),
        });
    }

    public ValueTask EndRequestAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async ValueTask<SqlOutcome> RunAsync(ParsedSql statement, CancellationToken cancellationToken)
    {
        if (await engine.BeginTransactionAsync(database, write: statement.IsWrite, cancellationToken).ConfigureAwait(false) is not { } transaction)
        {
            return UnknownDatabase;
        }

        await using (transaction.ConfigureAwait(false))
        {
            try
            {
                var outcome = await ExecuteAsync(transaction, statement, cancellationToken).ConfigureAwait(false);
                if (outcome is SqlResult && statement.IsWrite)
                {
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                }

                return outcome;
            }
            catch (StorageBusyException)
            {
                return SqlErrors.Busy("the database is busy; retry");
            }
            catch (Exception error) when (error is ChangeGroupCapacityException or StorageWriteLimitException)
            {
                return SqlErrors.TooBig("a transaction writes at most 1024 rows and 2 MiB");
            }
        }
    }

    // Runs one statement in `transaction`, which the caller commits.
    private async ValueTask<SqlOutcome> ExecuteAsync(IStorageTransaction transaction, ParsedSql statement, CancellationToken cancellationToken)
    {
        switch (statement)
        {
            case CreateTableSql create:
                if (await transaction.GetCollectionAsync(create.Name, cancellationToken).ConfigureAwait(false) is not null)
                {
                    return create.IfNotExists && await SqlTable.ReadAsync(transaction, create.Name, cancellationToken).ConfigureAwait(false) is not null
                        ? Empty
                        : SqlErrors.Error("the table already exists, or a document collection has its name");
                }

                var collection = new CollectionInfo(create.Name, time.GetUtcNow().ToUnixTimeMilliseconds(), EntityIds.New(time).ToString());
                await transaction.CreateCollectionAsync(collection, cancellationToken).ConfigureAwait(false);
                await SqlTable.WriteAsync(transaction, create.Name, create.Table, cancellationToken).ConfigureAwait(false);
                if (create.Unique.Count > 0)
                {
                    await transaction.SetUniqueConstraintsAsync(create.Name, create.Unique, cancellationToken).ConfigureAwait(false);
                }

                return Empty;
            case DropTableSql drop:
                if (await SqlTable.ReadAsync(transaction, drop.Name, cancellationToken).ConfigureAwait(false) is null)
                {
                    return drop.IfExists ? Empty : SqlErrors.Error("no such table");
                }

                await transaction.DropCollectionAsync(drop.Name, cancellationToken).ConfigureAwait(false);
                return Empty;
            default:
                throw new ArgumentOutOfRangeException(nameof(statement));
        }
    }
}
