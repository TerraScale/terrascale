using System.Globalization;
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
            ParsedSql statement => await RunAsync(statement, command, cancellationToken).ConfigureAwait(false),
        };
    }

    public async ValueTask<SqlDescribeOutcome> DescribeAsync(SqlCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return SqlParse.Parse(command.Sql) switch
        {
            SqlError error => error,
            // Columns come from the statement run with no rows: names and declared types exactly as execution reports them.
            ReadSql read => await RunAsync(read, command with { Args = [], Named = new Dictionary<string, SqlValue>() }, cancellationToken, describe: true)
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

    private async ValueTask<SqlOutcome> RunAsync(ParsedSql statement, SqlCommand command, CancellationToken cancellationToken, bool describe = false)
    {
        if (await engine.BeginTransactionAsync(database, write: statement.IsWrite, cancellationToken).ConfigureAwait(false) is not { } transaction)
        {
            return UnknownDatabase;
        }

        await using (transaction.ConfigureAwait(false))
        {
            try
            {
                var outcome = await ExecuteAsync(transaction, statement, command, describe, cancellationToken).ConfigureAwait(false);
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
            catch (QueryCapacityException)
            {
                return SqlErrors.Interrupt;
            }
            catch (SqlProgramException error)
            {
                return SqlErrors.Error(error.Message);
            }
        }
    }

    // The engine read of `read`: its tables' definitions, its parameters bound, its rows converted.
    private static async ValueTask<SqlOutcome> ReadAsync(
        IStorageTransaction transaction, ReadSql read, SqlCommand command, bool describe, CancellationToken cancellationToken)
    {
        var tables = new Dictionary<string, SqlTableDefinition>(StringComparer.Ordinal);
        foreach (var name in read.Tables)
        {
            if (await SqlTable.ReadAsync(transaction, name, cancellationToken).ConfigureAwait(false) is not { } table)
            {
                return SqlErrors.Error("no such table");
            }

            tables[name] = table;
        }

        if (Bind(read.Params, command) is not { } parameters)
        {
            return SqlErrors.Error(string.Create(CultureInfo.InvariantCulture, $"the statement takes {read.Params.Count} arguments"));
        }

        var result = await transaction.ExecuteSqlAsync(SqlRead.Program(read, tables, parameters, describe), cancellationToken).ConfigureAwait(false);
        if (result.Truncated)
        {
            return SqlErrors.TooBig("a result holds at most 10000 rows and 8 MiB; add a LIMIT");
        }

        var rows = new List<IReadOnlyList<SqlValue>>(result.Rows.Count);
        foreach (var row in result.Rows)
        {
            var values = new SqlValue[row.Length];
            for (var i = 0; i < row.Length; i++)
            {
                switch (row[i])
                {
                    case long integer:
                        values[i] = SqlValue.Of(integer);
                        break;
                    case double real:
                        values[i] = SqlValue.Of(real);
                        break;
                    case string text:
                        values[i] = SqlValue.Of(text);
                        break;
                    case null:
                        break;
                    default:
                        return SqlErrors.Error("blob values are not supported");
                }
            }

            rows.Add(values);
        }

        var types = SqlRead.DeclTypes(read, tables, result.Columns.Count);
        return new SqlResult([.. result.Columns.Select((name, i) => new SqlColumn(name, types[i]))], rows, 0, null);
    }

    /// <summary>
    /// One value per parameter: positional arguments by number, a named argument (with or without its prefix) over it;
    /// unbound parameters are NULL, as in SQLite. Null when more positional arguments than parameters arrive.
    /// </summary>
    private static object?[]? Bind(IReadOnlyList<string?> names, SqlCommand command)
    {
        if (command.Args.Count > names.Count)
        {
            return null;
        }

        var values = new object?[names.Count];
        for (var i = 0; i < names.Count; i++)
        {
            var value = i < command.Args.Count ? command.Args[i] : SqlValue.Null;
            if (names[i] is { } name && (command.Named.TryGetValue(name, out var named) || command.Named.TryGetValue(name[1..], out named)))
            {
                value = named;
            }

            values[i] = value.Kind switch
            {
                SqlValueKind.Integer => value.Integer,
                SqlValueKind.Real => value.Real,
                SqlValueKind.Text => value.Text,
                _ => null,
            };
        }

        return values;
    }

    // Runs one statement in `transaction`, which the caller commits.
    private async ValueTask<SqlOutcome> ExecuteAsync(
        IStorageTransaction transaction, ParsedSql statement, SqlCommand command, bool describe, CancellationToken cancellationToken)
    {
        switch (statement)
        {
            case ReadSql read:
                return await ReadAsync(transaction, read, command, describe, cancellationToken).ConfigureAwait(false);
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
