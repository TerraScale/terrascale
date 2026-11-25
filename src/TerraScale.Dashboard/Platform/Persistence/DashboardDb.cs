using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Net.WebSockets;
using Dapper;
using TerraScale.DotNet.TerraBase;
using SdkTerraBaseException = TerraScale.DotNet.TerraBase.TerraBaseException;

[module: DapperAot]
[module: TypeHandler(typeof(DateTimeOffset), typeof(TerraScale.Dashboard.Platform.Persistence.UtcDateTimeOffsetHandler))]

namespace TerraScale.Dashboard.Platform.Persistence;

public sealed class DashboardDb(TerraBaseSqlDatabase database)
{
    private static readonly TimeSpan FirstConflictBackoff = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan LongestConflictBackoff = TimeSpan.FromMilliseconds(400);

    public async Task<TerraBaseDbConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new TerraBaseDbConnection(database);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    public async Task WriteAsync(Func<TerraBaseDbConnection, DbTransaction, Task> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        await ServerTiming.StateWriteAsync(async () =>
        {
            try
            {
                await using var connection = await OpenAsync(cancellationToken);
                await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
                await work(connection, transaction);
                await transaction.CommitAsync(cancellationToken);
            }
            catch (TerraBaseSqlException error) when (DashboardStorageOutcomes.IsConflict(error))
            {
                throw new DBConcurrencyException("Dashboard storage changed concurrently.", error);
            }
        });
    }

    public async Task<StorageWrite> TryWriteAsync(Func<TerraBaseDbConnection, DbTransaction, Task<bool>> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        return await ServerTiming.StateWriteAsync<StorageWrite>(async () =>
        {
            try
            {
                await using var connection = await OpenAsync(cancellationToken);
                await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
                if (!await work(connection, transaction))
                {
                    return new StorageConflicted();
                }

                await transaction.CommitAsync(cancellationToken);
                return new StorageWritten();
            }
            catch (TerraBaseSqlException error) when (DashboardStorageOutcomes.IsConflict(error))
            {
                return new StorageConflicted();
            }
            catch (HttpRequestException error) when (IsDuplicate(error))
            {
                return new StorageDuplicate();
            }
            catch (HttpRequestException error)
            {
                return new StorageFailed(StorageFailure.Describe(error));
            }
            catch (WebSocketException error)
            {
                return new StorageFailed(StorageFailure.Describe(error));
            }
            catch (IOException error)
            {
                return new StorageFailed(StorageFailure.Describe(error));
            }
            catch (TimeoutException error)
            {
                return new StorageFailed(StorageFailure.Describe(error));
            }
            catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
            {
                return new StorageFailed(StorageFailure.Describe(error));
            }
        });
    }

    public async Task<StorageFailed?> TryReadAsync(Func<TerraBaseDbConnection, Task> read, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(read);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await read(connection);
            return null;
        }
        catch (HttpRequestException error)
        {
            return new StorageFailed(StorageFailure.Describe(error));
        }
        catch (WebSocketException error)
        {
            return new StorageFailed(StorageFailure.Describe(error));
        }
        catch (IOException error)
        {
            return new StorageFailed(StorageFailure.Describe(error));
        }
        catch (TimeoutException error)
        {
            return new StorageFailed(StorageFailure.Describe(error));
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            return new StorageFailed(StorageFailure.Describe(error));
        }
    }

    public static async Task<int> RunAsync(TerraBaseDbConnection connection, string sql, IReadOnlyDictionary<string, object?> parameters,
        DbTransaction? transaction, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, sql, parameters, transaction);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static async Task<IReadOnlyList<T>> RowsAsync<T>(TerraBaseDbConnection connection, string sql, IReadOnlyDictionary<string, object?> parameters,
        Func<DbDataReader, T> read, DbTransaction? transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(read);
        await using var command = Command(connection, sql, parameters, transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        List<T> rows = [];
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(read(reader));
        }

        return rows;
    }

    private static DbCommand Command(TerraBaseDbConnection connection, string sql, IReadOnlyDictionary<string, object?> parameters, DbTransaction? transaction)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(parameters);
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }

        return command;
    }

    public static Task ConflictBackoffAsync(int attempt, TimeProvider time, CancellationToken cancellationToken) =>
        Task.Delay(ConflictBackoff(attempt, Random.Shared.NextDouble()), time, cancellationToken);

    public static bool IsConflict(Exception error) =>
        error is DBConcurrencyException || error is TerraBaseSqlException sql && DashboardStorageOutcomes.IsConflict(sql);

    private static TimeSpan ConflictBackoff(int attempt, double jitter) =>
        TimeSpan.FromTicks(Math.Clamp((long)(Math.Min(LongestConflictBackoff.Ticks, FirstConflictBackoff.Ticks << Math.Clamp(attempt, 0, 8)) * (0.5 + Math.Clamp(jitter, 0, 1))),
            FirstConflictBackoff.Ticks, LongestConflictBackoff.Ticks));

    public async Task<T> ReadAsync<T>(Func<TerraBaseDbConnection, Task<T>> read, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(read);
        await using var connection = await OpenAsync(cancellationToken);
        return await read(connection);
    }

    public static void Expect(int affected, int expected = 1)
    {
        if (affected != expected)
        {
            throw new DBConcurrencyException("Dashboard storage changed concurrently.");
        }
    }

    public static bool IsDuplicate(Exception error) =>
        error is SdkTerraBaseException { ErrorCode: TerraBaseSqlErrorCodes.UniqueViolation or TerraBaseSqlErrorCodes.PrimaryKeyViolation }
            or TerraBaseSqlException { Code: TerraBaseSqlErrorCodes.UniqueViolation or TerraBaseSqlErrorCodes.PrimaryKeyViolation };
}

public sealed record StorageWritten;

public sealed record StorageConflicted;

public sealed record StorageDuplicate;

public sealed record StorageFailed(string Failure);

public readonly union StorageWrite(StorageWritten, StorageConflicted, StorageDuplicate, StorageFailed);

public sealed class UtcDateTimeOffsetHandler : DbValueHandler<DateTimeOffset>
{
    private const string Format = "yyyy-MM-dd HH:mm:ss.FFFFFFF";

    protected override void Configure(DbParameter parameter)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        parameter.DbType = DbType.String;
    }

    protected override void SetValueCore(DbParameter parameter, DateTimeOffset value)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        parameter.Value = value.UtcDateTime.ToString(Format, CultureInfo.InvariantCulture);
    }

    protected override DateTimeOffset Parse(object? value) => value switch
    {
        DateTimeOffset offset => offset,
        DateTime time => new DateTimeOffset(DateTime.SpecifyKind(time, DateTimeKind.Utc)),
        string text when DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed) => parsed,
        _ => throw new FormatException("A stored timestamp is not a UTC date and time."),
    };
}
