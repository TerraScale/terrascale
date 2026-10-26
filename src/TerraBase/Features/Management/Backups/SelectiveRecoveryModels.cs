using System.Text.Json;

namespace TerraBase.Features.Management.Backups;

/// <summary>Historical live documents to inspect; omitted ids selects the bounded whole collection.</summary>
public sealed record RecoverySelection(string Collection, IReadOnlyList<string>? Ids = null);

/// <summary>A retained recovery point and at most sixteen collection selections.</summary>
public sealed record RecoveryPreviewRequest(DateTimeOffset? PointInTime, IReadOnlyList<RecoverySelection>? Selections, string? Destination = null);

/// <summary>A historical document and the current content and exact validator, including a tombstone's validator.</summary>
public sealed record RecoveryRecord(string Collection, string Id, JsonElement Historical, JsonElement? Current, string? CurrentVersion);

/// <summary>Actual historical/current contents and a short-lived server-authenticated approval.</summary>
public sealed record RecoveryPreview(string Approval, DateTimeOffset ExpiresAt, IReadOnlyList<RecoveryRecord> Records);

/// <summary>Identifies one explicitly approved historical record.</summary>
public sealed record RecoveryRecordKey(string Collection, string Id);

/// <summary>Approves only the named records from a preview; contents and expected versions cannot be supplied.</summary>
public sealed record RecoveryRequest(string? Approval, IReadOnlyList<RecoveryRecordKey>? Records);

internal sealed record RecoveryApprovedRecord(string Collection, string Id, string Hash, string? CurrentVersion);
internal sealed record RecoveryApproval(int Version, string Actor, Guid Source, DateTimeOffset PointInTime, string Destination,
    DateTimeOffset ExpiresAt, IReadOnlyList<RecoveryApprovedRecord> Records);
