using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
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
using MasterKeyring = TerraBase.Features.Backup.Keyring;

namespace Journey.Keyring;

internal static class InventoryProof
{
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
        Require(Gauge(text, "terrabase_keys_local_wrapped", "k0") >= 10_000, "synthetic wrappers missing");
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"keyring-driver:10000 startup_ms={startupMilliseconds:F2} scrape_ms={scrape:F2}"));
    }

    internal static async Task ImportAsync(IServiceProvider services, CancellationToken cancellationToken)
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
        Require(current == 1, "imported old credential missing from immediate local gauge: " + current.ToString(CultureInfo.InvariantCulture));
        Console.WriteLine("keyring-driver:import absent=whole-refusal k0=0->1");
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
        var inspected = DetachedImport.Inspect(path, key);
        Require(inspected.Position == 42, "inspection lost log rows");
        Verify();
        try
        {
            _ = DetachedImport.Inspect(path, RandomNumberGenerator.GetBytes(32));
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
