using System.Text.Json.Serialization;

namespace TerraBase.LiveRestoreDrill
{
    internal sealed class Proof
    {
        public string Kind { get; set; } = "single_file_snapshot_restore";
        public bool Passed { get; set; }
        public string Stage { get; set; } = "arguments";
        public string? Bucket { get; set; }
        public string Endpoint { get; set; } = ReadOnlyTransport.Endpoint;
        public string? FilePrefix { get; set; }
        public string? KeyScope { get; set; }
        public string? SnapshotKey { get; set; }
        public string? SourceSnapshotKey { get; set; }
        public string? Generation { get; set; }
        public DateTimeOffset? SnapshotAt { get; set; }
        public DateTimeOffset? ObservedAt { get; set; }
        public double? SnapshotRpoMs { get; set; }
        public double? BackupMs { get; set; }
        public double? ReferenceDownloadMs { get; set; }
        public double? DownloadRestoreMs { get; set; }
        public long? TargetPosition { get; set; }
        public long? ReachedPosition { get; set; }
        public long WalBytesReplayed { get; set; }
        public bool WalExercised { get; set; }
        public bool EncryptedObjectAuthentication { get; set; }
        public bool DatabaseIntegrityCheck { get; set; }
        public string? ExpectedDigest { get; set; }
        public string? RestoredDigest { get; set; }
        public bool DigestMatch { get; set; }
        public bool WriteRejectionSelfCheck { get; set; }
        public bool DisposableCleanup { get; set; }
        public bool LatestPitrProven { get; set; }
        public bool FullCellDrProven { get; set; }
    }

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
    [JsonSerializable(typeof(Proof))]
    internal sealed partial class ProofJson : JsonSerializerContext;
}
