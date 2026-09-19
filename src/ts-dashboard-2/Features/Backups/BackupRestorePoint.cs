namespace TerraScale.Dashboard2;

/// <summary>A completed backup generation available for a side-by-side
/// restore (1:1 with RestorePointView in backups-data.ts).</summary>
public sealed record BackupRestorePoint(
    string Generation,
    string DatabaseId,
    string DatabaseName,
    string Region,
    string Mode,          // Full | Incremental | Snapshot
    bool Encrypted,
    int FilesToRestore,
    long SizeBytes,
    string CreatedAt);
