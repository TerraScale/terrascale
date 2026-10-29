namespace Turso.Data.Sqlite.Tests;

/// <summary>
/// A local error keeps the SQLite code of Turso's native status when it reports damage: SQLITE_CORRUPT (11) for a
/// corrupt page and SQLITE_NOTADB (26) for a file that is not a database. Every other error stays SQLITE_ERROR (1).
/// </summary>
public sealed class DamageErrorCodeTests
{
    /// <summary>Syntax errors and constraint violations are no damage: they stay SQLITE_ERROR.</summary>
    [Test]
    [Arguments("SELEC 1;")]
    [Arguments("INSERT INTO t VALUES ('a');")]
    public async Task OtherErrorsStaySqliteError(string sql)
    {
        using var database = new TemporaryDatabase();
        using var statement = database.Connection.CreateCommand();
        statement.CommandText = sql;

        var error = Assert.Throws<SqliteException>(() => statement.ExecuteNonQuery());

        await Assert.That(error.SqliteErrorCode).IsEqualTo(1);
    }
}
