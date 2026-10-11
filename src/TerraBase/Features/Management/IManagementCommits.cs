using System.Text.Json;
using System.Text.Json.Serialization;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.Documents;

namespace TerraBase.Features.Management;

/// <summary>One Management operation of a commit (docs/management-commits.md), as sent: snake_case, every member optional.</summary>
/// <param name="Op">The operation, for example <c>create_project</c>.</param>
/// <param name="Organization">Organization id.</param>
/// <param name="Project">Project id.</param>
/// <param name="Database">Database id.</param>
/// <param name="User">A member's user id.</param>
/// <param name="Invitation">Invitation id.</param>
/// <param name="Id">Client-chosen id of a created organization or project.</param>
/// <param name="Name">Organization or project display name, or database name.</param>
/// <param name="Plan"><c>hobby</c>, <c>pro</c> or <c>enterprise</c>.</param>
/// <param name="Role"><c>owner</c>, <c>admin</c>, <c>member</c> or <c>viewer</c>.</param>
/// <param name="Email">Invited email address.</param>
/// <param name="TokenHash">Lowercase hex SHA-256 of an invitation token.</param>
/// <param name="Version">Expected version of a <c>fence</c>.</param>
/// <param name="Account">Whether a <c>fence</c> is on the acting user's account.</param>
/// <param name="Days">Audit retention days.</param>
/// <param name="Databases">Database bound of a <c>limit</c>.</param>
/// <param name="Members">Bound of a <c>limit</c> on members plus pending, unexpired invitations.</param>
/// <param name="ActiveMembers">Bound of a <c>limit</c> on members only.</param>
/// <param name="HomeRegion">The home region of a <c>create_database</c>: the region the commit is sent to.</param>
/// <param name="ReplicaRegions">The replica regions of a <c>create_database</c> or <c>set_database_regions</c> (docs/cross-region.md).</param>
public sealed record ManagementOpRequest(
    string? Op,
    string? Organization,
    string? Project,
    string? Database,
    string? User,
    string? Invitation,
    string? Id,
    string? Name,
    string? Plan,
    OrganizationRole? Role,
    string? Email,
    [property: JsonPropertyName("token_hash")] string? TokenHash,
    string? Version,
    bool? Account,
    int? Days,
    long? Databases,
    long? Members,
    [property: JsonPropertyName("active_members")] long? ActiveMembers,
    [property: JsonPropertyName("home_region")] string? HomeRegion = null,
    [property: JsonPropertyName("replica_regions")] IReadOnlyList<string>? ReplicaRegions = null);

/// <summary>A validated operation of a commit.</summary>
public abstract record CommitOperation;

/// <summary>A document operation on the dashboard document space.</summary>
/// <param name="Operation">The operation, its collection as sent.</param>
public sealed record DocumentCommitOperation(DocumentOperation Operation) : CommitOperation;

/// <summary>A Management operation whose members were validated and normalized (trimmed names and emails).</summary>
/// <param name="Request">The operation.</param>
public sealed record ManagementCommitOperation(ManagementOpRequest Request) : CommitOperation;

/// <summary>A committed commit's response.</summary>
/// <param name="Id">The commit id.</param>
/// <param name="Offset">System database commit offset, a decimal string.</param>
/// <param name="Results">One result per operation.</param>
/// <param name="Versions">Versions after the commit.</param>
public sealed record CommitBody(string Id, string Offset, IReadOnlyList<CommitResultBody> Results, VersionsBody Versions);

/// <summary>Result of one commit operation; members that do not apply are omitted.</summary>
/// <param name="Id">Written document id.</param>
/// <param name="Version">Written document version.</param>
/// <param name="Asserted">Set by fences, limits and assertions.</param>
/// <param name="Count">Decimal count of <c>assert_count</c> and <c>limit</c>.</param>
/// <param name="Value">The resource a Management operation returned.</param>
public sealed record CommitResultBody(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Id = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Version = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Asserted = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Count = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? Value = null);

/// <summary>Versions of the acting user's account and of organizations.</summary>
/// <param name="Account">The account's version.</param>
/// <param name="Organizations">Organization id to version.</param>
public sealed record VersionsBody(string Account, IReadOnlyDictionary<string, string> Organizations);

/// <summary>A commit's response body and whether it was recorded before this request.</summary>
/// <param name="Body">UTF-8 JSON <see cref="CommitBody"/>.</param>
/// <param name="Replayed">Whether the commit had already committed and nothing was applied now.</param>
public sealed record CommitOutcome(byte[] Body, bool Replayed);

/// <summary>A commit that wrote nothing: why, and the operation that failed when one did (<c>error.op</c>).</summary>
/// <param name="Error">The failure.</param>
/// <param name="Op">Index of the failing operation; <see langword="null"/> for the commit as a whole.</param>
public sealed record CommitRejected(Failure Error, int? Op);

/// <summary>The outcome of a commit: its response (applied now or recorded before), or why nothing was written.</summary>
public readonly union CommitResult(CommitOutcome, CommitRejected);

/// <summary>The recorded commit body, or why it cannot be read.</summary>
public readonly union GetCommitResult(byte[], NotFound, Unavailable);

/// <summary>The requested versions, or why they cannot be read.</summary>
public readonly union GetVersionsResult(VersionsBody, NotFound, Unavailable);

/// <summary>A dashboard document, or why it cannot be read.</summary>
public readonly union GetDashboardDocumentResult(StoredDocument, NotFound, Unavailable);

/// <summary>A dashboard query result, or why it cannot be read.</summary>
public readonly union QueryDashboardDocumentsResult(QueryOutcome, NotFound, Validation, Unavailable);

/// <summary>
/// Atomic commits of Management operations and dashboard documents, versions, and reads of the dashboard document
/// space (docs/management-commits.md). Expected failures are values (docs/error-handling.md).
/// </summary>
public interface IManagementCommits
{
    /// <summary>
    /// Applies every operation in one system database transaction as <paramref name="userId"/>, whose verified email (from its
    /// access token) is <paramref name="email"/>, recording the response under <paramref name="commitId"/>, or answers the
    /// recorded response of an earlier commit with that id and request hash.
    /// </summary>
    ValueTask<CommitResult> CommitAsync(
        string userId, string? email, string commitId, string requestHash, IReadOnlyList<CommitOperation> operations, CancellationToken cancellationToken);

    /// <summary>The recorded response of the user's commit, or <see cref="NotFound"/> when there is none within the retention.</summary>
    ValueTask<GetCommitResult> GetCommitAsync(string userId, string commitId, CancellationToken cancellationToken);

    /// <summary>Reads versions in one snapshot; <see cref="NotFound"/> when the user is not a member of one of the organizations.</summary>
    ValueTask<GetVersionsResult> GetVersionsAsync(string userId, IReadOnlyList<string> organizations, CancellationToken cancellationToken);

    /// <summary>Reads a document of the dashboard document space; <see cref="NotFound"/> when it is absent or deleted.</summary>
    ValueTask<GetDashboardDocumentResult> GetDocumentAsync(string collection, string id, CancellationToken cancellationToken);

    /// <summary>Queries the dashboard document space, as the Documents API query does.</summary>
    ValueTask<QueryDashboardDocumentsResult> QueryDocumentsAsync(DocumentQuery query, long? countLimit, CancellationToken cancellationToken);
}
