using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TerraBase.Storage.Schema;

namespace TerraBase.Storage.Backup;

/// <summary>Settings for the native SQLite backup stream.</summary>
public sealed record SqliteBackupOptions
{
    public required string DirectoryPath { get; init; }
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(10);
    public int RetainedSnapshots { get; init; } = 24;
    public bool CheckpointWal { get; init; } = true;
}

/// <summary>Integrity metadata written beside every backup snapshot.</summary>
public sealed record SqliteBackupManifest(
    string Format,
    string DatabasePath,
    string SnapshotPath,
    string Sha256,
    long JournalSequence,
    DateTimeOffset CreatedAt,
    long Bytes);

/// <summary>A checksummed, resumable byte range from SQLite's WAL.</summary>
public sealed record SqliteWalSegment(string Generation, long StartOffset, long EndOffset, string Sha256, string Path);

/// <summary>Latest locally observed backup recovery state.</summary>
public sealed record SqliteBackupHealth(bool Healthy, DateTimeOffset? LastSuccess, DateTimeOffset? LastFailure,
    long? RecoverableJournalSequence, string? Error);

/// <summary>Online, crash-safe SQLite snapshots with bounded retention.</summary>
public sealed class SqliteBackupManager : IAsyncDisposable
{
    private const string FormatVersion = "terrabase-sqlite-backup-v1";
    private readonly string databasePath;
    private readonly SqliteBackupOptions options;
    private readonly MetricsRegistry? metrics;
    private readonly SemaphoreSlim gate = new(1, 1);
    private string? walGeneration;
    private long walOffset = 32;
    private SqliteBackupHealth health = new(false, null, null, null, "no snapshot has completed");

    public SqliteBackupManager(string databasePath, SqliteBackupOptions options, MetricsRegistry? metrics = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentNullException.ThrowIfNull(options);
        if (options.RetainedSnapshots < 1) throw new ArgumentOutOfRangeException(nameof(options));
        if (options.Interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options));
        this.databasePath = Path.GetFullPath(databasePath);
        this.options = options with { DirectoryPath = Path.GetFullPath(options.DirectoryPath) };
        this.metrics = metrics;
        metrics?.SetComponentHealth(Component.Backup, HealthStatus.Degraded);
    }

    /// <summary>Creates one consistent snapshot and atomically publishes its manifest.</summary>
    public async Task<SqliteBackupManifest> CreateSnapshotAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(options.DirectoryPath);
            var id = $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";
            var finalPath = Path.Combine(options.DirectoryPath, $"{id}.db");
            var tempPath = finalPath + ".partial";
            var manifestPath = finalPath + ".json";
            try
            {
                // Litestream coordinates checkpoints through SQLite's write
                // lock. A read-only handle cannot safely checkpoint a WAL.
                await using var source = Open(databasePath, SqliteOpenMode.ReadWrite);
                await source.OpenAsync(cancellationToken).ConfigureAwait(false);
                SqliteSchema.ConfigureConnection(source);
                if (options.CheckpointWal)
                {
                    await using var checkpoint = source.CreateCommand();
                    checkpoint.CommandText = "PRAGMA wal_checkpoint(PASSIVE);";
                    await checkpoint.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                await using (var destination = Open(tempPath, SqliteOpenMode.ReadWriteCreate))
                {
                    await destination.OpenAsync(cancellationToken).ConfigureAwait(false);
                    SqliteSchema.ConfigureConnection(destination);
                    source.BackupDatabase(destination);
                    // The destination connection is WAL-enabled by the
                    // normal schema contract. Flush its WAL before moving
                    // only the database file, otherwise restored data could
                    // remain stranded in the temporary sidecar.
                    await using var destinationCheckpoint = destination.CreateCommand();
                    destinationCheckpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                    await destinationCheckpoint.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    await destination.CloseAsync().ConfigureAwait(false);
                }

                await FlushFileAsync(tempPath, cancellationToken).ConfigureAwait(false);
                File.Move(tempPath, finalPath, overwrite: false);
                var hash = await ComputeHashAsync(finalPath, cancellationToken).ConfigureAwait(false);
                var sequence = await ReadJournalSequenceAsync(source, cancellationToken).ConfigureAwait(false);
                var info = new FileInfo(finalPath);
                var manifest = new SqliteBackupManifest(FormatVersion, databasePath, finalPath, hash, sequence,
                    DateTimeOffset.UtcNow, info.Length);
                await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest), cancellationToken).ConfigureAwait(false);
                await FlushFileAsync(manifestPath, cancellationToken).ConfigureAwait(false);
                await PruneAsync(cancellationToken).ConfigureAwait(false);
                health = new SqliteBackupHealth(true, manifest.CreatedAt, health.LastFailure, manifest.JournalSequence, null);
                metrics?.SetComponentHealth(Component.Backup, HealthStatus.Healthy);
                return manifest;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                health = new SqliteBackupHealth(false, health.LastSuccess, DateTimeOffset.UtcNow,
                    health.RecoverableJournalSequence, exception.Message);
                metrics?.SetComponentHealth(Component.Backup, HealthStatus.Degraded);
                throw;
            }
            finally
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
        }
        finally { gate.Release(); }
    }

    /// <summary>Returns the latest backup state; healthy means a verified snapshot exists.</summary>
    public SqliteBackupHealth Health => health;

    /// <summary>
    /// Runs the bounded backup loop: one verified base snapshot followed by
    /// periodic WAL frame capture until cancellation. When a replica is
    /// supplied, artifacts are uploaded only after local checksum validation.
    /// </summary>
    public async Task RunAsync(IBackupReplica? replica = null, CancellationToken cancellationToken = default)
    {
        if (replica is null) await CreateSnapshotAsync(cancellationToken).ConfigureAwait(false);
        else await ReplicateSnapshotAsync(replica, cancellationToken).ConfigureAwait(false);
        using var timer = new PeriodicTimer(options.Interval);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                if (replica is null) await CaptureWalSegmentAsync(cancellationToken).ConfigureAwait(false);
                else await ReplicateWalSegmentAsync(replica, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                health = new SqliteBackupHealth(false, health.LastSuccess, DateTimeOffset.UtcNow,
                    health.RecoverableJournalSequence, exception.Message);
                metrics?.SetComponentHealth(Component.Backup, HealthStatus.Degraded);
                throw;
            }
        }
    }

    /// <summary>Creates and uploads a complete immutable snapshot artifact pair.</summary>
    public async Task<SqliteBackupManifest> ReplicateSnapshotAsync(IBackupReplica replica, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(replica);
        var manifest = await CreateSnapshotAsync(cancellationToken).ConfigureAwait(false);
        await using (var snapshot = new FileStream(manifest.SnapshotPath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await replica.PutAsync(Path.GetFileName(manifest.SnapshotPath), snapshot, manifest.Sha256, cancellationToken).ConfigureAwait(false);
        var manifestBytes = await File.ReadAllBytesAsync(manifest.SnapshotPath + ".json", cancellationToken).ConfigureAwait(false);
        var manifestHash = Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant();
        await using var metadata = new MemoryStream(manifestBytes, writable: false);
        await replica.PutAsync(Path.GetFileName(manifest.SnapshotPath) + ".json", metadata, manifestHash, cancellationToken).ConfigureAwait(false);
        return manifest;
    }

    /// <summary>
    /// Captures only complete WAL frames since the previous capture. The
    /// generation changes whenever SQLite rotates the WAL salt, matching the
    /// Litestream restore rule that segments never cross generations.
    /// </summary>
    public async Task<SqliteWalSegment?> CaptureWalSegmentAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var walPath = databasePath + "-wal";
            if (!File.Exists(walPath)) return null;
            await using var wal = new FileStream(walPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (wal.Length < 32) return null;
            var header = new byte[32];
            if (await wal.ReadAsync(header, cancellationToken).ConfigureAwait(false) != 32) return null;
            var magic = BitConverter.ToUInt32(header, 0);
            if (magic is not (0x377f0682u or 0x377f0683u)) return null;
            var pageSize = BitConverter.ToUInt32(header, 8);
            if (pageSize is < 512 or > 65536 || (pageSize & (pageSize - 1)) != 0) return null;
            var frameSize = checked((long)pageSize + 24);
            var completeLength = 32 + ((wal.Length - 32) / frameSize * frameSize);
            var generation = Convert.ToHexString(SHA256.HashData(header.AsSpan(16, 8))).ToLowerInvariant()[..32];
            if (!string.Equals(generation, walGeneration, StringComparison.Ordinal)) { walGeneration = generation; walOffset = 32; }
            if (completeLength <= walOffset) return null;
            Directory.CreateDirectory(options.DirectoryPath);
            var name = $"wal-{generation}-{walOffset:D16}-{completeLength:D16}.segment";
            var finalPath = Path.Combine(options.DirectoryPath, name);
            var tempPath = finalPath + ".partial";
            wal.Position = walOffset;
            await using (var output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                var remaining = completeLength - walOffset;
                var buffer = new byte[128 * 1024];
                while (remaining > 0)
                {
                    var read = await wal.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken).ConfigureAwait(false);
                    if (read == 0) throw new EndOfStreamException("SQLite WAL changed while it was being captured.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    remaining -= read;
                }
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(tempPath, finalPath, overwrite: false);
            var hash = await ComputeHashAsync(finalPath, cancellationToken).ConfigureAwait(false);
            var segment = new SqliteWalSegment(generation, walOffset, completeLength, hash, finalPath);
            await File.WriteAllTextAsync(finalPath + ".json", JsonSerializer.Serialize(segment), cancellationToken).ConfigureAwait(false);
            walOffset = completeLength;
            return segment;
        }
        finally { gate.Release(); }
    }

    /// <summary>Captures and uploads one WAL segment when new frames exist.</summary>
    public async Task<SqliteWalSegment?> ReplicateWalSegmentAsync(IBackupReplica replica, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(replica);
        var segment = await CaptureWalSegmentAsync(cancellationToken).ConfigureAwait(false);
        if (segment is null) return null;
        await using (var content = new FileStream(segment.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await replica.PutAsync(Path.GetFileName(segment.Path), content, segment.Sha256, cancellationToken).ConfigureAwait(false);
        var metadata = await File.ReadAllBytesAsync(segment.Path + ".json", cancellationToken).ConfigureAwait(false);
        var metadataHash = Convert.ToHexString(SHA256.HashData(metadata)).ToLowerInvariant();
        await using var manifest = new MemoryStream(metadata, writable: false);
        await replica.PutAsync(Path.GetFileName(segment.Path) + ".json", manifest, metadataHash, cancellationToken).ConfigureAwait(false);
        return segment;
    }

    /// <summary>Restores a verified snapshot to a new database path.</summary>
    public static async Task RestoreAsync(string snapshotPath, string destinationPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        snapshotPath = Path.GetFullPath(snapshotPath);
        destinationPath = Path.GetFullPath(destinationPath);
        var manifestPath = snapshotPath + ".json";
        if (File.Exists(manifestPath))
        {
            var manifest = JsonSerializer.Deserialize<SqliteBackupManifest>(await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false))
                ?? throw new InvalidDataException("Backup manifest is empty.");
            if (!string.Equals(manifest.Format, FormatVersion, StringComparison.Ordinal))
                throw new InvalidDataException($"Unsupported backup format '{manifest.Format}'.");
            if (manifest.Bytes != new FileInfo(snapshotPath).Length)
                throw new InvalidDataException("Backup manifest length does not match the snapshot.");
            var actual = await ComputeHashAsync(snapshotPath, cancellationToken).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(actual), Convert.FromHexString(manifest.Sha256)))
                throw new InvalidDataException("Backup snapshot integrity verification failed.");
        }
        var parent = Path.GetDirectoryName(destinationPath) ?? throw new ArgumentException("Destination must have a parent.", nameof(destinationPath));
        Directory.CreateDirectory(parent);
        var temp = destinationPath + ".restore";
        try
        {
            File.Copy(snapshotPath, temp, overwrite: true);
            await FlushFileAsync(temp, cancellationToken).ConfigureAwait(false);
            await using (var verification = Open(temp, SqliteOpenMode.ReadOnly))
            {
                await verification.OpenAsync(cancellationToken).ConfigureAwait(false);
                await using var check = verification.CreateCommand();
                check.CommandText = "PRAGMA integrity_check;";
                var result = Convert.ToString(await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
                if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Backup SQLite integrity check failed: {result ?? "<empty>"}.");
            }
            File.Move(temp, destinationPath, overwrite: true);
        }
        catch
        {
            if (File.Exists(temp)) File.Delete(temp);
            throw;
        }
    }

    public IReadOnlyList<SqliteBackupManifest> ListSnapshots() =>
        Directory.Exists(options.DirectoryPath)
            ? Directory.EnumerateFiles(options.DirectoryPath, "*.db.json")
                .Select(path => JsonSerializer.Deserialize<SqliteBackupManifest>(File.ReadAllText(path)))
                .Where(static value => value is not null).Cast<SqliteBackupManifest>()
                .OrderByDescending(static value => value.CreatedAt).ToArray()
            : [];

    private async Task PruneAsync(CancellationToken cancellationToken)
    {
        foreach (var stale in ListSnapshots().Skip(options.RetainedSnapshots))
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.Delete(stale.SnapshotPath + ".json");
            File.Delete(stale.SnapshotPath);
        }
        await Task.CompletedTask.ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        gate.Dispose();
        return ValueTask.CompletedTask;
    }

    private static SqliteConnection Open(string path, SqliteOpenMode mode) => new(new SqliteConnectionStringBuilder
    { DataSource = path, Mode = mode, Cache = SqliteCacheMode.Private, ForeignKeys = true, DefaultTimeout = 60 }.ToString());

    private static async Task<long> ReadJournalSequenceAsync(SqliteConnection connection, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(sequence), 0) FROM journal;";
        return Convert.ToInt64(await command.ExecuteScalarAsync(token).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<string> ComputeHashAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, token).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task FlushFileAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }
}
