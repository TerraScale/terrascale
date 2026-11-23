using System.Diagnostics.CodeAnalysis;
using System.Data;
using System.Data.Common;

namespace TerraScale.DotNet.TerraBase;

/// <summary>
/// An ADO.NET connection to one <see cref="TerraBaseSqlDatabase"/>, for Dapper and other ADO.NET callers. Commands and
/// transactions share the database's tracked <c>TerraScale-Bookmark</c>, so every connection to it reads the writes the
/// others committed.
/// </summary>
public sealed class TerraBaseDbConnection(TerraBaseSqlDatabase database) : DbConnection
{
    private ConnectionState state = ConnectionState.Closed;

    internal TerraBaseSqlDatabase SqlDatabase { get; } = database;

    internal TerraBaseDbTransaction? CurrentTransaction { get; set; }

    /// <summary>
    /// The consistency of this connection's autocommit commands and of transactions begun with
    /// <see cref="IsolationLevel.Unspecified"/>; it starts as the database handle's
    /// <see cref="TerraBaseSqlDatabase.Consistency"/>, and null leaves it to the database's <c>defaultConsistency</c>.
    /// <see cref="IsolationLevel.Serializable"/> transactions are always <see cref="TerraBaseConsistencyMode.Strong"/>.
    /// </summary>
    public TerraBaseConsistencyMode? Consistency { get; set; } = database.Consistency;

    /// <summary>The newest <c>TerraScale-Bookmark</c> observed for the connection's database, or empty before the first answer.</summary>
    public string Bookmark => SqlDatabase.Bookmark;

    /// <summary>
    /// Merges a <c>TerraScale-Bookmark</c> from another process into the database's tracked bookmark; later commands observe
    /// it. Throws <see cref="ArgumentException"/> when <paramref name="bookmark"/> is not one TerraScale returned.
    /// </summary>
    public void ObserveBookmark(string bookmark) => SqlDatabase.ObserveBookmark(bookmark);

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

    /// <summary>
    /// Closes the connection. An open transaction is aborted: its socket closes at once and the server rolls it back.
    /// Never throws, so a synchronous dispose cannot mask the caller's own exception; <see cref="CloseAsync"/> rolls back
    /// gracefully.
    /// </summary>
    public override void Close()
    {
        CurrentTransaction?.Abort();
        state = ConnectionState.Closed;
    }

    /// <summary>Aborts an open transaction like <see cref="Close"/>.</summary>
    protected override void Dispose(bool disposing)
    {
        CurrentTransaction?.Abort();
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

    /// <summary>
    /// Opens a SQL WebSocket session, begins a transaction on it and returns the wrapping <see cref="TerraBaseDbTransaction"/>.
    /// <see cref="IsolationLevel.Serializable"/> begins a strong transaction (<c>BEGIN STRONG</c>);
    /// <see cref="IsolationLevel.Unspecified"/> begins one with the connection's <see cref="Consistency"/>.
    /// </summary>
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

    /// <summary>Creates a command bound to this connection.</summary>
    protected override DbCommand CreateDbCommand() => new TerraBaseDbCommand(this);
}
