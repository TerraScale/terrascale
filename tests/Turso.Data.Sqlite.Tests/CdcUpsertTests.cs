using System.Text.Json;

namespace Turso.Data.Sqlite.Tests;

/// <summary>Change capture in full mode records UPSERTs correctly (upstream Turso 26dbb464, native patch 0008).</summary>
public sealed class CdcUpsertTests
{
    /// <summary>An UPSERT updates the row, and its change record holds the old row before and the new row after.</summary>
    [Test]
    public async Task AnUpsertCapturedInFullModeKeepsTheRowAndRecordsTheOldRowAsItsBeforeImage()
    {
        using var database = new TemporaryDatabase();
        var connection = database.Connection;
        var key = Random.Shared.NextInt64(1, long.MaxValue);
        var old = "old-" + Guid.NewGuid().ToString("N");
        var updated = "new-" + Guid.NewGuid().ToString("N");
        using (var setup = connection.CreateCommand())
        {
            setup.CommandText = "CREATE TABLE u (k INTEGER PRIMARY KEY, v TEXT NOT NULL); PRAGMA capture_data_changes_conn('full');";
            setup.ExecuteNonQuery();
        }

        Execute(connection, "INSERT INTO u (k, v) VALUES ($k, $v);", key, old);
        Execute(connection, "INSERT INTO u (k, v) VALUES ($k, $v) ON CONFLICT(k) DO UPDATE SET v = excluded.v;", key, updated);

        using (var row = connection.CreateCommand())
        {
            row.CommandText = "SELECT v FROM u WHERE k = $k;";
            row.Parameters.AddWithValue("$k", key);
            await Assert.That(row.ExecuteScalar()).IsEqualTo(updated);
        }

        using var change = connection.CreateCommand();
        change.CommandText = "SELECT bin_record_json_object(table_columns_json_array('u'), before), bin_record_json_object(table_columns_json_array('u'), after) "
            + "FROM turso_cdc WHERE table_name = 'u' ORDER BY change_id DESC LIMIT 1;";
        using var images = change.ExecuteReader();
        await Assert.That(images.Read()).IsTrue();
        using var before = JsonDocument.Parse(images.GetString(0));
        using var after = JsonDocument.Parse(images.GetString(1));
        await Assert.That(before.RootElement.GetProperty("k").GetInt64()).IsEqualTo(key);
        await Assert.That(before.RootElement.GetProperty("v").GetString()).IsEqualTo(old);
        await Assert.That(after.RootElement.GetProperty("k").GetInt64()).IsEqualTo(key);
        await Assert.That(after.RootElement.GetProperty("v").GetString()).IsEqualTo(updated);
    }

    private static void Execute(SqliteConnection connection, string sql, long key, string value)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$k", key);
        command.Parameters.AddWithValue("$v", value);
        command.ExecuteNonQuery();
    }
}
