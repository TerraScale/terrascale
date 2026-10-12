using Turso.Data.Sqlite;

namespace TerraBase.Engine.Sqlite;

/// <summary>
/// A transaction on one connection, its control statements run through <see cref="TursoStatements"/> in place of the
/// provider's <c>SqliteTransaction</c>, whose failed statements the provider would abandon. Disposing it rolls back
/// what it did not commit.
/// </summary>
internal sealed class TursoTransaction : IDisposable
{
    private readonly SqliteConnection _connection;
    private bool _ended;

    private TursoTransaction(SqliteConnection connection)
    {
        _connection = connection;
    }

    /// <summary>Begins a transaction: a writer takes the write lock up front (<c>BEGIN IMMEDIATE</c>), a reader when it reads.</summary>
    public static TursoTransaction Begin(SqliteConnection connection, bool deferred)
    {
        connection.Run(deferred ? "BEGIN;" : "BEGIN IMMEDIATE;");
        return new TursoTransaction(connection);
    }

    public void Commit()
    {
        _connection.Run("COMMIT;");
        _ended = true;
    }

    public void Save(string savepoint) => _connection.Run($"SAVEPOINT \"{savepoint}\";");

    public void Rollback(string savepoint) => _connection.Run($"ROLLBACK TO SAVEPOINT \"{savepoint}\";");

    public void Release(string savepoint) => _connection.Run($"RELEASE SAVEPOINT \"{savepoint}\";");

    public void Dispose()
    {
        if (!_ended)
        {
            _ended = true;
            _connection.Run("ROLLBACK;");
        }
    }
}
