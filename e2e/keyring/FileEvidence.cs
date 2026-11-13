using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using TerraBase.Configuration;

namespace Journey.Keyring;

internal sealed class FileEvidence(Dictionary<string, byte[]> files)
{
    internal static FileEvidence Read(string request)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(request));
        TerraBaseOptions.ReadSecretFiles();
        var options = TerraBaseOptions.FromProcess([]);
        var database = json.RootElement.GetProperty("database").GetString()!;
        var unreadable = json.RootElement.TryGetProperty("unreadable", out var intent) ? intent.GetString() : null;
        if (unreadable is not null)
        {
            try
            {
                _ = File.ReadAllBytes(unreadable);
                throw new InvalidOperationException("intent permission proof was readable");
            }
            catch (UnauthorizedAccessException)
            {
                Console.WriteLine("keyring-driver:intent permission-denied");
            }
        }
        return new(Directory.GetFiles(options.DataDirectory, "*", SearchOption.AllDirectories)
            .Where(path => path != unreadable && Path.GetFileName(path).Contains(database, StringComparison.Ordinal))
            .ToDictionary(path => path, path => SHA256.HashData(File.ReadAllBytes(path)), StringComparer.Ordinal));
    }

    internal static FileEvidence Capture(string detached)
    {
        var directory = TerraBaseOptions.FromProcess([]).DataDirectory;
        var database = Path.GetFileName(detached)[..^".promote.db".Length];
        return new(Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(path).Contains(database, StringComparison.Ordinal))
            .ToDictionary(path => path, path => SHA256.HashData(File.ReadAllBytes(path)), StringComparer.Ordinal));
    }

    internal static async Task VerifyMetricsAsync(long expectedFiles, long expectedBytes, CancellationToken cancellationToken)
    {
        using var client = new HttpClient();
        var options = TerraBaseOptions.FromProcess([]);
        var text = await client.GetStringAsync("http://127.0.0.1:" + options.MetricsPort.ToString(CultureInfo.InvariantCulture) + "/metrics", cancellationToken);
        var count = ReadGauge(text, "terrabase_storage_retained_files");
        var bytes = ReadGauge(text, "terrabase_storage_retained_bytes");
        if (count != expectedFiles || bytes != expectedBytes)
        {
            throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture,
                $"retained files or bytes were not counted: files={count}/{expectedFiles}, bytes={bytes}/{expectedBytes}"));
        }
        Console.WriteLine("keyring-driver:retained-gauges-exact-files=" + expectedFiles);
    }

    private static double ReadGauge(string metrics, string name)
    {
        var row = metrics.Split('\n').Single(line => line.StartsWith(name, StringComparison.Ordinal) && !line.StartsWith('#'));
        return double.Parse(row[(row.LastIndexOf(' ') + 1)..], CultureInfo.InvariantCulture);
    }

    internal void Verify()
    {
        var relocated = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (original, digest) in files)
        {
            var canonical = original.Replace(".promote.db", ".db", StringComparison.Ordinal);
            var moved = !files.ContainsKey(canonical)
                && (original.EndsWith(".promote.db", StringComparison.Ordinal) || original.Contains(".promote.db-", StringComparison.Ordinal));
            var path = !File.Exists(original) && moved ? canonical : original;
            if (!File.Exists(path) && original.EndsWith(".db.key", StringComparison.Ordinal)
                && !File.Exists(original[..^4]) && !File.Exists(original[..^4] + "-wal"))
            {
                var name = Path.GetFileName(original)[..^".db.key".Length];
                var retained = Directory.EnumerateFiles(Path.GetDirectoryName(original)!, $".{name}.*.retained-key")
                    .Where(file => !files.ContainsKey(file) && !relocated.Contains(file))
                    .SingleOrDefault(file => SHA256.HashData(File.ReadAllBytes(file)).SequenceEqual(digest));
                if (retained is not null)
                {
                    path = retained;
                    relocated.Add(path);
                }
            }
            if (!File.Exists(path))
            {
                throw new InvalidDataException("recovery lost file " + Path.GetFileName(original));
            }
            if (!original.EndsWith(".importing", StringComparison.Ordinal)
                && !SHA256.HashData(File.ReadAllBytes(path)).SequenceEqual(digest))
            {
                throw new InvalidDataException("recovery changed bytes of " + Path.GetFileName(original));
            }
        }
        Console.WriteLine("keyring-driver:files-preserved=" + files.Count);
    }
}
