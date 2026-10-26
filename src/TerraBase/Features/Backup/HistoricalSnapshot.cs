using TerraBase.Engine;
using TerraBase.Errors;

namespace TerraBase.Features.Backup;

/// <summary>An isolated historical snapshot; disposing closes native readers before deleting all scratch artifacts.</summary>
public sealed class HistoricalSnapshot(IRecoverySnapshot reader, IDisposable scratch) : IAsyncDisposable
{
    /// <summary>The historical read transaction, never a tenant database.</summary>
    public IStorageTransaction Transaction => reader.Transaction;

    /// <inheritdoc />
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

/// <summary>A readable scratch snapshot, or the same validation failures as a whole-database PITR.</summary>
public readonly union HistoricalSnapshotResult(HistoricalSnapshot, NotFound, Conflict, Validation, Unavailable);
