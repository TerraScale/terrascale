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
        return new(Directory.GetFiles(options.DataDirectory, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(path).Contains(database, StringComparison.Ordinal))
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

    internal async Task VerifyMetricsAsync(CancellationToken cancellationToken)
    {
        using var client = new HttpClient();
        var text = await client.GetStringAsync("http://127.0.0.1:18493/metrics", cancellationToken);
        var retained = files.Keys.Where(path => path.Contains(".promote.db", StringComparison.Ordinal) && File.Exists(path)).ToArray();
        var count = ReadGauge(text, "terrabase_storage_retained_files");
        var bytes = ReadGauge(text, "terrabase_storage_retained_bytes");
        if (count < retained.Length || bytes < retained.Sum(path => new FileInfo(path).Length))
        {
            throw new InvalidDataException("retained files or bytes were not counted");
        }
        Console.WriteLine("keyring-driver:retained-gauges-cover-files=" + retained.Length);
    }

    private static double ReadGauge(string metrics, string name)
    {
        var row = metrics.Split('\n').Single(line => line.StartsWith(name, StringComparison.Ordinal) && !line.StartsWith('#'));
        return double.Parse(row[(row.LastIndexOf(' ') + 1)..], CultureInfo.InvariantCulture);
    }

    internal void Verify()
    {
        foreach (var (original, digest) in files)
        {
            var path = File.Exists(original) ? original : original.Replace(".promote.db", ".db", StringComparison.Ordinal);
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
