using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using Turso.Data.Sqlite;

var repeats = args.Length > 0 ? int.Parse(args[0], CultureInfo.InvariantCulture) : 3;
ArgumentOutOfRangeException.ThrowIfLessThan(repeats, 3);
var directory = Path.Combine(Path.GetTempPath(), "repair-digest-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    Console.WriteLine($"Host={Environment.MachineName}; runtime={Environment.Version}; repeats={repeats}; warm cache; milliseconds");
    foreach (var count in new[] { 100_000, 1_000_000 })
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(directory, count + ".db")}");
        connection.Open();
        Execute("CREATE TABLE _tb_state (collection TEXT NOT NULL, id TEXT NOT NULL, bucket INTEGER NOT NULL, state TEXT NOT NULL, stamp INTEGER NOT NULL, origin INTEGER NOT NULL, live INTEGER NOT NULL, h1 INTEGER NOT NULL, h2 INTEGER NOT NULL, UNIQUE(collection,id)); CREATE INDEX _tb_state_bucket ON _tb_state(bucket,collection,id); CREATE TABLE _tb_merkle(bucket INTEGER PRIMARY KEY,h1 INTEGER NOT NULL,h2 INTEGER NOT NULL);");
        var folded = new long[4096 * 2];
        using (var transaction = connection.BeginTransaction())
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO _tb_state VALUES('items',$id,$bucket,$state,65536,1,1,$h1,$h2);";
            insert.Parameters.AddWithValue("$id", "");
            insert.Parameters.AddWithValue("$bucket", 0L);
            insert.Parameters.AddWithValue("$state", new string('s', 128));
            insert.Parameters.AddWithValue("$h1", 0L);
            insert.Parameters.AddWithValue("$h2", 0L);
            for (var index = 0; index < count; index++)
            {
                var bucket = index % 4096;
                var h1 = unchecked(index * 6364136223846793005L);
                var h2 = unchecked(index * -7046029254386353131L);
                insert.Parameters["$id"].Value = "000000000000000000" + index.ToString("D8", CultureInfo.InvariantCulture);
                insert.Parameters["$bucket"].Value = (long)bucket;
                insert.Parameters["$h1"].Value = h1;
                insert.Parameters["$h2"].Value = h2;
                insert.ExecuteNonQuery();
                folded[bucket * 2] ^= h1;
                folded[bucket * 2 + 1] ^= h2;
            }
            transaction.Commit();
        }
        using (var transaction = connection.BeginTransaction())
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO _tb_merkle VALUES($bucket,$h1,$h2);";
            insert.Parameters.AddWithValue("$bucket", 0L);
            insert.Parameters.AddWithValue("$h1", 0L);
            insert.Parameters.AddWithValue("$h2", 0L);
            for (var bucket = 0; bucket < 4096; bucket++)
            {
                insert.Parameters["$bucket"].Value = (long)bucket;
                insert.Parameters["$h1"].Value = folded[bucket * 2];
                insert.Parameters["$h2"].Value = folded[bucket * 2 + 1];
                insert.ExecuteNonQuery();
            }
            transaction.Commit();
        }
        var baseline = Read("_tb_state");
        if (!baseline.SequenceEqual(Read("_tb_merkle")))
        {
            throw new InvalidDataException("folded digests differ");
        }
        for (var repeat = 0; repeat < repeats; repeat++)
        {
            foreach (var table in new[] { "_tb_state", "_tb_merkle" })
            {
                var started = Stopwatch.GetTimestamp();
                var digest = Read(table);
                var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                if (!digest.SequenceEqual(baseline))
                {
                    throw new InvalidDataException("measured digest differs");
                }
                Console.WriteLine(FormattableString.Invariant($"{count},{table},{repeat + 1},{elapsed:F3}"));
            }
        }

        byte[] Read(string table)
        {
            var digests = new byte[4096 * 16];
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT CASE WHEN typeof(bucket) = 'integer' AND bucket >= 0 AND bucket < 4096
                    AND typeof(h1) = 'integer' AND typeof(h2) = 'integer' THEN 1 ELSE 0 END,
                    CASE WHEN typeof(bucket) = 'integer' THEN bucket ELSE 0 END,
                    CASE WHEN typeof(h1) = 'integer' THEN h1 ELSE 0 END,
                    CASE WHEN typeof(h2) = 'integer' THEN h2 ELSE 0 END FROM {table};
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (reader.GetInt64(0) != 1)
                {
                    throw new InvalidDataException("invalid stored scalar");
                }
                var slot = digests.AsSpan((int)reader.GetInt64(1) * 16, 16);
                BinaryPrimitives.WriteInt64LittleEndian(slot, BinaryPrimitives.ReadInt64LittleEndian(slot) ^ reader.GetInt64(2));
                BinaryPrimitives.WriteInt64LittleEndian(slot[8..], BinaryPrimitives.ReadInt64LittleEndian(slot[8..]) ^ reader.GetInt64(3));
            }
            return digests;
        }

        void Execute(string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
    }
}
finally
{
    Directory.Delete(directory, recursive: true);
}
