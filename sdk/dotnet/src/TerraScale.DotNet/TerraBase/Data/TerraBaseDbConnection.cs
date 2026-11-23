using System.Diagnostics.CodeAnalysis;
using System.Data;
using System.Data.Common;

namespace TerraScale.DotNet.TerraBase;

public sealed class TerraBaseDbConnection(TerraBaseSqlDatabase database) : DbConnection
{
    private ConnectionState state = ConnectionState.Closed;

    internal TerraBaseSqlDatabase SqlDatabase { get; } = database;

    internal TerraBaseDbTransaction? CurrentTransaction { get; set; }

    public TerraBaseConsistencyMode? Consistency { get; set; } = database.Consistency;

    public string Bookmark => SqlDatabase.Bookmark;

    public void ObserveBookmark(string bookmark) => SqlDatabase.ObserveBookmark(bookmark);

    [AllowNull]
    public override string ConnectionString
    {
        get => "";

        set => throw new NotSupportedException("Build the connection from a TerraBaseSqlDatabase instead of a connection string.");
    }

    public override string Database => "";

    public override string DataSource => "";

    public override string ServerVersion => throw new NotSupportedException("The server version is not part of the SQL API.");

    public override ConnectionState State => state;

    public override void ChangeDatabase(string databaseName) => throw new NotSupportedException("Create another connection for another database.");

    public override void Close()
    {
        CurrentTransaction?.Abort();
        state = ConnectionState.Closed;
    }

    protected override void Dispose(bool disposing)
    {
        CurrentTransaction?.Abort();
        base.Dispose(disposing);
    }

    public override async Task CloseAsync()
    {
        if (CurrentTransaction is { } open) await open.DisposeAsync().ConfigureAwait(false);
        state = ConnectionState.Closed;
    }

    public override async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    public override void Open() => state = ConnectionState.Open;

    public override Task OpenAsync(CancellationToken cancellationToken)
    {
        state = ConnectionState.Open;
        return Task.CompletedTask;
    }

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
        throw new NotSupportedException("The TerraScale SDK is async-only; use the async method with a CancellationToken.");

    protected override async ValueTask<DbTransaction> BeginDbTransactionAsync(IsolationLevel isolationLevel, CancellationToken cancellationToken)
    {
        var consistency = isolationLevel switch
        {
            IsolationLevel.Serializable => TerraBaseConsistencyMode.Strong,
            IsolationLevel.Unspecified => Consistency,
            _ => throw new NotSupportedException($"Only {nameof(IsolationLevel.Unspecified)} and {nameof(IsolationLevel.Serializable)} are supported."),
        };

        if (CurrentTransaction is not null) throw new InvalidOperationException("The connection already has an open transaction.");
        var session = await SqlDatabase.WithConsistency(consistency).ConnectAsync(cancellationToken).ConfigureAwait(false);
        TerraBaseDbTransaction transaction;
        try
        {
            await (consistency is { } mode ? session.BeginAsync(mode, cancellationToken) : session.BeginAsync(cancellationToken)).ConfigureAwait(false);
            transaction = new(this, session, consistency);
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        CurrentTransaction = transaction;
        return transaction;
    }

    protected override DbCommand CreateDbCommand() => new TerraBaseDbCommand(this);
}
