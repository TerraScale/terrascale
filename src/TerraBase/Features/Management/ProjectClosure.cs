using TerraBase.Engine;
using TerraBase.Errors;

namespace TerraBase.Features.Management;

// ts-auth's closure request for one of its projects: docs/terrabase/project-closure.md.
public sealed record ProjectClosureRequest(int Version, string? ClosureId, string? AuthorityOrganizationId);

public sealed record ProjectClosureStatus(string Status, string ClosureId, int PendingHolders, int EvidenceVersion = 2);

public readonly union CloseProjectResult(ProjectClosureStatus, Conflict, Unavailable);

internal sealed record ProjectClosureRecord(
    string ProjectId, string ClosureId, string AuthorityOrganizationId, DateTimeOffset RequestedAt, string[] ExpectedHolders, int FormatVersion = 1);

internal sealed record ProjectClosureReceipt(string ProjectId, string ClosureId, string MachineId, bool Empty, DateTimeOffset VerifiedAt, int FormatVersion = 1, int EvidenceVersion = 0);

internal static class ProjectClosure
{
    public const string Kind = "project_closures";

    public const string Accepted = "accepted";

    public const string Pending = "pending";

    public static readonly Conflict ClosureConflict = new("the project is already closing under another closure id or organization", "closure_conflict");

    public static readonly Conflict BindingConflict = new("TerraBase binds this project id to another organization", "binding_conflict");

    public static string Receipts(string projectId) => $"project_closure_receipts/{projectId}";

    public static string Purges(string projectId) => $"project_closure_purges/{projectId}";

    public static string Scans(string projectId) => $"project_closure_scans/{projectId}";

    public static async ValueTask<bool> IsClosedAsync(IStorageTransaction transaction, string projectId, CancellationToken cancellationToken) =>
        await transaction.GetVersionAsync(Kind, projectId, cancellationToken).ConfigureAwait(false) is not null;
}
