using System.Buffers.Binary;
using System.Buffers.Text;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Engine.Lattices;
using TerraBase.Features.Replication;
using TerraBase.Tenancy;
using Turso.Data.Sqlite;

internal static class AntiEntropyDigestBench
{
    internal static async Task RunAsync(string[] args)
    {
        if (args.Length != 3 || !int.TryParse(args[2], CultureInfo.InvariantCulture, out var count)
            || count is not (100_000 or 1_000_000))
        {
            throw new ArgumentException("Usage: anti-entropy[-pull] <new-data-directory> <100000|1000000>", nameof(args));
        }

        var data = Path.GetFullPath(args[1]);
        var seed = !Directory.Exists(data);
        var tenant = new TenantContext("_system", PlanTier.Hobby, "_system", "_system");
        var options = new TerraBaseOptions { DataDirectory = data, MemoryBudget = 128 * 1024 * 1024, StorageThreads = 2, StorageQueue = 1024 };
        if (seed)
        {
            await using var setup = new ServiceCollection().AddTerraBaseEngine(options).BuildServiceProvider();
            _ = await setup.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(CancellationToken.None).ConfigureAwait(false);
        }

        var expected = new byte[4096 * 16];
        var bucketOf = typeof(DocumentStateEncoding).GetMethod("Bucket", BindingFlags.NonPublic | BindingFlags.Static)!
            .CreateDelegate<Func<string, string, int>>();
        using var json = JsonDocument.Parse("""{"value":1}""");
        var state = DocumentState.Bottom.Put(new EntryVersion(new Hlc(1), new ReplicaId(12345)), json.RootElement);
        var stored = Base64Url.EncodeToString(DocumentStateEncoding.EncodeStored(state));
        var databasePath = Path.Combine(data, "_system.db");
        using (var connection = new SqliteConnection($"Data Source={databasePath}"))
        {
            connection.Open();
            using var transaction = connection.BeginTransaction();
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO _tb_state (collection, id, bucket, state, stamp, origin, live, h1, h2)
                VALUES ('items', $id, $bucket, $state, 1, 12345, 1, $h1, $h2);
                INSERT INTO documents (collection, id, body) VALUES ('items', $id, '{"value":1}');
                """;
            var idParameter = insert.Parameters.AddWithValue("$id", "");
            var bucketParameter = insert.Parameters.AddWithValue("$bucket", 0L);
            _ = insert.Parameters.AddWithValue("$state", stored);
            var h1Parameter = insert.Parameters.AddWithValue("$h1", 0L);
            var h2Parameter = insert.Parameters.AddWithValue("$h2", 0L);
            for (var i = 0; i < count; i++)
            {
                var id = i.ToString("D26", CultureInfo.InvariantCulture);
                var hash = DocumentStateEncoding.Digest("items", id, state);
                var bucket = bucketOf("items", id);
                var h1 = unchecked((long)(ulong)(hash >> 64));
                var h2 = unchecked((long)(ulong)hash);
                idParameter.Value = id;
                bucketParameter.Value = (long)bucket;
                h1Parameter.Value = h1;
                h2Parameter.Value = h2;
                if (seed)
                {
                    _ = insert.ExecuteNonQuery();
                }
                Fold(expected, bucket, h1, h2);
            }

            transaction.Commit();
            using var verify = connection.CreateCommand();
            verify.CommandText = "SELECT (SELECT count(*) FROM _tb_state), (SELECT count(*) FROM documents);";
            using var reader = verify.ExecuteReader();
            if (!reader.Read() || reader.GetInt64(0) != count || reader.GetInt64(1) != count)
            {
                throw new InvalidDataException("Fixture row count does not match the requested document count.");
            }
        }

        Console.WriteLine($"host={Environment.MachineName}; documents={count}; runtime={Environment.Version}; utc={DateTimeOffset.UtcNow:O}");
        Console.WriteLine($"fixture={data}; expected_digest_sha256={Convert.ToHexString(SHA256.HashData(expected))}");
        await using var provider = new ServiceCollection().AddTerraBaseEngine(options).BuildServiceProvider();
        Console.WriteLine("Scope: IReplicaStates.DigestAsync only; two warmups, five measured scans; fixture generation excluded.");
        // shortcut: row counts assume the current full scan; instrument bucket reads when Merkle maintenance replaces it.
        Console.WriteLine("Logical state rows = verified fixture cardinality (digest SELECT visits every row); not physical I/O.");
        Console.WriteLine("Repair page bytes and full/network round time: not measured. Bucket digest payload: 65536 bytes (not wire bytes).");
        var source = provider.GetRequiredService<IReplicaStates>();
        for (var i = 0; i < 2; i++)
        {
            Validate(await source.DigestAsync(tenant, null, CancellationToken.None).ConfigureAwait(false), expected);
        }

        Console.WriteLine("| repeat | wall ms | process CPU ms | logical state rows | digest payload bytes | repair page bytes |");
        Console.WriteLine("| ---: | ---: | ---: | ---: | ---: | --- |");
        var wall = new double[5];
        var cpu = new double[5];
        using var process = Process.GetCurrentProcess();
        for (var i = 0; i < 5; i++)
        {
            process.Refresh();
            var cpuStart = process.TotalProcessorTime;
            var timer = Stopwatch.StartNew();
            var digest = await source.DigestAsync(tenant, null, CancellationToken.None).ConfigureAwait(false);
            timer.Stop();
            process.Refresh();
            cpu[i] = (process.TotalProcessorTime - cpuStart).TotalMilliseconds;
            wall[i] = timer.Elapsed.TotalMilliseconds;
            Validate(digest, expected);
            Console.WriteLine(FormattableString.Invariant($"| {i + 1} | {wall[i]:F3} | {cpu[i]:F3} | {count} | {digest!.Buckets.Length} | unmeasured |"));
        }

        Array.Sort(wall);
        Array.Sort(cpu);
        Console.WriteLine("| metric | median | min | max |");
        Console.WriteLine("| --- | ---: | ---: | ---: |");
        Console.WriteLine(FormattableString.Invariant($"| wall ms | {wall[2]:F3} | {wall[0]:F3} | {wall[4]:F3} |"));
        Console.WriteLine(FormattableString.Invariant($"| process CPU ms | {cpu[2]:F3} | {cpu[0]:F3} | {cpu[4]:F3} |"));
        if (args[0] == "anti-entropy-pull")
        {
            await AntiEntropyPullBench.RunAsync(provider, tenant, count, expected).ConfigureAwait(false);
        }
    }

    private static void Validate(ReplicaDigest? digest, byte[] expected)
    {
        if (digest is null || !digest.Buckets.AsSpan().SequenceEqual(expected))
        {
            throw new InvalidDataException("Engine digest differs from the keyed fixture hashes.");
        }
    }

    private static void Fold(byte[] digest, int bucket, long h1, long h2)
    {
        var slot = digest.AsSpan(bucket * 16, 16);
        BinaryPrimitives.WriteInt64LittleEndian(slot, BinaryPrimitives.ReadInt64LittleEndian(slot) ^ h1);
        BinaryPrimitives.WriteInt64LittleEndian(slot[8..], BinaryPrimitives.ReadInt64LittleEndian(slot[8..]) ^ h2);
    }
}
