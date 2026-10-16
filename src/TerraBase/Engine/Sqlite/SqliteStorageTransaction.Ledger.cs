using Turso.Data.Sqlite;

namespace TerraBase.Engine.Sqlite;

/// <summary>The statements of <see cref="RebuildLedger"/>, which only the engine runs.</summary>
internal sealed partial class SqliteStorageTransaction
{
    // Set when this transaction may have left its file uncertain or damaged: recorded as a copy fault before the writer is
    // released (docs/terrabase/decisions/0013a, section 1.3).
    private string? _copyFault;

    /// <summary>
    /// Runs <paramref name="call"/> with a factory of commands bound to this transaction; each command is the caller's to
    /// dispose. A failure of a write transaction leaves it able only to roll back.
    /// </summary>
    internal ValueTask<T> LedgerAsync<T>(Func<Func<string, SqliteCommand>, T> call, CancellationToken cancellationToken) =>
        RunAsync(() => call(PlainCommand), cancellationToken, mutation: write);

    // SQLITE_IOERR, SQLITE_CORRUPT and SQLITE_NOTADB: the file or its log may not hold what was written; a stored value
    // that does not decode says the same. Every other failure (a constraint, a busy writer, a cancelled read, a refused
    // value, a received state that does not decode) says nothing about the file.
    internal static bool Damaged(Exception error) => error is SqliteException { SqliteErrorCode: 10 or 11 or 26 } || StoredDataCorruption.Is(error);

    // An exception filter that never catches: it notes damage on the way out.
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
