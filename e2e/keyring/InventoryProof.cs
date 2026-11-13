using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Engine.Turso;
using TerraBase.Features.Backup;
using TerraBase.Features.Replicas;
using Turso.Data.Sqlite;
using StorageTransaction = TerraBase.Engine.Turso.TursoStorageTransaction;
using MasterKeyring = TerraBase.Features.Backup.Keyring;

namespace Journey.Keyring;

internal static class InventoryProof
{
    internal static async Task SeedStoredAsync(string mode, CancellationToken cancellationToken)
    {
        TerraBaseOptions.ReadSecretFiles();
        var options = TerraBaseOptions.FromProcess([]);
        var ring = Ring("k0", options.DatabaseKeyring!.Keys);
        var credentials = await CustomerCredentials.SealAsync(ring, "journey", "journey-secret", cancellationToken);
        var record = new ProjectBackupDestinationRecord(1, "inventory",
            new ProjectBackupDestination(new Uri("https://example.com"), "auto", "journey-backups", "both", DateTimeOffset.UnixEpoch), credentials);
        var body = JsonSerializer.Serialize(record, BackupJsonContext.Default.ProjectBackupDestinationRecord);
        body = mode switch
        {
            "missing" => body.Replace("\"credentials\":", "\"discarded\":", StringComparison.Ordinal),
            "null" => body.Replace(JsonSerializer.Serialize(credentials.WrappedKey), "null", StringComparison.Ordinal),
            "number" => body.Replace(JsonSerializer.Serialize(credentials.WrappedKey), "42", StringComparison.Ordinal),
            "empty" => body.Replace(JsonSerializer.Serialize(credentials.WrappedKey), "\"\"", StringComparison.Ordinal),
            "base64" => body.Replace(JsonSerializer.Serialize(credentials.WrappedKey), "\"not-base64\"", StringComparison.Ordinal),
            "version" => body.Replace(JsonSerializer.Serialize(credentials.WrappedKey), "\"v9.k0.invalid\"", StringComparison.Ordinal),
            _ => body,
        };
        using var connection = OpenCatalog(options);
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO _tb_record_kinds(name, created_at, entity_id) VALUES('backup_destination_history/inventory', 0, '01J00000000000000000000000');";
        command.ExecuteNonQuery();
        if (mode is "retained" or "cost")
        {
            command.CommandText = "PRAGMA capture_data_changes_conn('full');";
            command.ExecuteNonQuery();
        }
        command.CommandText = "INSERT INTO _tb_records(kind, id, version, body) VALUES('backup_destination_history/inventory', $id, 1, $body);";
        command.Parameters.AddWithValue("$id", "proof");
        command.Parameters.AddWithValue("$body", body);
        var count = mode == "cost" ? 5_000 : 1;
        using (var transaction = connection.BeginTransaction())
        {
            command.Transaction = transaction;
            for (var index = 0; index < count; index++)
            {
                command.Parameters["$id"].Value = index.ToString(CultureInfo.InvariantCulture);
                command.ExecuteNonQuery();
            }
            transaction.Commit();
        }
        command.Transaction = null;
        command.Parameters.Clear();
        if (mode == "retained")
        {
            var active = Ring(options.DatabaseKeyring.Active, options.DatabaseKeyring.Keys);
            var key = ring.Unwrap(credentials.WrappedKey);
            try
            {
                var rewrapped = record with { Credentials = credentials with { WrappedKey = active.Rewrap(key, "journey") } };
                command.CommandText = "UPDATE _tb_records SET body = $body WHERE kind = 'backup_destination_history/inventory';";
                command.Parameters.AddWithValue("$body", JsonSerializer.Serialize(rewrapped, BackupJsonContext.Default.ProjectBackupDestinationRecord));
                command.ExecuteNonQuery();
                command.Parameters.Clear();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }
        if (mode == "cost")
        {
            command.CommandText = "DELETE FROM _tb_records WHERE kind = 'backup_destination_history/inventory';";
            command.ExecuteNonQuery();
        }
        if (mode == "image")
        {
            command.CommandText = "SELECT name FROM sqlite_schema WHERE type = 'table' AND instr(sql, 'change_txn_id') > 0 AND instr(sql, 'updates') > 0;";
            var capture = command.ExecuteScalar() as string ?? throw new InvalidDataException("capture table is missing");
            command.CommandText = "INSERT INTO \"" + capture.Replace("\"", "\"\"", StringComparison.Ordinal)
                + "\"(change_type, table_name, before, after) VALUES(1, '_tb_records', NULL, x'ff');";
            command.ExecuteNonQuery();
            command.CommandText = "DELETE FROM _tb_records WHERE kind = 'backup_destination_history/inventory';";
            command.ExecuteNonQuery();
        }
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        command.ExecuteNonQuery();
        Console.WriteLine("keyring-driver:seed-stored " + mode);
    }

    internal static void TrimStored()
    {
        TerraBaseOptions.ReadSecretFiles();
        var options = TerraBaseOptions.FromProcess([]);
        using var connection = OpenCatalog(options);
        _ = StorageTransaction.Trim(connection, null, long.MaxValue, isSystem: true);
        Console.WriteLine("keyring-driver:retained-images-trimmed");
    }

    private static SqliteConnection OpenCatalog(TerraBaseOptions options)
    {
        var ring = Ring(options.DatabaseKeyring!.Active, options.DatabaseKeyring.Keys);
        var path = Path.Combine(options.DataDirectory, "_system.db");
        var key = ring.Unwrap(DatabaseKeys.ReadWrapped(path));
        try
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
                EncryptionCipher = "aegis256",
                EncryptionKey = Convert.ToHexString(key),
            }.ToString());
            connection.Open();
            return connection;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    internal static void SeedCost()
    {
        TerraBaseOptions.ReadSecretFiles();
        var options = TerraBaseOptions.FromProcess([]);
        var keyring = Ring(options.DatabaseKeyring!.Active, options.DatabaseKeyring.Keys);
        var directory = Path.Combine(options.DataDirectory, "inventory-cost");
        Directory.CreateDirectory(directory);
        var wrapped = keyring.Wrap(RandomNumberGenerator.GetBytes(32));
        for (var index = 0; index < 10_000; index++)
        {
            File.WriteAllText(Path.Combine(directory, index.ToString(CultureInfo.InvariantCulture) + ".creating.db.key"),
                DatabaseKeys.PreviousHeader + wrapped + "\n");
        }
    }

    internal static async Task CostAsync(double startupMilliseconds, CancellationToken cancellationToken)
    {
        using var client = new HttpClient();
        var timer = Stopwatch.StartNew();
        var text = await client.GetStringAsync("http://127.0.0.1:18383/metrics", cancellationToken);
        var scrape = timer.Elapsed.TotalMilliseconds;
        var firstReference = Gauge(text, "terrabase_keys_local_wrapped", "k0");
        Require(firstReference >= 10_000 || firstReference == -1, "synthetic wrappers missing");
        timer.Restart();
        var repeated = await client.GetStringAsync("http://127.0.0.1:18383/metrics", cancellationToken);
        var repeat = timer.Elapsed.TotalMilliseconds;
        Require(Gauge(repeated, "terrabase_keys_local_wrapped", "k0") == -1, "bounded recount must report unknown instead of a cached count");
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"keyring-driver:10000 startup_ms={startupMilliseconds:F2} scrape_ms={scrape:F2} repeat_scrape_ms={repeat:F2} repeat_reference=-1 interval_s=5 retained_images=10000 first_reference={firstReference}"));
    }

    internal static async Task WaitForMaintenanceAsync(CancellationToken cancellationToken)
    {
        var complete = false;
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, owner) =>
            {
                if (instrument.Name == "terrabase.keys.last_complete")
                {
                    owner.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => complete |= value > 0);
        listener.Start();
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
        do
        {
            listener.RecordObservableInstruments();
            if (complete)
            {
                break;
            }
        }
        while (await timer.WaitForNextTickAsync(cancellationToken));
    }

    internal static async Task ImportAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        await WaitForMaintenanceAsync(cancellationToken);
        var engine = services.GetRequiredService<IStorageEngine>();
        var receipts = services.GetRequiredService<RegionalReceiptImports>();
        var settings = services.GetRequiredService<TerraBaseOptions>().DatabaseKeyring!;
        var old = Ring("k0", settings.Keys);
        var absent = Ring("absent", new Dictionary<string, byte[]>
        {
            ["k0"] = settings.Keys["k0"],
            ["absent"] = RandomNumberGenerator.GetBytes(32),
        });
        var snapshot = await RegionalCatalog.ExportAsync(engine, cancellationToken);
        var destination = new ProjectBackupDestination(new Uri("https://example.com"), "auto", "journey-backups", "both", DateTimeOffset.UnixEpoch);
        async Task<RegionalRecord> CredentialAsync(IKeyring ring, string id)
        {
            var credentials = await CustomerCredentials.SealAsync(ring, "journey", "journey-secret", cancellationToken);
            return new RegionalRecord("backup_destination_history/inventory", id, JsonSerializer.SerializeToUtf8Bytes(
                new ProjectBackupDestinationRecord(1, id, destination, credentials), BackupJsonContext.Default.ProjectBackupDestinationRecord));
        }
        var incoming = await CredentialAsync(absent, "missing");
        var marker = new RegionalRecord("projects/inventory/backup_destination", "marker",
            (await CredentialAsync(old, "marker")).Json);
        try
        {
            await RegionalCatalog.ImportAsync(engine, snapshot with { Records = [.. snapshot.Records, marker, incoming] }, receipts, cancellationToken);
            throw new InvalidOperationException("unknown key import committed");
        }
        catch (InvalidDataException error) when (error.Message.Contains("unconfigured key id 'absent'", StringComparison.Ordinal))
        {
            var after = await RegionalCatalog.ExportAsync(engine, cancellationToken);
            Require(!after.Records.Any(record => record.Id is "marker" or "missing"), "import did not roll back as a whole");
            Require(after.Records.Count == snapshot.Records.Count && snapshot.Records.All(original =>
                after.Records.Any(record => record.Kind == original.Kind && record.Id == original.Id && record.Json.SequenceEqual(original.Json))),
                "refused import changed stored records");
            Require(after.Tables.Zip(snapshot.Tables).All(pair => pair.First.Rows.Count == pair.Second.Rows.Count
                && pair.First.Rows.Zip(pair.Second.Rows).All(rows => rows.First.SequenceEqual(rows.Second))),
                "refused import changed catalog tables");
        }
        using var client = new HttpClient();
        var before = Gauge(await client.GetStringAsync("http://127.0.0.1:18383/metrics", cancellationToken),
            "terrabase_keys_local_wrapped", "k0");
        Require(before == 0, "old wrappers were not drained before import");
        incoming = await CredentialAsync(old, "old");
        await RegionalCatalog.ImportAsync(engine, snapshot with { Records = [.. snapshot.Records, incoming] }, receipts, cancellationToken);
        var current = Gauge(await client.GetStringAsync("http://127.0.0.1:18383/metrics", cancellationToken),
            "terrabase_keys_local_wrapped", "k0");
        Require(current == 2, "imported old credential missing from immediate local gauge: " + current.ToString(CultureInfo.InvariantCulture));
        Console.WriteLine("keyring-driver:import absent=whole-refusal k0=0->2");
    }

    internal static void SeedInspection(string request)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(request));
        var path = json.RootElement.GetProperty("path").GetString()!;
        var key = Convert.FromBase64String(json.RootElement.GetProperty("key").GetString()!);
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            EncryptionCipher = "aegis256",
            EncryptionKey = Convert.ToHexString(key),
        }.ToString();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        connection.UseManualWal();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE commit_offset(id INTEGER PRIMARY KEY, incarnation TEXT, value INTEGER);
            INSERT INTO commit_offset VALUES(1, '01J00000000000000000000000', 42);
            CREATE TABLE evidence(value TEXT);
            INSERT INTO evidence VALUES('preserve every byte');
            """;
        command.ExecuteNonQuery();
        Require(new FileInfo(path + "-wal").Length > 0, "inspection proof needs a nonempty log");
        Console.WriteLine("keyring-driver:inspection-seed nonempty-wal-ready");
        _ = Console.In.ReadLine() ?? throw new IOException("inspection seed control closed");
    }

    internal static void Inspection(string request)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(request));
        var path = json.RootElement.GetProperty("path").GetString()!;
        var key = Convert.FromBase64String(json.RootElement.GetProperty("key").GetString()!);
        Require(new FileInfo(path + "-wal").Length > 0, "inspection requires a dormant nonempty log");
        var hashes = Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + "*")
            .ToDictionary(file => file, file => SHA256.HashData(File.ReadAllBytes(file)), StringComparer.Ordinal);
        void Verify()
        {
            Require(hashes.Keys.Order(StringComparer.Ordinal).SequenceEqual(
                Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + "*").Order(StringComparer.Ordinal)),
                "inspection changed sidecar names");
            foreach (var (file, hash) in hashes)
            {
                Require(SHA256.HashData(File.ReadAllBytes(file)).SequenceEqual(hash), "inspection changed " + Path.GetFileName(file));
            }
        }
        var inspected = Inspect(path, key);
        Require(inspected.Position == 42, "inspection lost log rows");
        Verify();
        try
        {
            _ = Inspect(path, RandomNumberGenerator.GetBytes(32));
            throw new InvalidOperationException("wrong inspection key succeeded");
        }
        catch (SqliteException)
        {
            Verify();
        }
        File.Copy(path, path.Replace(".promote.db", ".db", StringComparison.Ordinal));
        var evidence = FileEvidence.Capture(path);
        File.Delete(path);
        try
        {
            evidence.Verify();
            throw new InvalidOperationException("removed detached duplicate passed file evidence");
        }
        catch (InvalidDataException)
        {
            Console.WriteLine("keyring-driver:inspection nonempty-wal bytes-identical success+failure; removed-duplicate=refused");
        }
    }

    private static (string Incarnation, long Position) Inspect(string path, byte[] key)
    {
        TerraBaseOptions.ReadSecretFiles();
        var options = TerraBaseOptions.FromProcess([]);
        using var meter = new Meter("Journey.Inspection");
        var method = typeof(DetachedImport).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(method => method.Name == "Inspect" && method.GetParameters()[0].ParameterType == typeof(string));
        try
        {
            return ((string, long))method.Invoke(null, method.GetParameters().Length == 2
                ? [path, key] : [path, key, options.DataDirectory, new DiskBudget(options.DataDirectory, meter)])!;
        }
        catch (TargetInvocationException error) when (error.InnerException is { } inner)
        {
            ExceptionDispatchInfo.Capture(inner).Throw();
            throw;
        }
    }

    private static MasterKeyring Ring(string active, IReadOnlyDictionary<string, byte[]> keys)
    {
        var material = string.Join(",", keys.Select(pair => "\"" + pair.Key + "\":\"" + Convert.ToBase64String(pair.Value) + "\""));
        var settings = KeyringSettings.Parse(new Dictionary<string, string>
        {
            ["master-keys"] = "{\"active\":\"" + active + "\",\"keys\":{" + material + "}}",
        })!;
        return new MasterKeyring(settings, new KeyRewrapFaults(NullLogger<KeyRewrapFaults>.Instance));
    }

    private static double Gauge(string text, string name, string id)
    {
        Require(text.Split('\n').Any(line => line.StartsWith("# TYPE " + name + " ", StringComparison.Ordinal)), "metric missing " + name);
        var row = text.Split('\n').Single(line => line.StartsWith(name, StringComparison.Ordinal)
            && line.Contains("key_id=\"" + id + "\"", StringComparison.Ordinal));
        return double.Parse(row[(row.LastIndexOf(' ') + 1)..], CultureInfo.InvariantCulture);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
