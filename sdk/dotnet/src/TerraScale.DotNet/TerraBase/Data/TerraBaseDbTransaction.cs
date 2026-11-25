using System.Data;
using System.Data.Common;
using System.Net.WebSockets;

namespace TerraScale.DotNet.TerraBase;

public sealed class TerraBaseDbTransaction : DbTransaction
{
    private static readonly TimeSpan RollbackBound = TimeSpan.FromSeconds(5);
    private readonly TerraBaseDbConnection connection;
    private readonly TerraBaseSqlSession session;
    private bool finished;

    internal TerraBaseDbTransaction(TerraBaseDbConnection connection, TerraBaseSqlSession session, TerraBaseConsistencyMode? consistency)
    {
        this.connection = connection;
        this.session = session;
        Consistency = consistency;
    }

    protected override DbConnection DbConnection => connection;

    public TerraBaseConsistencyMode? Consistency { get; }

    public override IsolationLevel IsolationLevel => Consistency == TerraBaseConsistencyMode.Strong ? IsolationLevel.Serializable : IsolationLevel.Unspecified;

    internal TerraBaseSqlSession Session => session;

    public override Task CommitAsync(CancellationToken cancellationToken) => FinishAsync(session.CommitAsync, cancellationToken);

    public override Task RollbackAsync(CancellationToken cancellationToken) => FinishAsync(session.RollbackAsync, cancellationToken);

    public override async ValueTask DisposeAsync()
    {
        if (finished) return;
        using var bound = new CancellationTokenSource(RollbackBound);
        try
        {
            await RollbackAsync(bound.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is TerraBaseException or WebSocketException or TimeoutException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    public override void Commit() => throw NotSync();

    public override void Rollback() => throw NotSync();

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
