using System.Net.Http.Headers;
using System.Text.Json.Serialization.Metadata;

namespace TerraScale.DotNet.TerraBase;

/// <summary>Configures a <see cref="TerraBaseManagementClient"/>.</summary>
public sealed record TerraBaseManagementClientOptions
{
    /// <summary>
    /// TerraBase origin: the public HTTPS listener (with <see cref="AccessTokenProvider"/>) or the private plain-HTTP listener
    /// on NetBird <c>100.64.0.0/10</c> addresses or loopback (<c>http://100.64.0.1:8081</c>).
    /// </summary>
    public Uri? BaseAddress { get; init; }

    /// <summary>Resolves the origin per request. Set this or BaseAddress, exclusively.</summary>
    public ITerraBaseEndpointResolver? EndpointResolver { get; init; }

    /// <summary>
    /// A region id such as <c>gru</c>: every request is pinned to that region (<c>X-TerraScale-Region-Pin</c>) and, unless
    /// BaseAddress or EndpointResolver is set, sent to <c>https://api.terrascale.tech</c>. Another region
    /// answers <see cref="TerraBaseWrongRegionException"/> instead of serving it. Unset, requests use the configured origin without a region pin.
    /// </summary>
    public string? Region { get; init; }

    /// <summary>
    /// Supplies the acting user's ts-auth access token per request, for TerraBase's public HTTPS listener. Leave it unset
    /// for TerraBase's private plain-HTTP listener, which trusts its network and acts as the platform operator.
    /// The provider must be thread-safe and honor cancellation; acquisition shares the request deadline.
    /// </summary>
    public Func<CancellationToken, ValueTask<string>>? AccessTokenProvider { get; init; }

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
        connection = new TerraBaseConnection(httpClient, options.BaseAddress, options.EndpointResolver, null, nameof(options.AccessTokenProvider), TerraBaseConnection.RequestDeadline, TerraBaseConnection.DefaultMaxResponseBytes,
            options.AccessTokenProvider, anonymousAllowed: true, region: options.Region);
    }

    internal TerraBaseManagementClient(TerraBaseConnection connection) => this.connection = connection;

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

    /// <summary>
    /// ts-auth's project closure handoff (TerraBase docs/terrabase/project-closure.md): fences <paramref name="projectId"/>
    /// permanently against new databases and enrollment, or reads the fence's status. Version 1 accepts only empty projects.
    /// Served only on TerraBase's private listener behind the peer credential, so use a client without
    /// <see cref="TerraBaseManagementClientOptions.AccessTokenProvider"/>;
    /// no user acts. Idempotent: retry with the same <paramref name="closureId"/> after a lost response. A refusal writes
    /// nothing and throws <see cref="TerraBaseConflictException"/> (<c>not_empty</c>, <c>closure_conflict</c> or
    /// <c>binding_conflict</c>). Any other failure, a 404 included, proves nothing about the project.
    /// </summary>
    public Task<TerraBaseProjectClosure> CloseProjectAsync(string projectId, string authorityOrganizationId, string closureId, CancellationToken cancellationToken = default) =>
        SendProjectClosureAsync(projectId, authorityOrganizationId, closureId, 1, cancellationToken);

    /// <summary>
    /// ts-auth's version 2 project erasure handoff for explicit account-deletion consent: authorizes a permanent project
    /// fence and TerraBase's purge of the project's databases regardless of replication, TerraBase API keys and backup
    /// destinations. Served only on TerraBase's private listener behind the peer credential; use a client without
    /// <see cref="TerraBaseManagementClientOptions.AccessTokenProvider"/>. The caller must obtain consent before calling.
    /// Idempotent: retry with the same <paramref name="closureId"/> after a lost response. A pending response is not proof
    /// of completed erasure. Any failure, a 404 included, proves nothing about completion.
    /// </summary>
    public Task<TerraBaseProjectClosure> EraseProjectAsync(string projectId, string authorityOrganizationId, string closureId, CancellationToken cancellationToken = default) =>
        SendProjectClosureAsync(projectId, authorityOrganizationId, closureId, 2, cancellationToken);

    private async Task<TerraBaseProjectClosure> SendProjectClosureAsync(string projectId, string authorityOrganizationId, string closureId, int version, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(authorityOrganizationId);
        ArgumentException.ThrowIfNullOrEmpty(closureId);
        var body = TerraBaseConnection.Json(new RequestBody { Version = version, ClosureId = closureId, AuthorityOrganizationId = authorityOrganizationId }, Json.RequestBody);
        var (closure, status) = await connection.SendAsync(HttpMethod.Put, "/admin/projects/" + TerraBaseConnection.Segment(projectId, nameof(projectId)) + "/closure",
            body, Json.TerraBaseProjectClosure, null, cancellationToken).ConfigureAwait(false);
        // A body that does not answer this closure, or contradicts its status code, is not evidence either way.
        return closure.ClosureId == closureId && (status, closure.Status) is (System.Net.HttpStatusCode.OK, "accepted") or (System.Net.HttpStatusCode.Accepted, "pending")
            ? closure
            : throw new TerraBaseException(status, "TerraBase answered a project closure with a status or closure id that does not match the request.", "invalid_response", null);
    }

    /// <summary>
    /// ts-auth's shared-history erasure handoff (TerraBase docs/terrabase/history-erasure.md): creates or advances the permanent
    /// erasure <paramref name="erasureId"/> (the random id of ts-auth's deletion job, whose request document names the fence) over ts-auth's
    /// database and TerraBase's SYSTEM, and returns its status computed from every holder. Served only on TerraBase's private
    /// listener behind the peer credential; the scope is fixed, no database is named. Always present the original
    /// <paramref name="requestedAt"/>. Present <paramref name="settlementToken"/> only after re-verifying auth's cleanup against the
    /// same TerraBase that issued it. A stale token or another request time throws <see cref="TerraBaseConflictException"/>
    /// (<c>settlement_stale</c>, <c>erasure_conflict</c>). Any other failure, a 404 included, proves nothing.
    /// </summary>
    public async Task<TerraBaseHistoryErasure> RequestHistoryErasureAsync(string erasureId, DateTimeOffset requestedAt, string? settlementToken = null, CancellationToken cancellationToken = default)
    {
        var body = TerraBaseConnection.Json(new RequestBody { Version = 1, RequestedAt = requestedAt, SettlementToken = settlementToken }, Json.RequestBody);
        var (erasure, status) = await connection.SendAsync(HttpMethod.Put, "/admin/history-erasures/" + TerraBaseConnection.Segment(erasureId, nameof(erasureId)),
            body, Json.TerraBaseHistoryErasure, null, cancellationToken).ConfigureAwait(false);
        // A body that does not answer this erasure, or contradicts its status code, is not evidence either way.
        return erasure.ErasureId == erasureId && (status == System.Net.HttpStatusCode.OK) == erasure.Complete
            && status is System.Net.HttpStatusCode.OK or System.Net.HttpStatusCode.Accepted
            ? erasure
            : throw new TerraBaseException(status, "TerraBase answered a history erasure with a status or erasure id that does not match the request.", "invalid_response", null);
    }

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

    /// <summary>Lists all invitations with their status, expired ones included, in creation order. Needs admin.</summary>
    public Task<TerraBasePage<TerraBaseInvitation>> ListInvitationsAsync(
        string userId, string organizationId, string? after = null, int? limit = null, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Get, Organization(organizationId) + "/invitations" + Page(after, limit), userId, organizationId, null, Json.TerraBasePageTerraBaseInvitation, cancellationToken);

    /// <summary>Revokes an invitation; its token stops working. Needs admin.</summary>
    public Task RevokeInvitationAsync(string userId, string organizationId, string invitationId, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Delete, Organization(organizationId) + "/invitations/" + TerraBaseConnection.Segment(invitationId, nameof(invitationId)), userId, organizationId, null, cancellationToken);

    /// <summary>
    /// Makes the acting user a member through an invitation token. TerraBase matches the invitation against the verified
    /// email of the user's ts-auth access token, so the management service credential, which carries none, gets
    /// <see cref="TerraBaseForbiddenException"/>; use <see cref="AddMemberAsync"/> there.
    /// </summary>
    public Task<TerraBaseMember> AcceptInvitationAsync(string userId, string token, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        return SendAsync(HttpMethod.Post, Root + "/invitations/accept", userId, null, new RequestBody { Token = token }, Json.TerraBaseMember, cancellationToken);
    }

    /// <summary>
    /// Adds <paramref name="memberUserId"/> as a member without an invitation; an existing member is returned unchanged.
    /// Needs admin. Served only on TerraBase's loopback administrative listener with the management service credential;
    /// <paramref name="email"/> is recorded in the audit log only.
    /// </summary>
    public Task<TerraBaseMember> AddMemberAsync(
        string userId, string organizationId, string memberUserId, string email, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, Member(organizationId, memberUserId), userId, organizationId, new RequestBody { Email = email }, Json.TerraBaseMember, cancellationToken);

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
