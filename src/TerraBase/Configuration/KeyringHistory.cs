using System.Net;
using System.Text;
using Amazon.S3;
using TerraBase.Engine.Turso;
using TerraBase.Features.Backup;
using TerraBase.Primitives;
using ZLogger;

namespace TerraBase.Configuration;

internal static class KeyringHistory
{
    internal const string ObjectName = "v1/master-key-history";
    private const string Header = "TERRABASE-MASTER-KEY-HISTORY-1\n";

    private static Dictionary<string, string> Parse(string text)
    {
        if (text.Length > 16384 || !text.StartsWith(Header, StringComparison.Ordinal) || !text.EndsWith('\n'))
        {
            throw new TerraBaseConfigurationException("configured master key history cannot be read");
        }
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in text[Header.Length..^1].Split('\n'))
        {
            var parts = line.Split(' ');
            if (parts is not [var id, var fingerprint] || !KeyringSettings.ValidId(id) || fingerprint.Length != 64
                || !fingerprint.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F')
                || !result.TryAdd(id, fingerprint))
            {
                throw new TerraBaseConfigurationException("configured master key history cannot be read");
            }
        }
        if (result.Count is 0 or > 32)
        {
            throw new TerraBaseConfigurationException("configured master key history cannot be read");
        }
        return result;
    }

    private static string Serialize(IReadOnlyDictionary<string, string> keys) =>
        Header + string.Concat(keys.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + " " + pair.Value + "\n"));

    private static void Validate(IReadOnlyDictionary<string, string> recorded, IReadOnlyDictionary<string, string> configured)
    {
        foreach (var (id, fingerprint) in recorded)
        {
            if (!configured.TryGetValue(id, out var current))
            {
                throw new TerraBaseConfigurationException($"configured master key id '{id}' cannot be removed");
            }
            if (current != fingerprint)
            {
                throw new TerraBaseConfigurationException($"configured master key id '{id}' has different material from its history");
            }
        }
    }

    internal static void Record(string directory, IReadOnlyDictionary<string, string> configured)
    {
        var local = ReadLocal(directory);
        if (local is not null)
        {
            Validate(local, configured);
        }
        WriteLocal(directory, configured);
    }

    internal static async Task RecordAsync(string directory, IKeyring keyring, S3ObjectStore? store, ILogger logger, CancellationToken cancellationToken)
    {
        var configured = keyring.Fingerprints;
        var local = ReadLocal(directory);
        if (local is not null)
        {
            Validate(local, configured);
        }
        if (store is null)
        {
            Offline(directory, configured, local);
            return;
        }

        try
        {
            while (true)
            {
                var shared = await store.ReadKeyHistoryAsync(cancellationToken).ConfigureAwait(false);
                var recorded = shared is { } document ? Parse(document.Text) : new Dictionary<string, string>(StringComparer.Ordinal);
                Validate(recorded, configured);
                var merged = new Dictionary<string, string>(recorded, StringComparer.Ordinal);
                foreach (var pair in configured)
                {
                    merged.TryAdd(pair.Key, pair.Value);
                }
                var extended = (shared?.Text ?? Header) + string.Concat(configured.Where(pair => !recorded.ContainsKey(pair.Key))
                    .OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + " " + pair.Value + "\n"));
                if (recorded.Count == merged.Count || await store.AppendKeyHistoryAsync(extended, shared?.ETag, cancellationToken).ConfigureAwait(false))
                {
                    WriteLocal(directory, merged);
                    return;
                }
            }
        }
        catch (Exception error) when (error is HttpRequestException or IOException
            || error is AmazonS3Exception s3 && ((int)s3.StatusCode >= 500 || s3.StatusCode == 0)
            || error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.ZLogWarning($"Shared master key history is unreachable; validating the durable local cache: {error.GetType().Name:@Error}");
            Offline(directory, configured, local);
        }
    }

    private static void Offline(string directory, IReadOnlyDictionary<string, string> configured, Dictionary<string, string>? local)
    {
        if (local is null && Directory.Exists(directory) && Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Any(path => !Path.GetFileName(path).StartsWith(".master-key-history", StringComparison.Ordinal)))
        {
            throw new TerraBaseConfigurationException("shared master key history is unreachable and no local history proves the keys for existing encrypted data");
        }
        WriteLocal(directory, configured);
    }

    private static Dictionary<string, string>? ReadLocal(string directory)
    {
        try
        {
            DatabaseKeys.RejectLink(directory);
            var path = Path.Combine(directory, ".master-key-history");
            DatabaseKeys.RejectLink(path);
            if (Directory.Exists(path))
            {
                throw new TerraBaseConfigurationException("configured master key history cannot be read");
            }
            if (File.Exists(path) && new FileInfo(path).Length is <= 0 or > 16384)
            {
                throw new TerraBaseConfigurationException("configured master key history cannot be read");
            }
            return File.Exists(path) ? Parse(File.ReadAllText(path, Encoding.ASCII)) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new TerraBaseConfigurationException("configured master key history cannot be read");
        }
    }

    private static void WriteLocal(string directory, IReadOnlyDictionary<string, string> keys)
    {
        try
        {
            Directory.CreateDirectory(directory);
            DatabaseKeys.RejectLink(directory);
            var path = Path.Combine(directory, ".master-key-history");
            DatabaseKeys.RejectLink(path);
            DatabaseKeys.RejectLink(path + ".writing");
            using (var file = new FileStream(path + ".writing", FileMode.Create, FileAccess.Write, FileShare.None))
            {
                file.Write(Encoding.ASCII.GetBytes(Serialize(keys)));
                file.Flush(flushToDisk: true);
            }
            File.Move(path + ".writing", path, overwrite: true);
            DirectorySync.Sync(directory);
            DirectorySync.Sync(Path.GetDirectoryName(directory)!);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new TerraBaseConfigurationException("configured master key history cannot be durably recorded");
        }
    }
}
