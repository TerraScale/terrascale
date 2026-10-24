using TerraBase.Engine;
using TerraBase.Errors;

namespace TerraBase.Features.Management;

/// <summary>ts-auth's closure request for one of its projects (docs/terrabase/project-closure.md).</summary>
/// <param name="Version">The contract version; only 1.</param>
/// <param name="ClosureId">ts-auth's stable closure id: a retry presents the same one.</param>
/// <param name="AuthorityOrganizationId">The ts-auth organization that owns the project.</param>
public sealed record ProjectClosureRequest(int Version, string? ClosureId, string? AuthorityOrganizationId);

/// <summary>Where a project closure stands.</summary>
/// <param name="Status"><c>accepted</c> once every holder confirmed emptiness under the fence, otherwise <c>pending</c>.</param>
/// <param name="ClosureId">The fence's closure id.</param>
/// <param name="PendingHolders">Machines that have not confirmed the project empty under the fence.</param>
public sealed record ProjectClosureStatus(string Status, string ClosureId, int PendingHolders);

/// <summary>A closure's status, or why it was refused without writing anything.</summary>
public readonly union CloseProjectResult(ProjectClosureStatus, Conflict, Unavailable);

/// <summary>The permanent fence: no database of the project is registered and no enrollment happens after it.</summary>
internal sealed record ProjectClosureRecord(
    string ProjectId, string ClosureId, string AuthorityOrganizationId, DateTimeOffset RequestedAt, string[] ExpectedHolders, int FormatVersion = 1);

/// <summary>One machine's view of the project under the fence; only that machine writes it.</summary>
internal sealed record ProjectClosureReceipt(string ProjectId, string ClosureId, string MachineId, bool Empty, DateTimeOffset VerifiedAt, int FormatVersion = 1);

internal static class ProjectClosure
{
    // Outside projects/<id>/, which a project deletion's prefix delete tombstones.
    public const string Collection = "project_closures";

    public const string Accepted = "accepted";

    public const string Pending = "pending";

    public static readonly Conflict ClosureConflict = new("the project is already closing under another closure id or organization", "closure_conflict");

    public static readonly Conflict BindingConflict = new("TerraBase binds this project id to another organization", "binding_conflict");

    public static string Receipts(string projectId) => $"project_closure_receipts/{projectId}";

    /// <summary>
    /// Whether the project is fenced; every write that would make it non-empty (a database, a project API key, a backup
    /// destination) reads this in its committing transaction. Tombstones count, so it fails closed.
    /// </summary>
    public static async ValueTask<bool> IsClosedAsync(IStorageTransaction transaction, string projectId, CancellationToken cancellationToken) =>
        await transaction.GetVersionAsync(Collection, projectId, cancellationToken).ConfigureAwait(false) is not null;
}
