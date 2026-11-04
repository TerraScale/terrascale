using System.Text.Json;
using Bogus;

namespace Turso.Data.Sqlite.Tests;

public sealed class CdcUpsertTests
{
    private readonly Faker _faker = new();

    [Test]
    public async Task AnUpsertCapturedInFullModeKeepsTheRowAndRecordsTheOldRowAsItsBeforeImage()
    {
        using var database = new TemporaryDatabase();
        var connection = database.Connection;
        var key = _faker.Random.Long(1, long.MaxValue);
        var old = _faker.Lorem.Sentence();
        var updated = _faker.Lorem.Sentence();
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
