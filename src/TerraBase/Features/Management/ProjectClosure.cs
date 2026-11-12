using TerraBase.Engine;
using TerraBase.Errors;

namespace TerraBase.Features.Management;

public sealed record ProjectClosureRequest(int Version, string? ClosureId, string? AuthorityOrganizationId);

public sealed record ProjectClosureStatus(string Status, string ClosureId, int PendingHolders, int EvidenceVersion = 2)
{
    public IReadOnlyList<string> NotEmptyRegions { get; init; } = [];
}

public readonly union CloseProjectResult(ProjectClosureStatus, Conflict, Unavailable);

internal sealed record ProjectClosureRecord(
    string ProjectId, string ClosureId, string AuthorityOrganizationId, DateTimeOffset RequestedAt, string[] ExpectedHolders, int FormatVersion = 1);

internal sealed record ProjectClosureReceipt(string ProjectId, string ClosureId, string MachineId, bool Empty, DateTimeOffset VerifiedAt, int FormatVersion = 1, int EvidenceVersion = 0)
{
    public bool Confirms(ProjectClosureRecord closure, string holder) => Empty && Answers(closure, holder);

    public bool Refuses(ProjectClosureRecord closure, string holder) => !Empty && Answers(closure, holder);

    private bool Answers(ProjectClosureRecord closure, string holder) =>
        EvidenceVersion == 2 && FormatVersion == closure.FormatVersion && MachineId == holder && ProjectId == closure.ProjectId && ClosureId == closure.ClosureId;
}

internal sealed record RegionClosureStatus(string? Status, string? ClosureId, int EvidenceVersion);

internal sealed record RegionRefusal(RegionRefusalDetail? Error);

internal sealed record RegionRefusalDetail(string? Code);

internal static class ProjectClosure
{
    public const string Kind = "project_closures";

    public const string Accepted = "accepted";

    public const string Pending = "pending";

    public const string Empty = "empty";

    public const string ClosureConflictCode = "closure_conflict";

    public const string BindingConflictCode = "binding_conflict";

    public static readonly Conflict ClosureConflict = new("the project is already closing under another closure id or organization", ClosureConflictCode);

    public static readonly Conflict BindingConflict = new("TerraBase binds this project id to another organization", BindingConflictCode);

    public static readonly Unavailable RegionUnanswered = new("a serving region did not answer whether it holds anything of the project; nothing was written, try again");

    private const string RegionHolders = "region:";

    public static string RegionHolder(string region) => RegionHolders + region;

    public static bool IsRegionHolder(string holder) => holder.StartsWith(RegionHolders, StringComparison.Ordinal);

    public static string HolderRegion(string holder) => holder[RegionHolders.Length..];

    public static string Receipts(string projectId) => $"project_closure_receipts/{projectId}";

    public static string Purges(string projectId) => $"project_closure_purges/{projectId}";

    public static string Scans(string projectId) => $"project_closure_scans/{projectId}";

    public static async ValueTask<bool> IsClosedAsync(IStorageTransaction transaction, string projectId, CancellationToken cancellationToken) =>
        await transaction.GetVersionAsync(Kind, projectId, cancellationToken).ConfigureAwait(false) is not null;
}
