using System.Diagnostics.CodeAnalysis;
using System.Data;
using System.Data.Common;

namespace TerraScale.DotNet.TerraBase;

/// <summary>An ADO.NET connection to one <see cref="TerraBaseSqlDatabase"/>, for Dapper and other ADO.NET callers.</summary>
public sealed class TerraBaseDbConnection(TerraBaseSqlDatabase database) : DbConnection
{
    private ConnectionState state = ConnectionState.Closed;

    /// <summary>The database every command of this connection runs against.</summary>
    internal TerraBaseSqlDatabase SqlDatabase { get; } = database;

    /// <summary>The connection's open transaction, or null outside one.</summary>
    internal TerraBaseDbTransaction? CurrentTransaction { get; set; }

    /// <summary>Always empty; the connection is built from a <see cref="TerraBaseSqlDatabase"/>, not a connection string.</summary>
    [AllowNull]
    public override string ConnectionString
    {
        get => "";

        set => throw new NotSupportedException("Build the connection from a TerraBaseSqlDatabase instead of a connection string.");
    }

    /// <summary>Always empty.</summary>
    public override string Database => "";

    /// <summary>Always empty.</summary>
    public override string DataSource => "";

    /// <summary>Not part of the SQL API.</summary>
    public override string ServerVersion => throw new NotSupportedException("The server version is not part of the SQL API.");

    /// <summary>The connection state.</summary>
    public override ConnectionState State => state;

    /// <summary>Unsupported: one connection addresses one database.</summary>
    public override void ChangeDatabase(string databaseName) => throw new NotSupportedException("Create another connection for another database.");

    /// <summary>Closes the connection. With an open transaction it throws: the SDK is async-only, so only <see cref="CloseAsync"/> can roll it back.</summary>
    public override void Close()
    {
        if (CurrentTransaction is not null)
        {
            throw new NotSupportedException("The TerraScale SDK is async-only; use CloseAsync or DisposeAsync so the open transaction rolls back.");
        }

        state = ConnectionState.Closed;
    }

    /// <summary>
    /// Detaches an open transaction without rolling it back here — the server ends it at its timeout; the session is
    /// async-only and is not blocked on. Never throws, so a sync dispose cannot mask the caller's own exception.
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (CurrentTransaction is { } open)
        {
            CurrentTransaction = null;
            open.Abandon();
        }

        base.Dispose(disposing);
    }

    /// <summary>Closes the connection asynchronously, rolling back an open transaction first.</summary>
    public override async Task CloseAsync()
    {
        if (CurrentTransaction is { } open) await open.DisposeAsync().ConfigureAwait(false);
        state = ConnectionState.Closed;
    }

    /// <summary>Closes the connection asynchronously; the base dispose keeps component semantics.</summary>
    public override async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Marks the connection open. Opening performs no network work.</summary>
    public override void Open() => state = ConnectionState.Open;

    /// <summary>Marks the connection open. Opening performs no network work.</summary>
    public override Task OpenAsync(CancellationToken cancellationToken)
    {
        state = ConnectionState.Open;
        return Task.CompletedTask;
    }

    /// <summary>The SDK is async-only; use <see cref="DbConnection.BeginTransactionAsync(CancellationToken)"/>.</summary>
    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
        throw new NotSupportedException("The TerraScale SDK is async-only; use the async method with a CancellationToken.");

    /// <summary>Opens a SQL WebSocket session, begins a transaction on it and returns the wrapping <see cref="TerraBaseDbTransaction"/>.</summary>
    protected override async ValueTask<DbTransaction> BeginDbTransactionAsync(IsolationLevel isolationLevel, CancellationToken cancellationToken)
    {
        if (isolationLevel is not (IsolationLevel.Unspecified or IsolationLevel.Serializable))
        {
            throw new NotSupportedException($"Only {nameof(IsolationLevel.Unspecified)} and {nameof(IsolationLevel.Serializable)} are supported.");
        }

        if (CurrentTransaction is not null) throw new InvalidOperationException("The connection already has an open transaction.");
        var session = await SqlDatabase.ConnectAsync(cancellationToken).ConfigureAwait(false);
        TerraBaseDbTransaction transaction;
        try
        {
            await session.BeginAsync(cancellationToken).ConfigureAwait(false);
            transaction = new(this, session);
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        CurrentTransaction = transaction;
        return transaction;
    }

    /// <summary>Creates a command bound to this connection.</summary>
    protected override DbCommand CreateDbCommand() => new TerraBaseDbCommand(this);
}
