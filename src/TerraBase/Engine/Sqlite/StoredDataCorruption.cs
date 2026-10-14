namespace TerraBase.Engine.Sqlite;

/// <summary>
/// Marks an <see cref="InvalidDataException"/> (sealed, so not subclassed) as a value this node's file stored that does
/// not decode: the file is damaged, and the transaction that read it faults its copy (<see cref="RebuildLedger"/>). A
/// received value that does not decode stays unmarked, so wire validation never faults a healthy file.
/// </summary>
internal static class StoredDataCorruption
{
    private const string Marker = "terrabase.stored-data-corrupt";

    /// <summary>A marked exception, for a stored value that does not decode.</summary>
    public static InvalidDataException Create(string message, Exception? inner = null)
    {
        var error = new InvalidDataException(message, inner);
        error.Data[Marker] = true;
        return error;
    }

    /// <summary>Whether <paramref name="error"/> was made by <see cref="Create"/>.</summary>
    public static bool Is(Exception error) => error is InvalidDataException && error.Data[Marker] is true;
}
