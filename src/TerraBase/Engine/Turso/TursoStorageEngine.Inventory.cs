using System.Buffers.Binary;
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
    private readonly ConcurrentDictionary<string, KeyRefusal[]> _keyRefusals = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _weakWarnings = new(StringComparer.Ordinal);
    private sealed record KeyRefusal(string Id, string Wrapper);
    private sealed record RetainedInventory(long Files, long Bytes);
    private static readonly RetainedInventory UnknownRetained = new(-1, -1);
    private RetainedInventory _retainedInventory = UnknownRetained;
    private long _fileInventoryAfter;
    private int _fileInventoryRunning;
    private long _malformedKeys;
    private int _malformedLogged;
    private int _inventoryFailedLogged;
    private static readonly TimeSpan InventoryDeadline = TimeSpan.FromSeconds(2);

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

    private Dictionary<string, long> InventoryLocalKeys(bool startup, Dictionary<string, long> missing, out long wrappers, out RetainedInventory retained, CancellationToken cancellationToken)
    {
        var counts = NewKeyCounts();
        wrappers = 0;
        long retainedFiles = 0;
        long retainedBytes = 0;
        var retainedUnknown = false;
        foreach (var (entry, failed) in InventoryPaths(_dataDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = entry.FullName;
            if (failed)
            {
                Unknown(counts);
                retainedUnknown = true;
                continue;
            }
            var name = entry.Name;
            var wrapper = WrappedKeyFile.IsKey(name);
            var intentAt = name.IndexOf(".promote.db.importing", StringComparison.Ordinal);
            var intent = intentAt > 0;
            var authoritativeIntent = intent && name.EndsWith(".promote.db.importing", StringComparison.Ordinal)
                && Ulid.TryParse(name[..intentAt], out _);
            var relative = Path.GetRelativePath(_dataDirectory, path);
            var parts = relative.Split(Path.DirectorySeparatorChar);
            var area = parts[0];
            var archived = parts.Contains(".retained");
            var payloadArtifact = !wrapper && (name.Contains(".creating", StringComparison.Ordinal)
                || name.Contains(".rewriting", StringComparison.Ordinal) || name.Contains(".migrating", StringComparison.Ordinal)
                || name.Contains(".retained.", StringComparison.Ordinal) || archived);
            try
            {
                if (name.Contains(".retained-key", StringComparison.Ordinal) || name.Contains(".retained.", StringComparison.Ordinal) || area == PreservedFiles.Directory || name.Contains(".promote.db", StringComparison.Ordinal)
                    || name.Contains(".key.rewrapping", StringComparison.Ordinal) || name.Contains(".creating", StringComparison.Ordinal)
                    || name.Contains(".key.tmp", StringComparison.Ordinal)
                    || name.EndsWith(".key", StringComparison.Ordinal) && !File.Exists(path[..^4]))
                {
                    retainedFiles++;
                    retainedBytes += ((FileInfo)entry).Length;
                }
                if ((ScratchDirectories.Contains(area) || area == PreservedFiles.Directory || payloadArtifact) && ((FileInfo)entry).Length > 0
                    && !(area == ".tmp" && EmptyNativeScratch(path)))
                {
                    Unknown(counts);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Unknown(counts);
                retainedUnknown = true;
            }
            if (!wrapper && !intent)
            {
                continue;
            }
            wrappers++;
            var stem = name.EndsWith(".db.key", StringComparison.Ordinal) ? name[..^".db.key".Length] : null;
            var primary = stem is not null && (parts.Length == 1 && stem is SystemName or IntentName
                || parts.Length == 3 && Ulid.TryParse(stem, out _) && Ulid.TryParse(parts[0], out _) && Ulid.TryParse(parts[1], out _));
            var authoritative = name.Contains(".retained-key", StringComparison.Ordinal) || name.Contains(".retained.", StringComparison.Ordinal)
                || stem is SystemName or IntentName || stem is not null && Ulid.TryParse(stem, out _);
            try
            {
                if (wrapper)
                {
                    CountWrapper(path, WrappedKeyFile.Read(path), authoritative, startup, counts, missing);
                }
                else
                {
                    var record = DetachedImport.Read(path, name[..intentAt]);
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
                if (startup && (authoritative || authoritativeIntent) && (!primary || error is InvalidDataException or JsonException))
                {
                    RefuseKey(path, "unknown");
                }
            }
        }
        retained = retainedUnknown ? UnknownRetained : new RetainedInventory(retainedFiles, retainedBytes);
        return counts;
    }

    private static bool EmptyNativeScratch(string path)
    {
        var name = Path.GetFileName(path);
        if (name is not ("tursodb-temp.db" or "tursodb-temp.db-wal"))
        {
            return false;
        }
        var database = name.EndsWith("-wal", StringComparison.Ordinal) ? path[..^4] : path;
        DatabaseKeys.RejectLink(database);
        DatabaseKeys.RejectLink(database + "-wal");
        using var file = new FileStream(database, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        Span<byte> header = stackalloc byte[108];
        if (file.Length != 4096 || file.Read(header) != header.Length
            || !header[..16].SequenceEqual("SQLite format 3\0"u8)
            || BinaryPrimitives.ReadUInt16BigEndian(header[16..]) != 4096
            || BinaryPrimitives.ReadUInt32BigEndian(header[28..]) != 1
            || BinaryPrimitives.ReadUInt64BigEndian(header[32..]) != 0
            || header[100] != 13 || BinaryPrimitives.ReadUInt16BigEndian(header[103..]) != 0)
        {
            return false;
        }
        var wal = new FileInfo(database + "-wal");
        return !wal.Exists || wal.Length <= 32;
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
        var refusal = new KeyRefusal(id, wrapper);
        if (_keyRefusals.TryAdd(database, [refusal]))
        {
            _logger.ZLogError($"Local wrapper refused database {Path.GetRelativePath(_dataDirectory, database):@Database} under key id {id:@KeyId}; files are retained");
        }
        else
        {
            _keyRefusals.AddOrUpdate(database, [refusal], (_, held) =>
                held.Any(item => item.Wrapper == wrapper) ? held : [.. held, refusal]);
        }
    }

    private void Malformed(Dictionary<string, long> counts, string? keyId = null)
    {
        Unknown(counts, keyId);
        Interlocked.Increment(ref _malformedKeys);
        if (Interlocked.Exchange(ref _malformedLogged, 1) == 0)
        {
            _logger.ZLogWarning($"Local sealed inventory contains malformed records or retained images; storage is unchanged and retirement counts are unknown");
        }
    }

    private void CountCredential(ReadOnlyMemory<byte> json, bool startup,
        Dictionary<string, long> counts, Dictionary<string, long> missing)
    {
        string? keyId = null;
        try
        {
            var record = CustomerCredentials.ReadRecord(json);
            keyId = Encryption.Keyring.KeyId(record.Credentials.WrappedKey);
            if (!Encryption.Keyring.KeyIds.Contains(keyId))
            {
                Unknown(counts, keyId);
                if (startup)
                {
                    missing[keyId] = missing.GetValueOrDefault(keyId) + 1;
                }
                return;
            }
            var key = Encryption.Keyring.Unwrap(record.Credentials.WrappedKey);
            CryptographicOperations.ZeroMemory(key);
            if (counts[keyId] >= 0)
            {
                counts[keyId]++;
            }
        }
        catch (Exception error) when (error is InvalidDataException or JsonException or InvalidOperationException or ArgumentException)
        {
            Malformed(counts, keyId);
        }
    }

    public async ValueTask InventoryKeysAsync(CancellationToken cancellationToken)
    {
        var missing = new Dictionary<string, long>(StringComparer.Ordinal);
        var counts = InventoryLocalKeys(startup: true, missing, out _, out _, cancellationToken);
        RequireConfigured(missing);
        try
        {
            await Io.RunAsync(() => InventoryCatalog(true, counts, missing, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or SqliteException or UnauthorizedAccessException)
        {
            Unknown(counts);
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
        var unknown = NewKeyCounts();
        Unknown(unknown);
        Volatile.Write(ref _retainedInventory, UnknownRetained);
        if (Time.GetTimestamp() < Volatile.Read(ref _fileInventoryAfter)
            || Interlocked.CompareExchange(ref _fileInventoryRunning, 1, 0) != 0)
        {
            return unknown;
        }
        var finished = new ManualResetEventSlim();
        var deadline = new CancellationTokenSource(InventoryDeadline, Time);
        var counts = unknown;
        var retained = UnknownRetained;
        var scan = Task.Run(() =>
        {
            long size = 0;
            try
            {
                var missing = new Dictionary<string, long>(StringComparer.Ordinal);
                counts = InventoryLocalKeys(false, missing, out size, out retained, deadline.Token);
                size += InventoryCatalog(false, counts, missing, deadline.Token);
                deadline.Token.ThrowIfCancellationRequested();
            }
            catch (Exception error)
            {
                Unknown(counts);
                if (Interlocked.Exchange(ref _inventoryFailedLogged, 1) == 0)
                {
                    _logger.ZLogWarning($"Local sealed inventory scan failed with {error.GetType().Name:@Error}; retirement counts are unknown");
                }
            }
            finally
            {
                Volatile.Write(ref _fileInventoryAfter, size >= 4096 || deadline.IsCancellationRequested
                    ? Time.GetTimestamp() + 5 * Time.TimestampFrequency : 0);
                Volatile.Write(ref _fileInventoryRunning, 0);
                finished.Set();
            }
        });
        if (finished.Wait(InventoryDeadline))
        {
            if (deadline.IsCancellationRequested)
            {
                Unknown(counts);
            }
            else
            {
                Volatile.Write(ref _retainedInventory, retained);
            }
            finished.Dispose();
            deadline.Dispose();
            return counts;
        }
        deadline.Cancel();
        Volatile.Write(ref _retainedInventory, UnknownRetained);
        _ = scan.ContinueWith(_ =>
        {
            finished.Dispose();
            deadline.Dispose();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return unknown;
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

}
