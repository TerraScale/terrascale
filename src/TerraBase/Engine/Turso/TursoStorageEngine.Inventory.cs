using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text.Json;
using TerraBase.Configuration;
using TerraBase.Features.Backup;
using TerraBase.Features.Management;
using TerraBase.Primitives;
using ZLogger;

namespace TerraBase.Engine.Turso;

internal sealed partial class TursoStorageEngine
{
    private readonly ConcurrentDictionary<string, string> _keyRefusals = new(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, string> _credentialKeys = new Dictionary<string, string>(StringComparer.Ordinal);

    private IEnumerable<(string Path, string Wrapped)> LocalWrappers(bool refuse = true)
    {
        foreach (var path in WrappedKeyFile.Paths(_dataDirectory))
        {
            string wrapped;
            try
            {
                wrapped = WrappedKeyFile.Read(path);
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                if (!refuse)
                {
                    throw;
                }
                RefuseKey(path, "unknown");
                continue;
            }
            yield return (path, wrapped);
        }
        foreach (var path in Directory.EnumerateFiles(_dataDirectory, "*.promote.db.importing*", SearchOption.AllDirectories))
        {
            DetachedImport intent;
            try
            {
                var detached = path[..path.IndexOf(".importing", StringComparison.Ordinal)];
                intent = DetachedImport.Read(path, Path.GetFileName(detached)[..^".promote.db".Length]);
            }
            catch (Exception error) when (error is IOException or InvalidDataException or JsonException)
            {
                if (!refuse)
                {
                    throw;
                }
                continue;
            }
            yield return (path, intent.SourceKey);
            if (intent.DestinationKey is { } wrapped)
            {
                yield return (path, wrapped);
            }
        }
    }

    private Dictionary<string, long> InventoryLocalKeys()
    {
        var wrappers = LocalWrappers().ToArray();
        var missing = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var (path, wrapped) in wrappers)
        {
            string id;
            try
            {
                id = Encryption.Keyring.KeyId(wrapped);
            }
            catch (InvalidDataException)
            {
                RefuseKey(path, "unknown");
                continue;
            }
            if (!Encryption.Keyring.KeyIds.Contains(id))
            {
                missing[id] = missing.GetValueOrDefault(id) + 1;
                continue;
            }
            try
            {
                var key = Encryption.Keyring.Unwrap(wrapped);
                CryptographicOperations.ZeroMemory(key);
            }
            catch (InvalidDataException)
            {
                RefuseKey(path, id);
            }
        }
        return missing;
    }

    private void RefuseKey(string wrapper, string id)
    {
        var name = Path.GetFileName(wrapper);
        var end = name.IndexOf(".db", StringComparison.Ordinal);
        var database = Path.Combine(Path.GetDirectoryName(wrapper)!, end >= 0 ? name[..(end + 3)] : name);
        database = database.Replace(".promote.db", ".db", StringComparison.Ordinal);
        var replicaRoot = Path.Combine(_dataDirectory, TursoReplicas.DirectoryName) + Path.DirectorySeparatorChar;
        if (database.StartsWith(replicaRoot, StringComparison.Ordinal))
        {
            database = Path.Combine(_dataDirectory, Path.GetRelativePath(replicaRoot, database));
        }
        if (database == LocateSystem() || database == Path.Combine(_dataDirectory, IntentFile))
        {
            throw new TerraBaseConfigurationException($"catalog wrapper is not authentic under key id '{id}'");
        }
        if (_keyRefusals.TryAdd(database, id))
        {
            _logger.ZLogError($"Local wrapper refused database {Path.GetRelativePath(_dataDirectory, database):@Database} under key id {id:@KeyId}; files are retained");
        }
    }

    public async ValueTask InventoryKeysAsync(CancellationToken cancellationToken)
    {
        var missing = InventoryLocalKeys();
        if (!SystemDatabaseExists)
        {
            RequireConfigured(missing);
            return;
        }
        var catalogId = Encryption.Keyring.KeyId(DatabaseKeys.ReadWrapped(LocateSystem()));
        if (!Encryption.Keyring.KeyIds.Contains(catalogId))
        {
            RequireConfigured(missing);
        }
        var store = await OpenSystemDatabaseAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await store.BeginTransactionAsync(write: false, cancellationToken).ConfigureAwait(false);
        var credentials = new Dictionary<string, string>(StringComparer.Ordinal);
        string? afterKind = null;
        while (true)
        {
            var kinds = await transaction.ListKindsAsync(afterKind, 100, cancellationToken).ConfigureAwait(false);
            foreach (var kind in kinds.Kinds.Where(kind => kind.Name.StartsWith("backup_destination_history/", StringComparison.Ordinal)
                || kind.Name.StartsWith("projects/", StringComparison.Ordinal) && kind.Name.EndsWith("/backup_destination", StringComparison.Ordinal)))
            {
                string? after = null;
                while (true)
                {
                    var page = await transaction.ListAsync(kind.Name, after, 100, 1024 * 1024, cancellationToken).ConfigureAwait(false);
                    foreach (var stored in page.Records)
                    {
                        var record = EntityPayload.Read(stored, BackupJsonContext.Default.ProjectBackupDestinationRecord)
                            ?? throw new TerraBaseConfigurationException("catalog credential wrapper is malformed");
                        var wrapped = record.Credentials.WrappedKey;
                        var id = Encryption.Keyring.KeyId(wrapped);
                        credentials[kind.Name + "/" + stored.Id] = id;
                        if (!Encryption.Keyring.KeyIds.Contains(id))
                        {
                            missing[id] = missing.GetValueOrDefault(id) + 1;
                            continue;
                        }
                        try
                        {
                            var key = Encryption.Keyring.Unwrap(wrapped);
                            CryptographicOperations.ZeroMemory(key);
                        }
                        catch (InvalidDataException)
                        {
                            var item = kind.Name + "/" + stored.Id;
                            if (_keyRefusals.TryAdd(item, id))
                            {
                                _logger.ZLogError($"Local sealed credential {item:@Database} is refused under key id {id:@KeyId}; its stored wrapper is retained");
                            }
                        }
                    }
                    if (!page.HasMore)
                    {
                        break;
                    }
                    after = page.Records[^1].Id;
                }
            }
            if (!kinds.HasMore)
            {
                break;
            }
            afterKind = kinds.Kinds[^1].Name;
        }
        RequireConfigured(missing);
        Volatile.Write(ref _credentialKeys, credentials);
    }

    private static void RequireConfigured(Dictionary<string, long> missing)
    {
        if (missing.Count > 0)
        {
            throw new TerraBaseConfigurationException(string.Join("; ", missing.Select(pair =>
                $"unconfigured key id '{pair.Key}' is required by {pair.Value} local wrappers; add it to master-keys")));
        }
    }

    private IEnumerable<Measurement<long>> ObserveLocalKeys()
    {
        var counts = Encryption.Keyring.KeyIds.ToDictionary(id => id, _ => 0L, StringComparer.Ordinal);
        try
        {
            foreach (var (_, wrapped) in LocalWrappers(refuse: false))
            {
                var id = Encryption.Keyring.KeyId(wrapped);
                counts[id] = counts.GetValueOrDefault(id) + 1;
            }
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            return counts.Select(pair => new Measurement<long>(-1, new KeyValuePair<string, object?>("key_id", pair.Key)));
        }
        foreach (var id in Volatile.Read(ref _credentialKeys).Values)
        {
            counts[id] = counts.GetValueOrDefault(id) + 1;
        }
        return counts.Select(pair => new Measurement<long>(pair.Value, new KeyValuePair<string, object?>("key_id", pair.Key)));
    }

    private IEnumerable<string> RetainedFiles() => Directory.EnumerateFiles(_dataDirectory, "*", SearchOption.AllDirectories)
        .Where(path => path.Contains(".promote.db", StringComparison.Ordinal)
            || path.Contains(".key.rewrapping", StringComparison.Ordinal) || path.Contains(".creating", StringComparison.Ordinal)
            || path.EndsWith(".key", StringComparison.Ordinal) && !File.Exists(path[..^4]));

}
