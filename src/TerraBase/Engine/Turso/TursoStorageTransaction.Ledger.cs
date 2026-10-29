using Turso.Data.Sqlite;

namespace TerraBase.Engine.Turso;

internal sealed partial class TursoStorageTransaction
{

    private string? _copyFault;

    internal ValueTask<T> LedgerAsync<T>(Func<Func<string, SqliteCommand>, T> call, CancellationToken cancellationToken) =>
    RunAsync(() => call(PlainCommand), cancellationToken, mutation: write);

    internal static bool Damaged(Exception error) => error is SqliteException { SqliteErrorCode: 10 or 11 or 26 } || StoredDataCorruption.Is(error);

    private bool NoteDamage(Exception error)
    {
        if (Damaged(error))
        {
            NoteCopyFault(StoredDataCorruption.Is(error) ? "a stored value is corrupt" : "the file or its log reported an I/O error or corruption");
        }

        return false;
    }

    private void NoteCopyFault(string reason)
    {
        _copyFault ??= reason;
        store.NoteCopyFault(_copyFault);
    }
}
