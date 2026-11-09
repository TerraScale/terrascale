using System.Data;
using System.Data.Common;
using System.Net.WebSockets;

namespace TerraScale.DotNet.TerraBase;

/// <summary>One transaction over a SQL WebSocket session, from <see cref="DbConnection.BeginTransactionAsync(CancellationToken)"/>.</summary>
public sealed class TerraBaseDbTransaction : DbTransaction
{
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

    /// <summary>Rolls back unless the transaction already committed or rolled back, then closes the session.</summary>
    public override async ValueTask DisposeAsync()
    {
        if (finished) return;
        try
        {
            await RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error) when (error is TerraBaseException or WebSocketException or TimeoutException)
        {
        }
    }

    /// <summary>The SDK is async-only; use <see cref="CommitAsync(CancellationToken)"/>.</summary>
    public override void Commit() => throw NotSync();

    /// <summary>The SDK is async-only; use <see cref="RollbackAsync(CancellationToken)"/>.</summary>
    public override void Rollback() => throw NotSync();

    internal void Abandon() => finished = true;

    private async Task FinishAsync(Func<CancellationToken, Task> finish, CancellationToken cancellationToken)
    {
        if (finished) throw new InvalidOperationException("The transaction already committed or rolled back.");
        finished = true;
        try
        {
            await finish(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            connection.CurrentTransaction = null;
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static NotSupportedException NotSync() => new("The TerraScale SDK is async-only; use the async method with a CancellationToken.");
}
