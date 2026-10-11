using System.Text;
using Turso.Data.Sqlite;

namespace TerraBase.Engine.Sqlite;

/// <summary>
/// turso-spike: stands in for the SQLitePCL raw calls the Turso provider cannot make. Turso.Data.Sqlite returns a null
/// <see cref="SqliteConnection.Handle"/> and the native C API has no interrupt, progress handler or heap limit, so
/// those become no-ops (docs/terrabase/turso-spike.md). Local errors carry code 1, so busy is matched by message.
/// </summary>
internal static class TursoShim
{
    public const int SQLITE_BUSY = 5;
    public const int SQLITE_LOCKED = 6;
    public const int SQLITE_INTERRUPT = 9;

    public static long SoftHeapLimit => 0;

    public static void BusyTimeout(SqliteConnection connection, int milliseconds)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA busy_timeout = {milliseconds};";
        command.ExecuteNonQuery();
    }

    public static bool IsBusy(SqliteException error) =>
        error.SqliteErrorCode is SQLITE_BUSY or SQLITE_LOCKED || error.Message.Contains("database is locked", StringComparison.Ordinal);

    // Microsoft.Data.Sqlite reads TEXT as its UTF-8 bytes; the Turso reader throws InvalidCastException.
    public static byte[] Bytes(SqliteDataReader reader, int ordinal) => reader.GetValue(ordinal) switch
    {
        byte[] bytes => bytes,
        string text => Encoding.UTF8.GetBytes(text),
        var other => throw new InvalidCastException($"column {ordinal} holds {other.GetType().Name}, not TEXT or BLOB"),
    };

    // Microsoft.Data.Sqlite ignores removing a function that is not registered; Turso throws "Not Found".
    public static void Unregister(SqliteConnection connection, string name)
    {
        try
        {
            connection.CreateFunction<long, long>(name, null);
        }
        catch (Exception error) when (error.Message.Contains("Not Found", StringComparison.Ordinal))
        {
        }
    }
}
