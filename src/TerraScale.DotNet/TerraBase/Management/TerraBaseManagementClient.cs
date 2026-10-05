using System.Net.Http.Headers;
using System.Text.Json.Serialization.Metadata;

namespace TerraScale.DotNet.TerraBase;

/// <summary>Configures a <see cref="TerraBaseManagementClient"/>.</summary>
public sealed record TerraBaseManagementClientOptions
{
    /// <summary>HTTPS origin of the TerraBase client listener; the Management API is not served on the admin listener.</summary>
    public Uri? BaseAddress { get; init; }

    /// <summary>Resolves the HTTPS client origin per request. Set this or BaseAddress, exclusively.</summary>
    public ITerraBaseEndpointResolver? EndpointResolver { get; init; }

    /// <summary>
    /// The dashboard service credential (the secret in TerraBase's <c>dashboard-credential-file</c>), sent as
    /// <c>Authorization: Bearer</c>. It grants no document access.
    /// </summary>
    public string? ServiceCredential { get; init; }

    /// <summary>Deadline of each request, including reading its response. Default 30 seconds, at most 10 minutes.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Largest response body read, 64 KiB to 1 GiB. Default 64 MiB, enough for any page TerraBase returns.</summary>
    public int MaxResponseBytes { get; init; } = TerraBaseConnection.DefaultMaxResponseBytes;

    /// <inheritdoc />
    public override string ToString() => $"TerraBase management client options for {BaseAddress}";
}

/// <summary>
/// Client of the TerraBase Management API (<c>/v1/management/…</c>): organizations, projects, databases, branches,
/// members, invitations, ownership transfer and audit. Every call acts as <c>userId</c>, a ts-auth user id that must
/// come from the caller's authenticated session, never from browser input; organization routes also send it as
/// <c>X-TerraBase-Organization-Id</c>. Organizations the user is not a member of answer
/// <see cref="TerraBaseNotFoundException"/>. Built over a caller-owned <see cref="HttpClient"/> that it never disposes.
/// Thread-safe. Requests are sent once: the Management API has no idempotency contract.
/// </summary>
public sealed class TerraBaseManagementClient
{
    private const string Root = "/v1/management";
    private const string UserHeader = "X-TerraBase-User-Id";
    private const string OrganizationHeader = "X-TerraBase-Organization-Id";
    private const int MaxUserIdLength = 256;
    private static readonly TerraBaseJsonContext Json = TerraBaseJsonContext.Wire;

    private readonly TerraBaseConnection connection;

    /// <summary>
    /// Creates a client and validates <paramref name="options"/>. The caller-owned HttpClient must disable automatic
    /// redirects; use <see cref="TerraBaseHttp.CreateHandler"/>. The SDK cannot inspect or change its handler settings.
    /// </summary>
    public TerraBaseManagementClient(HttpClient httpClient, TerraBaseManagementClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        connection = new TerraBaseConnection(httpClient, options.BaseAddress, options.EndpointResolver, options.ServiceCredential, nameof(options.ServiceCredential), options.RequestTimeout, options.MaxResponseBytes);
    }

    // Organizations

    /// <summary>Creates an organization with the acting user as its first member, an owner. A taken name throws <see cref="TerraBaseConflictException"/>.</summary>
    public Task<TerraBaseOrganization> CreateOrganizationAsync(string userId, string name, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, Root + "/organizations", userId, null, new RequestBody { Name = name }, Json.TerraBaseOrganization, cancellationToken);

    /// <summary>Lists the acting user's organizations in creation order.</summary>
    public Task<TerraBasePage<TerraBaseOrganization>> ListOrganizationsAsync(string userId, string? after = null, int? limit = null, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Get, Root + "/organizations" + Page(after, limit), userId, null, null, Json.TerraBasePageTerraBaseOrganization, cancellationToken);

    /// <summary>Reads an organization.</summary>
    public Task<TerraBaseOrganization> GetOrganizationAsync(string userId, string organizationId, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Get, Organization(organizationId), userId, organizationId, null, Json.TerraBaseOrganization, cancellationToken);

    /// <summary>Renames an organization. Needs admin.</summary>
    public Task<TerraBaseOrganization> RenameOrganizationAsync(string userId, string organizationId, string name, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Patch, Organization(organizationId), userId, organizationId, new RequestBody { Name = name }, Json.TerraBaseOrganization, cancellationToken);

    /// <summary>Sets an organization's plan. Needs owner.</summary>
    public Task<TerraBaseOrganization> SetPlanAsync(string userId, string organizationId, TerraBasePlan plan, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, Organization(organizationId) + "/plan", userId, organizationId, new RequestBody { Plan = plan }, Json.TerraBaseOrganization, cancellationToken);

    /// <summary>Deletes an organization without projects, with its members, invitations, transfer and audit log. Needs owner.</summary>
    public Task DeleteOrganizationAsync(string userId, string organizationId, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Delete, Organization(organizationId), userId, organizationId, null, cancellationToken);

    /// <summary>Reads the organization the acting user last switched to; <see cref="TerraBaseNotFoundException"/> when none.</summary>
    public Task<TerraBaseOrganization> GetCurrentOrganizationAsync(string userId, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Get, Root + "/current-organization", userId, null, null, Json.TerraBaseOrganization, cancellationToken);

    /// <summary>Switches the acting user to one of their organizations.</summary>
    public Task<TerraBaseOrganization> SetCurrentOrganizationAsync(string userId, string organizationId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(organizationId);
        return SendAsync(HttpMethod.Put, Root + "/current-organization", userId, null, new RequestBody { OrganizationId = organizationId }, Json.TerraBaseOrganization, cancellationToken);
    }

    // Projects

    /// <summary>Creates a project; a name taken in the organization throws <see cref="TerraBaseConflictException"/>.</summary>
    public Task<TerraBaseProject> CreateProjectAsync(string userId, string organizationId, string name, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, Organization(organizationId) + "/projects", userId, organizationId, new RequestBody { Name = name }, Json.TerraBaseProject, cancellationToken);

    /// <summary>Lists an organization's projects in creation order.</summary>
    public Task<TerraBasePage<TerraBaseProject>> ListProjectsAsync(
        string userId, string organizationId, string? after = null, int? limit = null, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Get, Organization(organizationId) + "/projects" + Page(after, limit), userId, organizationId, null, Json.TerraBasePageTerraBaseProject, cancellationToken);

    /// <summary>Reads a project.</summary>
    public Task<TerraBaseProject> GetProjectAsync(string userId, string organizationId, string projectId, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Get, Project(organizationId, projectId), userId, organizationId, null, Json.TerraBaseProject, cancellationToken);

    /// <summary>Renames a project.</summary>
    public Task<TerraBaseProject> RenameProjectAsync(string userId, string organizationId, string projectId, string name, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Patch, Project(organizationId, projectId), userId, organizationId, new RequestBody { Name = name }, Json.TerraBaseProject, cancellationToken);

    /// <summary>Deletes a project without databases; one with databases throws <see cref="TerraBaseConflictException"/>.</summary>
    public Task DeleteProjectAsync(string userId, string organizationId, string projectId, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Delete, Project(organizationId, projectId), userId, organizationId, null, cancellationToken);

    // Databases and branches

    /// <summary>Creates an empty database in a project.</summary>
    public Task<TerraBaseManagedDatabase> CreateDatabaseAsync(
        string userId, string organizationId, string projectId, string name, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, Project(organizationId, projectId) + "/databases", userId, organizationId, new RequestBody { Name = name }, Json.TerraBaseManagedDatabase, cancellationToken);

    /// <summary>Lists a project's databases in creation order.</summary>
    public Task<TerraBasePage<TerraBaseManagedDatabase>> ListDatabasesAsync(
        string userId, string organizationId, string projectId, string? after = null, int? limit = null, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Get, Project(organizationId, projectId) + "/databases" + Page(after, limit), userId, organizationId, null, Json.TerraBasePageTerraBaseManagedDatabase, cancellationToken);

    /// <summary>Reads a database by id.</summary>
    public Task<TerraBaseManagedDatabase> GetDatabaseAsync(
        string userId, string organizationId, string projectId, string databaseId, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Get, Database(organizationId, projectId, databaseId), userId, organizationId, null, Json.TerraBaseManagedDatabase, cancellationToken);

    /// <summary>Deletes a database and its data. Its branches are independent and stay.</summary>
    public Task DeleteDatabaseAsync(string userId, string organizationId, string projectId, string databaseId, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Delete, Database(organizationId, projectId, databaseId), userId, organizationId, null, cancellationToken);

    /// <summary>Creates database <paramref name="name"/> in the same project, holding a consistent snapshot copy of <paramref name="databaseId"/>.</summary>
    public Task<TerraBaseManagedDatabase> BranchDatabaseAsync(
        string userId, string organizationId, string projectId, string databaseId, string name, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, Database(organizationId, projectId, databaseId) + "/branches", userId, organizationId, new RequestBody { Name = name }, Json.TerraBaseManagedDatabase, cancellationToken);

    // Members

    /// <summary>Lists an organization's members in user id order.</summary>
    public Task<TerraBasePage<TerraBaseMember>> ListMembersAsync(
        string userId, string organizationId, string? after = null, int? limit = null, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Get, Organization(organizationId) + "/members" + Page(after, limit), userId, organizationId, null, Json.TerraBasePageTerraBaseMember, cancellationToken);

    /// <summary>Changes a member's role. Needs admin, and owner when the member is or becomes an owner; the last owner cannot be demoted.</summary>
    public Task<TerraBaseMember> ChangeMemberRoleAsync(
        string userId, string organizationId, string memberUserId, TerraBaseRole role, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, Member(organizationId, memberUserId) + "/role", userId, organizationId, new RequestBody { Role = role }, Json.TerraBaseMember, cancellationToken);

    /// <summary>Removes a member. Needs admin, and owner to remove an owner; the last owner cannot be removed.</summary>
    public Task RemoveMemberAsync(string userId, string organizationId, string memberUserId, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Delete, Member(organizationId, memberUserId), userId, organizationId, null, cancellationToken);

    /// <summary>Removes the acting user from the organization, unless it is the last owner.</summary>
    public Task LeaveOrganizationAsync(string userId, string organizationId, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, Organization(organizationId) + "/leave", userId, organizationId, null, cancellationToken);

    // Invitations

    /// <summary>
    /// Invites an email address (role defaults to member). Needs admin, and owner to invite an owner. The token is
    /// returned only here; TerraBase sends no email.
    /// </summary>
    public Task<TerraBaseCreatedInvitation> CreateInvitationAsync(
        string userId, string organizationId, string email, TerraBaseRole? role = null, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, Organization(organizationId) + "/invitations", userId, organizationId, new RequestBody { Email = email, Role = role }, Json.TerraBaseCreatedInvitation, cancellationToken);

    /// <summary>Lists pending invitations, expired ones included, in creation order. Needs admin.</summary>
    public Task<TerraBasePage<TerraBaseInvitation>> ListInvitationsAsync(
        string userId, string organizationId, string? after = null, int? limit = null, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Get, Organization(organizationId) + "/invitations" + Page(after, limit), userId, organizationId, null, Json.TerraBasePageTerraBaseInvitation, cancellationToken);

    /// <summary>Revokes an invitation; its token stops working. Needs admin.</summary>
    public Task RevokeInvitationAsync(string userId, string organizationId, string invitationId, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Delete, Organization(organizationId) + "/invitations/" + TerraBaseConnection.Segment(invitationId, nameof(invitationId)), userId, organizationId, null, cancellationToken);

    /// <summary>
    /// Makes the acting user a member through an invitation token. <paramref name="email"/> is the user's verified
    /// address and must match the invitation.
    /// </summary>
    public Task<TerraBaseMember> AcceptInvitationAsync(string userId, string token, string email, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        return SendAsync(HttpMethod.Post, Root + "/invitations/accept", userId, null, new RequestBody { Token = token, Email = email }, Json.TerraBaseMember, cancellationToken);
    }

    // Ownership transfer

    /// <summary>Reads the pending ownership transfer; <see cref="TerraBaseNotFoundException"/> when none.</summary>
    public Task<TerraBaseOwnershipTransfer> GetOwnershipTransferAsync(string userId, string organizationId, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Get, Organization(organizationId) + "/ownership-transfer", userId, organizationId, null, Json.TerraBaseOwnershipTransfer, cancellationToken);

    /// <summary>Offers ownership to a member who is not an owner, replacing a pending offer. Needs owner.</summary>
    public Task<TerraBaseOwnershipTransfer> StartOwnershipTransferAsync(
        string userId, string organizationId, string toUserId, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, Organization(organizationId) + "/ownership-transfer", userId, organizationId, new RequestBody { UserId = toUserId }, Json.TerraBaseOwnershipTransfer, cancellationToken);

    /// <summary>The nominee accepts: it becomes an owner and the owner who started the transfer becomes a member.</summary>
    public Task<TerraBaseMember> ConfirmOwnershipTransferAsync(string userId, string organizationId, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, Organization(organizationId) + "/ownership-transfer/confirm", userId, organizationId, null, Json.TerraBaseMember, cancellationToken);

    /// <summary>Cancels the pending transfer. Needs owner, or to be the nominee.</summary>
    public Task CancelOwnershipTransferAsync(string userId, string organizationId, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Delete, Organization(organizationId) + "/ownership-transfer", userId, organizationId, null, cancellationToken);

    // Audit

    /// <summary>Reads how long audit events are kept. Needs admin.</summary>
    public Task<TerraBaseAuditRetention> GetAuditRetentionAsync(string userId, string organizationId, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Get, Organization(organizationId) + "/audit-retention", userId, organizationId, null, Json.TerraBaseAuditRetention, cancellationToken);

    /// <summary>Sets how long audit events are kept, 1 to 90 days. Needs admin.</summary>
    public Task<TerraBaseAuditRetention> SetAuditRetentionAsync(string userId, string organizationId, int days, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, Organization(organizationId) + "/audit-retention", userId, organizationId, new RequestBody { Days = days }, Json.TerraBaseAuditRetention, cancellationToken);

    /// <summary>Lists audit events within the retention, newest first. Needs admin.</summary>
    public Task<TerraBasePage<TerraBaseAuditEvent>> ListAuditEventsAsync(
        string userId, string organizationId, string? after = null, int? limit = null, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Get, Organization(organizationId) + "/audit-events" + Page(after, limit), userId, organizationId, null, Json.TerraBasePageTerraBaseAuditEvent, cancellationToken);

    private async Task<T> SendAsync<T>(
        HttpMethod method, string path, string userId, string? organizationId, RequestBody? body, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
    {
        var headers = Identity(userId, organizationId);
        var content = body is null ? null : TerraBaseConnection.Json(body, Json.RequestBody);
        return (await connection.SendAsync(method, path, content, typeInfo, headers, cancellationToken).ConfigureAwait(false)).Value;
    }

    private Task SendAsync(HttpMethod method, string path, string userId, string? organizationId, RequestBody? body, CancellationToken cancellationToken)
    {
        var headers = Identity(userId, organizationId);
        return connection.SendAsync(method, path, body is null ? null : TerraBaseConnection.Json(body, Json.RequestBody), headers, cancellationToken);
    }

    private static Action<HttpRequestHeaders> Identity(string userId, string? organizationId)
    {
        if (string.IsNullOrEmpty(userId) || userId.Length > MaxUserIdLength || userId.Any(char.IsControl))
        {
            throw new ArgumentException("userId must be a ts-auth user id of 1 to 256 characters without control characters.", nameof(userId));
        }

        if (organizationId is not null && (organizationId.Length == 0 || organizationId.Any(char.IsControl)))
        {
            throw new ArgumentException("organizationId must be non-empty and contain no control characters.", nameof(organizationId));
        }

        return headers =>
        {
            headers.TryAddWithoutValidation(UserHeader, userId);
            if (organizationId is not null)
            {
                headers.TryAddWithoutValidation(OrganizationHeader, organizationId);
            }
        };
    }

    private static string Page(string? after, int? limit) =>
        TerraBaseConnection.Query(("after", after), ("limit", TerraBaseConnection.Number(limit)));

    private static string Organization(string organizationId) => $"{Root}/organizations/{TerraBaseConnection.Segment(organizationId, nameof(organizationId))}";

    private static string Project(string organizationId, string projectId) =>
        $"{Organization(organizationId)}/projects/{TerraBaseConnection.Segment(projectId, nameof(projectId))}";

    private static string Database(string organizationId, string projectId, string databaseId) =>
        $"{Project(organizationId, projectId)}/databases/{TerraBaseConnection.Segment(databaseId, nameof(databaseId))}";

    private static string Member(string organizationId, string memberUserId) =>
        $"{Organization(organizationId)}/members/{TerraBaseConnection.Segment(memberUserId, nameof(memberUserId))}";
}
