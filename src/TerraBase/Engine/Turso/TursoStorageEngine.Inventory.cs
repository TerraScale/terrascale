using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text.Json;
using TerraBase.Configuration;
using TerraBase.Features.Backup;
using TerraBase.Primitives;
using Turso.Data.Sqlite;
using ZLogger;

namespace TerraBase.Engine.Turso;

internal sealed partial class TursoStorageEngine
{
    private readonly ConcurrentDictionary<string, string> _keyRefusals = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _weakWarnings = new(StringComparer.Ordinal);
    private long _fileInventoryAfter;
    private int _fileInventoryRunning;

    private static IEnumerable<(FileSystemInfo Entry, bool Failed)> InventoryPaths(string directory)
    {
        FileSystemInfo[] entries;
        var failed = false;
        try
        {
            DatabaseKeys.RejectLink(directory);
            entries = new DirectoryInfo(directory).GetFileSystemInfos();
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            entries = [];
            failed = true;
        }
        if (failed)
        {
            yield return (new DirectoryInfo(directory), true);
        }
        foreach (var entry in entries)
        {
            if (entry is DirectoryInfo child)
            {
                foreach (var path in InventoryPaths(child.FullName))
                {
                    yield return path;
                }
            }
            else
            {
                yield return (entry, false);
            }
        }
    }

    private Dictionary<string, long> NewKeyCounts() =>
        Encryption.Keyring.KeyIds.ToDictionary(id => id, _ => 0L, StringComparer.Ordinal);

    private static void Unknown(Dictionary<string, long> counts, string? id = null)
    {
        if (id is not null)
        {
            counts[id] = -1;
            return;
        }
        foreach (var key in counts.Keys)
        {
            counts[key] = -1;
        }
        counts["unattributed"] = -1;
    }

    private void CountWrapper(string path, string wrapped, bool authoritative, bool startup,
        Dictionary<string, long> counts, Dictionary<string, long> missing)
    {
        string id;
        try
        {
            id = Encryption.Keyring.KeyId(wrapped);
        }
        catch (InvalidDataException)
        {
            Unknown(counts);
            if (startup && authoritative)
            {
                RefuseKey(path, "unknown");
            }
            return;
        }
        if (!Encryption.Keyring.KeyIds.Contains(id))
        {
            Unknown(counts, id);
            if (startup && authoritative)
            {
                missing[id] = missing.GetValueOrDefault(id) + 1;
            }
            return;
        }
        try
        {
            var key = Encryption.Keyring.Unwrap(wrapped);
            CryptographicOperations.ZeroMemory(key);
            if (counts[id] >= 0)
            {
                counts[id]++;
            }
        }
        catch (InvalidDataException)
        {
            Unknown(counts, id);
            if (startup && authoritative)
            {
                RefuseKey(path, id);
            }
            else if (startup)
            {
                _logger.ZLogWarning($"Retained key artifact {Path.GetRelativePath(_dataDirectory, path):@Artifact} is not authentic under key id {id:@KeyId}; retirement counts are unknown");
            }
        }
    }

    private Dictionary<string, long> InventoryLocalKeys(bool startup, Dictionary<string, long> missing, out long wrappers)
    {
        var counts = NewKeyCounts();
        wrappers = 0;
        foreach (var (entry, failed) in InventoryPaths(_dataDirectory))
        {
            var path = entry.FullName;
            if (failed)
            {
                Unknown(counts);
                continue;
            }
            var wrapper = path.EndsWith(".key", StringComparison.Ordinal) || path.Contains(".key.rewrapping", StringComparison.Ordinal)
                || path.Contains(".key.tmp", StringComparison.Ordinal);
            var intent = path.Contains(".promote.db.importing", StringComparison.Ordinal);
            var authoritativeIntent = path.EndsWith(".promote.db.importing", StringComparison.Ordinal);
            if (!wrapper && !intent)
            {
                continue;
            }
            wrappers++;
            var authoritative = wrapper && path.EndsWith(".db.key", StringComparison.Ordinal)
                && !path.Contains(".creating", StringComparison.Ordinal);
            try
            {
                if (wrapper)
                {
                    CountWrapper(path, WrappedKeyFile.Read(path), authoritative, startup, counts, missing);
                }
                else
                {
                    var detached = path[..path.IndexOf(".importing", StringComparison.Ordinal)];
                    var record = DetachedImport.Read(path, Path.GetFileName(detached)[..^".promote.db".Length]);
                    CountWrapper(path, record.SourceKey, authoritativeIntent, startup, counts, missing);
                    if (record.DestinationKey is { } destination)
                    {
                        CountWrapper(path, destination, authoritativeIntent, startup, counts, missing);
                    }
                }
            }
            catch (Exception error) when (error is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
            {
                Unknown(counts);
                if (startup && !authoritative && wrapper)
                {
                    _logger.ZLogWarning($"Retained in-progress key artifact {Path.GetRelativePath(_dataDirectory, path):@Artifact} cannot be inventoried; retirement counts are unknown");
                }
                if (startup && (authoritative || authoritativeIntent))
                {
                    RefuseKey(path, "unknown");
                }
            }
        }
        return counts;
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
        if (database == LocateSystem() && wrapper == LocateSystem() + DatabaseKeys.Suffix)
        {
            throw new TerraBaseConfigurationException($"catalog wrapper is not authentic under key id '{id}'");
        }
        if (_keyRefusals.TryAdd(database, id))
        {
            _logger.ZLogError($"Local wrapper refused database {Path.GetRelativePath(_dataDirectory, database):@Database} under key id {id:@KeyId}; files are retained");
        }
    }

    private void CountCredential(string kind, string id, ReadOnlyMemory<byte> json, bool startup,
        Dictionary<string, long> counts, Dictionary<string, long> missing)
    {
        try
        {
            var record = EntityPayload.Read(new StoredRecord(id, 0, json), BackupJsonContext.Default.ProjectBackupDestinationRecord)
                ?? throw new InvalidDataException("sealed credential record is malformed");
            CountWrapper(kind + "/" + id, record.Credentials.WrappedKey, false, false, counts, missing);
            var keyId = Encryption.Keyring.KeyId(record.Credentials.WrappedKey);
            if (startup && !Encryption.Keyring.KeyIds.Contains(keyId))
            {
                missing[keyId] = missing.GetValueOrDefault(keyId) + 1;
            }
        }
        catch (Exception error) when (error is InvalidDataException or JsonException or InvalidOperationException or ArgumentException)
        {
            Unknown(counts);
            if (startup && _keyRefusals.TryAdd(kind + "/" + id, "unknown"))
            {
                _logger.ZLogError($"Local sealed credential {kind + "/" + id:@Database} is refused; its stored wrapper is retained");
            }
        }
    }

    public async ValueTask InventoryKeysAsync(CancellationToken cancellationToken)
    {
        var missing = new Dictionary<string, long>(StringComparer.Ordinal);
        var counts = InventoryLocalKeys(startup: true, missing, out _);
        RequireConfigured(missing);
        if (SystemDatabaseExists)
        {
            try
            {
                var store = await OpenSystemDatabaseAsync(cancellationToken).ConfigureAwait(false);
                await using var transaction = await store.BeginTransactionAsync(write: false, cancellationToken).ConfigureAwait(false);
                string? afterKind = null;
                while (true)
                {
                    var kinds = await transaction.ListKindsAsync(afterKind, 100, cancellationToken).ConfigureAwait(false);
                    foreach (var kind in kinds.Kinds.Where(kind => TursoStorageTransaction.CredentialKind(kind.Name)))
                    {
                        string? after = null;
                        while (true)
                        {
                            var page = await transaction.ListAsync(kind.Name, after, 100, 1024 * 1024, cancellationToken).ConfigureAwait(false);
                            foreach (var stored in page.Records)
                            {
                                CountCredential(kind.Name, stored.Id, stored.Json, true, counts, missing);
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
            }
            catch (Exception error) when (error is IOException or InvalidDataException or SqliteException or UnauthorizedAccessException)
            {
                Unknown(counts);
            }
        }
        RequireConfigured(missing);
        foreach (var id in Encryption.Keyring.KeyIds.Where(Encryption.Keyring.IsWeak))
        {
            if (counts[id] == 0 && id == Encryption.Keyring.ActiveId)
            {
                throw new TerraBaseConfigurationException($"active master key id '{id}' consists of one repeated byte and has no local wrappers; use a random key");
            }
            if (counts[id] != 0 && _weakWarnings.TryAdd(id, 0))
            {
                _logger.ZLogWarning($"Weak master key in use under key id {id:@KeyId}; rotate away using the normal master-key rotation procedure");
            }
        }
    }

    private static void RequireConfigured(Dictionary<string, long> missing)
    {
        if (missing.Count > 0)
        {
            throw new TerraBaseConfigurationException(string.Join("; ", missing.Select(pair =>
                $"unconfigured key id '{pair.Key}' is required by {pair.Value} local wrappers; add it to master-keys")));
        }
    }

    private Dictionary<string, long> StoredKeyCounts()
    {
        var missing = new Dictionary<string, long>(StringComparer.Ordinal);
        Dictionary<string, long> counts;
        if (Time.GetTimestamp() < Volatile.Read(ref _fileInventoryAfter)
            || Interlocked.CompareExchange(ref _fileInventoryRunning, 1, 0) != 0)
        {
            counts = NewKeyCounts();
            Unknown(counts);
        }
        else
        {
            try
            {
                counts = InventoryLocalKeys(startup: false, missing, out var wrappers);
                Volatile.Write(ref _fileInventoryAfter, wrappers >= 4096 ? Time.GetTimestamp() + 5 * Time.TimestampFrequency : 0);
            }
            finally
            {
                Volatile.Write(ref _fileInventoryRunning, 0);
            }
        }
        try
        {
            if (!SystemDatabaseExists)
            {
                return counts;
            }
            var key = Encryption.Keyring.Unwrap(DatabaseKeys.ReadWrapped(LocateSystem()));
            try
            {
                using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = LocateSystem(),
                    Mode = SqliteOpenMode.ReadOnly,
                    Pooling = false,
                    EncryptionCipher = "aegis256",
                    EncryptionKey = Convert.ToHexString(key),
                }.ToString());
                TursoSession.OpenExisting(connection, LocateSystem());
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT kind, id, body FROM _tb_records
                    WHERE kind GLOB 'backup_destination_history/*' OR kind GLOB 'projects/*/backup_destination'
                    """;
                using var rows = command.ExecuteReader();
                while (rows.Read())
                {
                    CountCredential(rows.GetString(0), rows.GetString(1), TursoSession.Bytes(rows, 2), false, counts, missing);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or SqliteException or UnauthorizedAccessException)
        {
            Unknown(counts);
        }
        return counts;
    }

    private IEnumerable<Measurement<long>> ObserveLocalKeys() => StoredKeyCounts()
        .Select(pair => new Measurement<long>(pair.Value, new KeyValuePair<string, object?>("key_id", pair.Key)));

    private IEnumerable<Measurement<long>> ObserveWeakKeys()
    {
        var weak = Encryption.Keyring.KeyIds.Where(Encryption.Keyring.IsWeak).ToArray();
        if (weak.Length == 0)
        {
            return [];
        }
        var counts = StoredKeyCounts();
        return weak.Select(id =>
            new Measurement<long>(counts[id] < 0 ? -1 : counts[id] > 0 ? 1 : 0, new KeyValuePair<string, object?>("key_id", id)));
    }

    private (long Files, long Bytes) RetainedFiles()
    {
        long count = 0;
        long bytes = 0;
        foreach (var (entry, failed) in InventoryPaths(_dataDirectory))
        {
            var path = entry.FullName;
            if (failed)
            {
                return (-1, -1);
            }
            if (!path.Contains(".promote.db", StringComparison.Ordinal)
                && !path.Contains(".key.rewrapping", StringComparison.Ordinal) && !path.Contains(".creating", StringComparison.Ordinal)
                && !path.Contains(".key.tmp", StringComparison.Ordinal)
                && !(path.EndsWith(".key", StringComparison.Ordinal) && !File.Exists(path[..^4])))
            {
                continue;
            }
            try
            {
                bytes += ((FileInfo)entry).Length;
                count++;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return (-1, -1);
            }
        }
        return (count, bytes);
    }
}
