using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Turso.Data.Sqlite;

const int rowCount = 10_000;
const int repeats = 3;
var directory = Directory.CreateTempSubdirectory("database-encryption-").FullName;
var payload = new string('x', 1024);
Console.WriteLine($"Host={Environment.MachineName}; runtime={Environment.Version}; rows={rowCount}; payload=1024 ASCII bytes; pooling=false; warm filesystem cache; milliseconds");
Console.WriteLine("| Repeat | Cipher | Write ms | Read ms | Checkpoint ms | Reopen ms | DB bytes | WAL bytes before checkpoint |");
Console.WriteLine("| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |");
try
{
    for (var repeat = 1; repeat <= repeats; repeat++)
    {
        // Alternate order to avoid always favoring the second cipher with a warm runtime.
        string[] ciphers = repeat % 2 == 0 ? ["aegis256", "plain"] : ["plain", "aegis256"];
        foreach (var cipher in ciphers)
        {
            var path = Path.Combine(directory, $"{repeat}-{cipher}.db");
            var builder = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false };
            if (cipher == "aegis256")
            {
                builder.EncryptionCipher = cipher;
                builder.EncryptionKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            }

            double writeMs;
            double readMs;
            double checkpointMs;
            long walBytes;
            using (var connection = new SqliteConnection(builder.ConnectionString))
            {
                connection.Open();
                using var setup = connection.CreateCommand();
                setup.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA wal_autocheckpoint=0; CREATE TABLE documents(id TEXT PRIMARY KEY, payload TEXT NOT NULL);";
                setup.ExecuteNonQuery();
                using var write = connection.CreateCommand();
                write.CommandText = "INSERT INTO documents VALUES($id,$payload);";
                write.Parameters.AddWithValue("$id", string.Empty);
                write.Parameters.AddWithValue("$payload", payload);
                write.Prepare();
                var clock = Stopwatch.StartNew();
                using (var transaction = connection.BeginTransaction())
                {
                    write.Transaction = transaction;
                    for (var index = 0; index < rowCount; index++)
                    {
                        write.Parameters["$id"].Value = index.ToString("D26", CultureInfo.InvariantCulture);
                        write.ExecuteNonQuery();
                    }
                    transaction.Commit();
                }
                writeMs = clock.Elapsed.TotalMilliseconds;
                using var read = connection.CreateCommand();
                read.CommandText = "SELECT payload FROM documents WHERE id=$id;";
                read.Parameters.AddWithValue("$id", string.Empty);
                read.Prepare();
                clock.Restart();
                for (var index = 0; index < rowCount; index++)
                {
                    read.Parameters["$id"].Value = index.ToString("D26", CultureInfo.InvariantCulture);
                    if (read.ExecuteScalar() as string != payload)
                    {
                        throw new InvalidOperationException("Read returned incorrect benchmark payload.");
                    }
                }
                readMs = clock.Elapsed.TotalMilliseconds;
                walBytes = new FileInfo(path + "-wal").Length;
                clock.Restart();
                setup.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                using (var checkpoint = setup.ExecuteReader())
                {
                    if (!checkpoint.Read() || checkpoint.GetInt64(0) != 0)
                    {
                        throw new InvalidOperationException("Benchmark checkpoint did not complete.");
                    }
                }
                checkpointMs = clock.Elapsed.TotalMilliseconds;
            }

            var reopenClock = Stopwatch.StartNew();
            using (var reopened = new SqliteConnection(builder.ConnectionString))
            {
                reopened.Open();
                using var count = reopened.CreateCommand();
                count.CommandText = "SELECT count(*) FROM documents;";
                if (Convert.ToInt64(count.ExecuteScalar(), CultureInfo.InvariantCulture) != rowCount)
                {
                    throw new InvalidOperationException("Reopened database lost benchmark rows.");
                }
            }
            var reopenMs = reopenClock.Elapsed.TotalMilliseconds;
            Console.WriteLine(FormattableString.Invariant($"| {repeat} | {cipher} | {writeMs:F3} | {readMs:F3} | {checkpointMs:F3} | {reopenMs:F3} | {new FileInfo(path).Length} | {walBytes} |"));
        }
    }
}
finally
{
    Directory.Delete(directory, recursive: true);
}
