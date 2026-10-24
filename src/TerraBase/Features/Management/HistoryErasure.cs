using System.Text.Json.Serialization;

namespace TerraBase.Features.Management;

/// <summary>One origin's committed position in one database file: every change it minted at or below <paramref name="Stamp"/>.</summary>
internal readonly record struct OriginCut(ulong Replica, ulong Stamp);

/// <summary>A completed healthy rewrite of one file, as a receipt binds it.</summary>
internal sealed record HistoryRewrite(string Source, string Target, long Offset);

/// <summary>
/// The permanent SYSTEM authority of one shared-history erasure (docs/terrabase/history-erasure.md), keyed by the random
/// erasure id ts-auth minted with its deletion job. Deadlines derive once from auth's immutable request time and never move.
/// Every field is an opaque random id, a machine name, a replica id, a stamp, an incarnation, a time or a digest of those:
/// no subject id, fence id (a natural-key hash), content, field value, name or email.
/// </summary>
/// <param name="ErasureId">A random ULID ts-auth minted with the deletion job; its auth request document names the fence.</param>
/// <param name="RequestedAt">Auth's original deletion time.</param>
/// <param name="ActiveDueAt"><paramref name="RequestedAt"/> + 30 days.</param>
/// <param name="BackupDueAt"><paramref name="RequestedAt"/> + the configured longest backup retention + 30 days.</param>
/// <param name="ExpectedHolders">Catalog members when requested, retired ones included; status adds current members.</param>
/// <param name="Requester">The machine that created the record: the only one that settles, finalizes and verifies backups, so
/// no two machines write this record after creation (no token churn, no reverting replicated fields).</param>
/// <param name="SettlementToken">Issued when the requester covered every holder's fence marker; auth re-verifies, then presents it.</param>
/// <param name="SettledMarks">Digest of the fence markers the token covered; cleared once the erasure is certified.</param>
/// <param name="FinalId">The final cut's id; rewrite receipts name it.</param>
/// <param name="FinalMachine">The machine that captured the final cut.</param>
/// <param name="FinalReplica">Its replica id.</param>
/// <param name="FinalMarks">Digest of the fence markers the final cut covered (a newer marker makes it stale); cleared once certified.</param>
/// <param name="FinalAuth">The auth database cut: the finalizer's applied vector plus its own committed final marker.</param>
/// <param name="FinalSystem">The SYSTEM cut: the finalizer's applied vector (its own entry is its SYSTEM final marker).</param>
/// <param name="BackupCleanAt">When an inventory first found no backup object older than the rewrites, for <paramref name="BackupFinalId"/>.</param>
/// <param name="BackupFinalId">The final cut <paramref name="BackupCleanAt"/> and <paramref name="BackupVerifiedAt"/> certify.</param>
/// <param name="BackupVerifiedAt">When no bucket snapshot from before <paramref name="BackupCleanAt"/> remained either: the certificate.</param>
/// <param name="CertifiedHolders">The holders the certificate covers: they rewrite the auth file once more after ts-auth retires the fence.</param>
/// <param name="FormatVersion">1.</param>
internal sealed record HistoryErasureRecord(
    string ErasureId, DateTimeOffset RequestedAt, DateTimeOffset ActiveDueAt, DateTimeOffset BackupDueAt, string[] ExpectedHolders,
    string? Requester = null, string? SettlementToken = null, string? SettledMarks = null, string? FinalId = null, string? FinalMachine = null,
    ulong FinalReplica = 0, string? FinalMarks = null, OriginCut[]? FinalAuth = null, OriginCut[]? FinalSystem = null,
    DateTimeOffset? BackupCleanAt = null, string? BackupFinalId = null, DateTimeOffset? BackupVerifiedAt = null, string[]? CertifiedHolders = null,
    int FormatVersion = 1)
{
    /// <summary>Immutable once set: backups closed for exactly the current final cut. Settlement and finalizing stop.</summary>
    public bool Certified => BackupVerifiedAt is not null && BackupFinalId is not null && BackupFinalId == FinalId && CertifiedHolders is { Length: > 0 };
}

/// <summary>
/// One holder's own progress, written only by that machine. <c>fenced</c>: its auth file held the fence when it committed
/// marker <paramref name="AuthMark"/>, so every later write of its origin was fenced and every earlier one is below the mark.
/// <c>rewritten</c>: both files were healthily rewritten from a cut covering <paramref name="FinalId"/>'s final cut.
/// <c>residue</c>: the auth file was rewritten again once no tombstone or feed event at or below that cut remained.
/// <c>retired</c>: after ts-auth retired the fence, the auth file was rewritten once its tombstones and feed events at or below
/// <paramref name="RetireCut"/> were gone. <paramref name="PreviousReplicas"/> are this machine's older replica epochs (it restarted
/// uncleanly since it first marked): their writes cannot be proven covered, so the erasure stalls for the operator.
/// </summary>
internal sealed record HistoryErasureReceipt(
    string ErasureId, string MachineId, ulong Replica, string Phase, ulong AuthMark, string AuthIncarnation,
    OriginCut[] AuthApplied, OriginCut[] SystemApplied, DateTimeOffset MarkedAt,
    string? FinalId = null, HistoryRewrite? Auth = null, HistoryRewrite? System = null, DateTimeOffset? RewrittenAt = null,
    HistoryRewrite? Residue = null, DateTimeOffset? ResidueAt = null, ulong[]? PreviousReplicas = null,
    ulong RetireCut = 0, HistoryRewrite? Retired = null, DateTimeOffset? RetiredAt = null, int FormatVersion = 1);

/// <summary>
/// The incarnations this machine's own successful rewrites (or markers) left in place, recorded per file as each succeeds;
/// another incarnation means a restore, rebuild or copy replaced the file.
/// </summary>
internal sealed record HistoryErasureLineage(string MachineId, string Auth, string System, DateTimeOffset At, int FormatVersion = 1);

/// <summary><c>PUT /admin/history-erasures/{erasure}</c> body.</summary>
public sealed record HistoryErasureRequest(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("requestedAt")] DateTimeOffset RequestedAt,
    [property: JsonPropertyName("settlementToken")] string? SettlementToken = null);

/// <summary>An erasure's status, computed from every holder's receipt; never complete while any holder is missing.</summary>
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

    /// <summary>
    /// The auth database collection (ts-auth provisions it) holding ts-auth's request document (id = erasure id, naming the
    /// fence) and the holders' committed per-origin markers. ts-auth deletes them all when it retires the fence.
    /// </summary>
    public const string MarkCollection = "history_erasure_marks";

    public const string Lineage = "history_erasure_lineage";

    public const string Fenced = "fenced";
    public const string Rewritten = "rewritten";
    public const string ResidueCleared = "residue";
    public const string RetiredPhase = "retired";

    // Status values.
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
