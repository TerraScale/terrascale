using System.Collections.ObjectModel;

namespace TerraBase.Backup;

/// <summary>The kind of recovery point represented by a manifest.</summary>
public enum BackupKind
{
    Full,
    Incremental,
}

/// <summary>An object-store operation used by a presigned S3-compatible adapter.</summary>
public enum BackupObjectOperation
{
    Put,
    Get,
    List,
    Delete,
}

/// <summary>The kind of payload represented by an artifact.</summary>
public enum BackupArtifactKind
{
    Snapshot,
    IncrementalLog,
}

/// <summary>A source watermark that can be used as a PITR boundary.</summary>
public sealed record BackupWatermark
{
    public BackupWatermark(long sequence, DateTimeOffset timestamp)
    {
        if (sequence < 0)
            throw new ArgumentOutOfRangeException(nameof(sequence));
        Sequence = sequence;
        Timestamp = timestamp;
    }

    public long Sequence { get; }

    public DateTimeOffset Timestamp { get; }
}

/// <summary>A deterministic snapshot captured at one source watermark.</summary>
public sealed record BackupSnapshot
{
    public BackupSnapshot(BackupWatermark watermark, byte[] content)
    {
        ArgumentNullException.ThrowIfNull(watermark);
        ArgumentNullException.ThrowIfNull(content);
        Watermark = watermark;
        Content = content.ToArray();
    }

    public BackupWatermark Watermark { get; }

    public byte[] Content { get; }
}

/// <summary>One ordered logical change in an incremental backup log.</summary>
public sealed record BackupLogEntry
{
    public BackupLogEntry(BackupWatermark watermark, byte[] content)
    {
        ArgumentNullException.ThrowIfNull(watermark);
        ArgumentNullException.ThrowIfNull(content);
        Watermark = watermark;
        Content = content.ToArray();
    }

    public BackupWatermark Watermark { get; }

    public byte[] Content { get; }
}

/// <summary>The source seam consumed by the backup manager.</summary>
public interface IBackupSource
{
    /// <summary>The stable, path-safe source identity.</summary>
    string SourceId { get; }

    /// <summary>Captures a complete snapshot and its exact boundary.</summary>
    ValueTask<BackupSnapshot> CaptureSnapshotAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns the newest source boundary currently available.</summary>
    ValueTask<BackupWatermark> GetWatermarkAsync(CancellationToken cancellationToken = default);

    /// <summary>Reads every log entry after <paramref name="afterSequence"/> through the boundary.</summary>
    IAsyncEnumerable<BackupLogEntry> ReadLogAsync(
        long afterSequence,
        long throughSequence,
        CancellationToken cancellationToken = default);
}

/// <summary>A durable target to which a verified recovery chain is replayed.</summary>
public interface IBackupRestoreTarget
{
    /// <summary>Replaces the target state with the full snapshot.</summary>
    ValueTask ApplySnapshotAsync(Stream snapshot, BackupWatermark boundary, CancellationToken cancellationToken = default);

    /// <summary>Applies one ordered incremental entry.</summary>
    ValueTask ApplyLogEntryAsync(Stream content, BackupLogEntry entry, CancellationToken cancellationToken = default);
}

/// <summary>Content encryption/decryption hooks invoked around object upload/download.</summary>
public interface IBackupEncryptionHook
{
    string Algorithm { get; }

    string KeyId { get; }

    ValueTask<BackupEncryptedPayload> EncryptAsync(
        ReadOnlyMemory<byte> plaintext,
        BackupEncryptionContext context,
        CancellationToken cancellationToken = default);

    ValueTask<byte[]> DecryptAsync(
        ReadOnlyMemory<byte> ciphertext,
        BackupEncryptionContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>Context supplied to an encryption hook for key scoping and auditing.</summary>
public sealed record BackupEncryptionContext(string SourceId, string BackupId, string ObjectKey);

/// <summary>Encrypted bytes and the metadata needed to describe them in a manifest.</summary>
public sealed record BackupEncryptedPayload
{
    public BackupEncryptedPayload(byte[] content, string algorithm, string keyId)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(algorithm);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
        Content = content.ToArray();
        Algorithm = algorithm;
        KeyId = keyId;
    }

    public byte[] Content { get; }

    public string Algorithm { get; }

    public string KeyId { get; }
}

/// <summary>A resumable upload identity and committed byte offset.</summary>
public sealed record BackupUploadCheckpoint
{
    public BackupUploadCheckpoint(string uploadId, long offset)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uploadId);
        if (offset < 0)
            throw new ArgumentOutOfRangeException(nameof(offset));
        UploadId = uploadId;
        Offset = offset;
    }

    public string UploadId { get; }

    public long Offset { get; }
}

/// <summary>Metadata returned after an object has been committed.</summary>
public sealed record BackupObjectMetadata
{
    public BackupObjectMetadata(
        string key,
        long length,
        string sha256,
        DateTimeOffset createdAt,
        string? encryptionAlgorithm = null,
        string? encryptionKeyId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (length < 0)
            throw new ArgumentOutOfRangeException(nameof(length));
        if (!BackupValidation.IsSha256(sha256))
            throw new ArgumentException("Sha256 must be 64 lowercase hexadecimal characters.", nameof(sha256));
        Key = key;
        Length = length;
        Sha256 = sha256;
        CreatedAt = createdAt;
        EncryptionAlgorithm = encryptionAlgorithm;
        EncryptionKeyId = encryptionKeyId;
    }

    public string Key { get; }
    public long Length { get; }
    public string Sha256 { get; }
    public DateTimeOffset CreatedAt { get; }
    public string? EncryptionAlgorithm { get; }
    public string? EncryptionKeyId { get; }
}

/// <summary>A manifest artifact and its encrypted/plaintext integrity metadata.</summary>
public sealed record BackupArtifact
{
    public BackupArtifact(
        string key,
        long length,
        string sha256,
        string plaintextSha256,
        BackupArtifactKind kind,
        string? encryptionAlgorithm = null,
        string? encryptionKeyId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (length < 0)
            throw new ArgumentOutOfRangeException(nameof(length));
        if (!BackupValidation.IsSha256(sha256) || !BackupValidation.IsSha256(plaintextSha256))
            throw new ArgumentException("Artifact hashes must be 64 lowercase hexadecimal characters.");
        Key = key;
        Length = length;
        Sha256 = sha256;
        PlaintextSha256 = plaintextSha256;
        Kind = kind;
        EncryptionAlgorithm = encryptionAlgorithm;
        EncryptionKeyId = encryptionKeyId;
    }

    public string Key { get; }
    public long Length { get; }
    public string Sha256 { get; }
    public string PlaintextSha256 { get; }
    public BackupArtifactKind Kind { get; }
    public string? EncryptionAlgorithm { get; }
    public string? EncryptionKeyId { get; }
}

/// <summary>Configured and observed recovery objectives attached to a backup.</summary>
public sealed record BackupRpoRtoMetadata
{
    public BackupRpoRtoMetadata(
        TimeSpan recoveryPointObjective,
        TimeSpan recoveryTimeObjective,
        TimeSpan observedRecoveryPointAge,
        TimeSpan? observedRecoveryTime = null)
    {
        if (recoveryPointObjective < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(recoveryPointObjective));
        if (recoveryTimeObjective < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(recoveryTimeObjective));
        if (observedRecoveryPointAge < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(observedRecoveryPointAge));
        if (observedRecoveryTime < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(observedRecoveryTime));
        RecoveryPointObjective = recoveryPointObjective;
        RecoveryTimeObjective = recoveryTimeObjective;
        ObservedRecoveryPointAge = observedRecoveryPointAge;
        ObservedRecoveryTime = observedRecoveryTime;
    }

    public TimeSpan RecoveryPointObjective { get; }
    public TimeSpan RecoveryTimeObjective { get; }
    public TimeSpan ObservedRecoveryPointAge { get; }
    public TimeSpan? ObservedRecoveryTime { get; }
}

/// <summary>Backup manager configuration.</summary>
public sealed record BackupOptions
{
    public TimeSpan RecoveryPointObjective { get; init; } = TimeSpan.FromMinutes(5);

    public TimeSpan RecoveryTimeObjective { get; init; } = TimeSpan.FromMinutes(30);

    public int MaxUploadAttempts { get; init; } = 4;

    public IBackupEncryptionHook? Encryption { get; init; }

}

/// <summary>Retention policy for recovery points and their chain ancestors.</summary>
public sealed record BackupRetentionPolicy
{
    public int KeepLatest { get; init; } = 7;

    public TimeSpan? MaximumAge { get; init; }

    public bool PreserveIncrementalChains { get; init; } = true;

}

/// <summary>Immutable manifest committed last for a recovery point.</summary>
public sealed record BackupManifest
{
    public const string CurrentFormat = "terrabase-backup-v1";

    public required string Format { get; init; }

    public required string BackupId { get; init; }

    public required string SourceId { get; init; }

    public required BackupKind Kind { get; init; }

    public required BackupWatermark Boundary { get; init; }

    public string? BaseBackupId { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required IReadOnlyList<BackupArtifact> Artifacts { get; init; }

    public required BackupRpoRtoMetadata RpoRto { get; init; }

    public required string Sha256 { get; init; }

    public BackupManifest WithSha256(string sha256) => this with { Sha256 = sha256 };

    public BackupManifest WithArtifacts(IEnumerable<BackupArtifact> artifacts) => this with
    {
        Artifacts = new ReadOnlyCollection<BackupArtifact>(artifacts.ToArray()),
    };
}

/// <summary>A restore point selected for a restore or a PITR operation.</summary>
public sealed record BackupRestorePoint(
    string BackupId,
    string SourceId,
    BackupKind Kind,
    BackupWatermark Boundary,
    DateTimeOffset CreatedAt,
    string? BaseBackupId,
    BackupRpoRtoMetadata RpoRto);

/// <summary>Restore result including the measured recovery time.</summary>
public sealed record BackupRestoreResult(
    BackupRestorePoint RestorePoint,
    int AppliedLogEntries,
    TimeSpan RecoveryTime,
    BackupRpoRtoMetadata RpoRto);

/// <summary>Object-store upload failure with a safe resumable checkpoint.</summary>
public sealed class BackupUploadException : IOException
{
    public BackupUploadException(string message, BackupUploadCheckpoint? checkpoint = null, Exception? innerException = null)
        : base(message, innerException)
    {
        Checkpoint = checkpoint;
    }

    public BackupUploadCheckpoint? Checkpoint { get; }
}

/// <summary>Result of retention garbage collection.</summary>
public sealed record BackupGarbageCollectionResult(
    IReadOnlyList<string> RetainedBackupIds,
    IReadOnlyList<string> DeletedBackupIds,
    int DeletedObjectCount);

internal static class BackupValidation
{
    internal static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    internal static void ValidatePathSegment(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Contains('/', StringComparison.Ordinal) || value.Contains('\\', StringComparison.Ordinal)
            || value is "." or ".." || value.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException("Backup identifiers cannot contain path traversal characters.", parameterName);
    }

    internal static void ValidateObjectKey(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.StartsWith('/', StringComparison.Ordinal) || value.Contains("../", StringComparison.Ordinal)
            || value.Contains("..\\", StringComparison.Ordinal) || value is "." or "..")
            throw new ArgumentException("Backup object keys cannot escape the configured prefix.", nameof(value));
    }
}
