using System.Text.Json.Serialization;

namespace TerraBase.Features.Management;

internal sealed record HistoryRewrite(string Source, string Target, long Offset);

internal sealed record HistoryErasureRecord(
    string ErasureId, DateTimeOffset RequestedAt, DateTimeOffset ActiveDueAt, DateTimeOffset BackupDueAt, string[] ExpectedHolders,
    string? Requester = null, string? SettlementToken = null, string? SettledMarks = null, string? FinalId = null, string? FinalMachine = null,
    ulong FinalReplica = 0, string? FinalMarks = null, ulong FinalAuthMark = 0,
    DateTimeOffset? BackupCleanAt = null, string? BackupFinalId = null, DateTimeOffset? BackupVerifiedAt = null, string[]? CertifiedHolders = null,
    int FormatVersion = 1)
{
    public bool Certified => BackupVerifiedAt is not null && BackupFinalId is not null && BackupFinalId == FinalId && CertifiedHolders is { Length: > 0 };
}

internal sealed record HistoryErasureReceipt(
    string ErasureId, string MachineId, ulong Replica, string Phase, ulong AuthMark, string AuthIncarnation,
    DateTimeOffset MarkedAt,
    string? FinalId = null, HistoryRewrite? Auth = null, HistoryRewrite? System = null, DateTimeOffset? RewrittenAt = null,
    HistoryRewrite? Residue = null, DateTimeOffset? ResidueAt = null,
    ulong RetireCut = 0, HistoryRewrite? Retired = null, DateTimeOffset? RetiredAt = null, int FormatVersion = 1);

internal sealed record HistoryErasureLineage(string MachineId, string Auth, string System, DateTimeOffset At, int FormatVersion = 1);

public sealed record HistoryErasureRequest(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("requestedAt")] DateTimeOffset RequestedAt,
    [property: JsonPropertyName("settlementToken")] string? SettlementToken = null);

public sealed record HistoryErasureStatus(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("erasureId")] string ErasureId,
    [property: JsonPropertyName("pendingHolders")] int PendingHolders,
    [property: JsonPropertyName("activeDueAt")] DateTimeOffset ActiveDueAt,
    [property: JsonPropertyName("backupDueAt")] DateTimeOffset BackupDueAt,
    [property: JsonPropertyName("activeOverdue")] bool ActiveOverdue,
    [property: JsonPropertyName("backupOverdue")] bool BackupOverdue,
    [property: JsonPropertyName("settlementToken")] string? SettlementToken = null);

internal static class HistoryErasure
{
    public const string Collection = "history_erasures";

    public const string MarkCollection = "history_erasure_marks";

    public const string Lineage = "history_erasure_lineage";

    public const string Fenced = "fenced";
    public const string Rewritten = "rewritten";
    public const string ResidueCleared = "residue";
    public const string RetiredPhase = "retired";

    public const string Fencing = "fencing";
    public const string Reverify = "reverify";
    public const string Settling = "settling";
    public const string Rewriting = "rewriting";
    public const string Residue = "residue";
    public const string Backup = "backup";
    public const string Retire = "retire";
    public const string EpochStalled = "epoch_stalled";
    public const string Complete = "complete";

    public const int MaxHolders = 1024;

    public static string Receipts(string erasureId) => $"history_erasure_receipts/{erasureId}";

    public static string Finals(string erasureId) => $"history_erasure_finals/{erasureId}";
}
