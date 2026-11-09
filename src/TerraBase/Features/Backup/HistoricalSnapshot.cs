using TerraBase.Engine;
using TerraBase.Errors;

namespace TerraBase.Features.Backup;

public sealed class HistoricalSnapshot(IRecoverySnapshot reader, IDisposable scratch) : IAsyncDisposable
{
    public IStorageTransaction Transaction => reader.Transaction;

    public async ValueTask DisposeAsync()
    {
        try
        {
            await reader.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            scratch.Dispose();
        }
    }
}

public readonly union HistoricalSnapshotResult(HistoricalSnapshot, NotFound, Conflict, Validation, Unavailable);
