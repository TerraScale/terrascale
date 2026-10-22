using TerraBase.Engine.Sqlite;
using Turso.Data.Sqlite;

namespace TerraBase.Testing;

// Direct file-damage fixtures that change the logical rows also change their derived bucket summaries.
internal static class MerkleFixture
{
    internal static void Rebuild(SqliteSession session, SqliteTransaction transaction)
    {
        var hashes = new long[4096 * 2];
        using var command = session.Connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT bucket, h1, h2 FROM _tb_state;";
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var slot = checked((int)reader.GetInt64(0)) * 2;
                hashes[slot] ^= reader.GetInt64(1);
                hashes[slot + 1] ^= reader.GetInt64(2);
            }
        }

        command.CommandText = "DELETE FROM _tb_merkle;";
        command.ExecuteNonQuery();
        command.CommandText = "INSERT INTO _tb_merkle (bucket, h1, h2) VALUES ($bucket, $h1, $h2);";
        command.Parameters.AddWithValue("$bucket", 0L);
        command.Parameters.AddWithValue("$h1", 0L);
        command.Parameters.AddWithValue("$h2", 0L);
        for (var bucket = 0; bucket < 4096; bucket++)
        {
            if (hashes[bucket * 2] == 0 && hashes[bucket * 2 + 1] == 0)
            {
                continue;
            }

            command.Parameters["$bucket"].Value = (long)bucket;
            command.Parameters["$h1"].Value = hashes[bucket * 2];
            command.Parameters["$h2"].Value = hashes[bucket * 2 + 1];
            command.ExecuteNonQuery();
        }
    }
}
