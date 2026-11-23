using System.Data;
using System.Data.Common;
using System.Globalization;
using Dapper;
using TerraScale.DotNet.TerraBase;
using SdkTerraBaseException = TerraScale.DotNet.TerraBase.TerraBaseException;

[module: DapperAot]
[module: TypeHandler(typeof(DateTimeOffset), typeof(TerraScale.Dashboard.Platform.Persistence.UtcDateTimeOffsetHandler))]

namespace TerraScale.Dashboard.Platform.Persistence;

public sealed class DashboardDb(TerraBaseSqlDatabase database)
{
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
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await work(connection, transaction);
            await transaction.CommitAsync(cancellationToken);
        });
    }

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
