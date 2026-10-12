namespace Turso.Data.Sqlite.Tests;

/// <summary>A database file of its own with table t (one row, 'a', of a unique id), deleted afterwards.</summary>
internal sealed class TemporaryDatabase : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("turso-driver-").FullName;

    public TemporaryDatabase()
    {
        Connection = new SqliteConnection($"Data Source={Path.Combine(_directory, "t.db")}");
        Connection.Open();
        using var create = Connection.CreateCommand();
        create.CommandText = "CREATE TABLE t (id TEXT NOT NULL UNIQUE); INSERT INTO t VALUES ('a');";
        create.ExecuteNonQuery();
    }

    public SqliteConnection Connection { get; }

    public void Dispose()
    {
        Connection.Dispose();
        Directory.Delete(_directory, recursive: true);
    }
}
