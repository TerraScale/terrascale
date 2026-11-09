using System.Data;
using System.Data.Common;
using System.Net.WebSockets;

namespace TerraScale.DotNet.TerraBase;

/// <summary>One transaction over a SQL WebSocket session, from <see cref="DbConnection.BeginTransactionAsync(CancellationToken)"/>.</summary>
public sealed class TerraBaseDbTransaction : DbTransaction
{
    private static readonly TimeSpan RollbackBound = TimeSpan.FromSeconds(5);
    private readonly TerraBaseDbConnection connection;
    private readonly TerraBaseSqlSession session;
    private bool finished;

    internal TerraBaseDbTransaction(TerraBaseDbConnection connection, TerraBaseSqlSession session)
    {
        this.connection = connection;
        this.session = session;
    }

    /// <summary>The connection this transaction runs on.</summary>
    protected override DbConnection DbConnection => connection;

    /// <summary>Always <see cref="System.Data.IsolationLevel.Serializable"/>: transactions run at the database's strongest isolation.</summary>
    public override IsolationLevel IsolationLevel => IsolationLevel.Serializable;

    internal TerraBaseSqlSession Session => session;

    /// <summary>Commits the transaction, then closes the session even when the commit throws.</summary>
    public override Task CommitAsync(CancellationToken cancellationToken) => FinishAsync(session.CommitAsync, cancellationToken);

    /// <summary>Rolls the transaction back, then closes the session even when the rollback throws.</summary>
    public override Task RollbackAsync(CancellationToken cancellationToken) => FinishAsync(session.RollbackAsync, cancellationToken);

    /// <summary>
    /// Rolls back unless the transaction already committed or rolled back, then closes the session. The rollback waits at
    /// most 5 seconds, TerraBase's transaction limit; after that the socket is aborted and the server rolls back.
    /// </summary>
    public override async ValueTask DisposeAsync()
    {
        if (finished) return;
        using var bound = new CancellationTokenSource(RollbackBound);
        try
        {
            await RollbackAsync(bound.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is TerraBaseException or WebSocketException or TimeoutException or OperationCanceledException)
        {
        }
    }

    /// <summary>The SDK is async-only; use <see cref="CommitAsync(CancellationToken)"/>.</summary>
    public override void Commit() => throw NotSync();

    /// <summary>The SDK is async-only; use <see cref="RollbackAsync(CancellationToken)"/>.</summary>
    public override void Rollback() => throw NotSync();

    /// <summary>
    /// Aborts an open transaction without waiting: the session's socket closes at once and the server rolls the
    /// transaction back. Never throws, so a synchronous dispose cannot mask the caller's own exception.
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing) Abort();
        base.Dispose(disposing);
    }

    internal void Abort()
    {
        if (finished) return;
        finished = true;
        connection.CurrentTransaction = null;
        session.Abort();
    }

    private async Task FinishAsync(Func<CancellationToken, Task> finish, CancellationToken cancellationToken)
    {
        if (finished) throw new InvalidOperationException("The transaction already committed or rolled back.");
        finished = true;
        try
        {
            await finish(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            session.Abort();
            throw;
        }
        finally
        {
            connection.CurrentTransaction = null;
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static NotSupportedException NotSync() => new("The TerraScale SDK is async-only; use the async method with a CancellationToken.");
}
