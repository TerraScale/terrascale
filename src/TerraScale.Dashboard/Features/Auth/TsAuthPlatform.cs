using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.DotNet;

namespace TerraScale.Dashboard.Features.Auth;

// ts-auth owns organizations, projects, memberships, invitations, ownership
// transfer and audit events. Every call carries the signed-in user's own
// access token, so ts-auth checks the current membership and role each time.
public interface ITsAuthPlatform
{
    Task<IReadOnlyList<PlatformOrganization>> ListOrganizationsAsync(SessionRecord session, CancellationToken cancellationToken);
    Task<PlatformOrganization> CreateOrganizationAsync(SessionRecord session, string name, CancellationToken cancellationToken);
    Task RenameOrganizationAsync(SessionRecord session, string organizationId, string name, CancellationToken cancellationToken);
    Task LeaveOrganizationAsync(SessionRecord session, string organizationId, CancellationToken cancellationToken);
    Task<IReadOnlyList<PlatformMember>> ListMembersAsync(SessionRecord session, string organizationId, CancellationToken cancellationToken);
    Task ChangeMemberRoleAsync(SessionRecord session, string organizationId, string userId, string role, CancellationToken cancellationToken);
    Task RemoveMemberAsync(SessionRecord session, string organizationId, string userId, CancellationToken cancellationToken);
    Task<IReadOnlyList<PlatformProject>> ListProjectsAsync(SessionRecord session, string organizationId, CancellationToken cancellationToken);
    Task<PlatformProject> CreateProjectAsync(SessionRecord session, string organizationId, string name, CancellationToken cancellationToken);
    Task RenameProjectAsync(SessionRecord session, string organizationId, string projectId, string name, CancellationToken cancellationToken);
    Task DeleteProjectAsync(SessionRecord session, string organizationId, string projectId, CancellationToken cancellationToken);
    Task<IReadOnlyList<PlatformInvitation>> ListInvitationsAsync(SessionRecord session, string organizationId, CancellationToken cancellationToken);
    Task<PlatformCreatedInvitation> CreateInvitationAsync(SessionRecord session, string organizationId, string email, string role, CancellationToken cancellationToken);
    Task RevokeInvitationAsync(SessionRecord session, string organizationId, string invitationId, CancellationToken cancellationToken);
    Task<PlatformInvitationLookup?> LookupInvitationAsync(SessionRecord session, string token, CancellationToken cancellationToken);
    Task<PlatformAcceptedInvitation> AcceptInvitationAsync(SessionRecord session, string token, CancellationToken cancellationToken);
    Task DeclineInvitationAsync(SessionRecord session, string token, CancellationToken cancellationToken);
    Task<PlatformOwnershipTransfer?> GetOwnershipTransferAsync(SessionRecord session, string organizationId, CancellationToken cancellationToken);
    Task StartOwnershipTransferAsync(SessionRecord session, string organizationId, string userId, CancellationToken cancellationToken);
    Task ConfirmOwnershipTransferAsync(SessionRecord session, string organizationId, CancellationToken cancellationToken);
    Task CancelOwnershipTransferAsync(SessionRecord session, string organizationId, CancellationToken cancellationToken);
    Task<IReadOnlyList<PlatformAuditEvent>> ListAuditEventsAsync(SessionRecord session, string organizationId, CancellationToken cancellationToken);
}

// Shapes of terrascale src/TerraScale.Authentication/Features/Platform/{PlatformEndpoints,InvitationEndpoints}.cs.
public sealed record PlatformOrganization(string Id, string Name, string CreatedBy, DateTimeOffset CreatedAt, string? Role);
public sealed record PlatformMember(string UserId, string Email, string Role, DateTimeOffset JoinedAt);
public sealed record PlatformProject(string Id, string OrganizationId, string Name, string CreatedBy, DateTimeOffset CreatedAt, string Role);
public sealed record PlatformInvitation(string Id, string Email, string Role, string? ProjectId, string InvitedBy,
    DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, string Status);
public sealed record PlatformCreatedInvitation(PlatformInvitation Invitation, string Token)
{
    public override string ToString() => "Created invitation [REDACTED]";
}
public sealed record PlatformNamed(string Id, string Name);
public sealed record PlatformInvitationLookup(PlatformNamed Organization, PlatformNamed? Project, string Email, string Role, DateTimeOffset ExpiresAt);
public sealed record PlatformAcceptedInvitation(string OrganizationId, string? ProjectId, string Role);
public sealed record PlatformOwnershipTransfer(string FromUserId, string ToUserId, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);
public sealed record PlatformAuditEvent(string Id, string Actor, string Action, string? Target, string? Detail, DateTimeOffset OccurredAt);

// shortcut: hand-written HTTP for ts-auth's /v1/terrascale platform API, which the pinned SDK lacks; replace with
// the typed ts-dotnet platform client when I1's methods land in the pinned package.
public sealed partial class TsAuthGateway : ITsAuthPlatform
{
    private const string Platform = "/v1/terrascale/organizations";
    private const int MaxPages = 100;
    private static readonly JsonSerializerOptions PlatformWire = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public Task<IReadOnlyList<PlatformOrganization>> ListOrganizationsAsync(SessionRecord session, CancellationToken cancellationToken) =>
        PagesAsync<PlatformOrganization>(session, Platform, cancellationToken);

    public async Task<PlatformOrganization> CreateOrganizationAsync(SessionRecord session, string name, CancellationToken cancellationToken) =>
        Valid(await SendAsync<PlatformOrganization>(session, HttpMethod.Post, Platform, new { name }, cancellationToken));

    public Task RenameOrganizationAsync(SessionRecord session, string organizationId, string name, CancellationToken cancellationToken) =>
        SendAsync<PlatformOrganization>(session, HttpMethod.Patch, Organization(organizationId), new { name }, cancellationToken);

    public Task LeaveOrganizationAsync(SessionRecord session, string organizationId, CancellationToken cancellationToken) =>
        SendAsync(session, HttpMethod.Post, Organization(organizationId) + "/leave", null, cancellationToken);

    public Task<IReadOnlyList<PlatformMember>> ListMembersAsync(SessionRecord session, string organizationId, CancellationToken cancellationToken) =>
        PagesAsync<PlatformMember>(session, Organization(organizationId) + "/members", cancellationToken);

    public Task ChangeMemberRoleAsync(SessionRecord session, string organizationId, string userId, string role, CancellationToken cancellationToken) =>
        SendAsync<PlatformMember>(session, HttpMethod.Put, Organization(organizationId) + "/members/" + Id(userId) + "/role", new { role }, cancellationToken);

    public Task RemoveMemberAsync(SessionRecord session, string organizationId, string userId, CancellationToken cancellationToken) =>
        SendAsync(session, HttpMethod.Delete, Organization(organizationId) + "/members/" + Id(userId), null, cancellationToken);

    public Task<IReadOnlyList<PlatformProject>> ListProjectsAsync(SessionRecord session, string organizationId, CancellationToken cancellationToken) =>
        PagesAsync<PlatformProject>(session, Organization(organizationId) + "/projects", cancellationToken);

    public async Task<PlatformProject> CreateProjectAsync(SessionRecord session, string organizationId, string name, CancellationToken cancellationToken)
    {
        var project = await SendAsync<PlatformProject>(session, HttpMethod.Post, Organization(organizationId) + "/projects", new { name }, cancellationToken);
        if (!TerraScaleIds.IsValid(project.Id) || project.OrganizationId != organizationId) throw new IOException("ts-auth returned an invalid project.");
        return project;
    }

    public Task RenameProjectAsync(SessionRecord session, string organizationId, string projectId, string name, CancellationToken cancellationToken) =>
        SendAsync<PlatformProject>(session, HttpMethod.Patch, Organization(organizationId) + "/projects/" + Id(projectId), new { name }, cancellationToken);

    public Task DeleteProjectAsync(SessionRecord session, string organizationId, string projectId, CancellationToken cancellationToken) =>
        SendAsync(session, HttpMethod.Delete, Organization(organizationId) + "/projects/" + Id(projectId), null, cancellationToken);

    public Task<IReadOnlyList<PlatformInvitation>> ListInvitationsAsync(SessionRecord session, string organizationId, CancellationToken cancellationToken) =>
        PagesAsync<PlatformInvitation>(session, Organization(organizationId) + "/invitations", cancellationToken);

    public async Task<PlatformCreatedInvitation> CreateInvitationAsync(SessionRecord session, string organizationId, string email, string role, CancellationToken cancellationToken)
    {
        var created = await SendAsync<PlatformCreatedInvitation>(session, HttpMethod.Post, Organization(organizationId) + "/invitations", new { email, role }, cancellationToken);
        if (created.Invitation is null || !TerraScaleIds.IsValid(created.Invitation.Id) || created.Token is not { Length: > 0 and <= 4096 } ||
            !created.Invitation.Email.Equals(email, StringComparison.OrdinalIgnoreCase))
            throw new IOException("ts-auth returned an invalid invitation.");
        return created;
    }

    public Task RevokeInvitationAsync(SessionRecord session, string organizationId, string invitationId, CancellationToken cancellationToken) =>
        SendAsync(session, HttpMethod.Delete, Organization(organizationId) + "/invitations/" + Id(invitationId), null, cancellationToken);

    public async Task<PlatformInvitationLookup?> LookupInvitationAsync(SessionRecord session, string token, CancellationToken cancellationToken)
    {
        try { return await SendAsync<PlatformInvitationLookup>(session, HttpMethod.Post, "/v1/terrascale/invitations/lookup", new { token }, cancellationToken); }
        catch (AccountAccessException error) when (error.StatusCode == StatusCodes.Status404NotFound) { return null; }
    }

    public Task<PlatformAcceptedInvitation> AcceptInvitationAsync(SessionRecord session, string token, CancellationToken cancellationToken) =>
        SendAsync<PlatformAcceptedInvitation>(session, HttpMethod.Post, "/v1/terrascale/invitations/accept", new { token }, cancellationToken);

    public Task DeclineInvitationAsync(SessionRecord session, string token, CancellationToken cancellationToken) =>
        SendAsync(session, HttpMethod.Post, "/v1/terrascale/invitations/decline", new { token }, cancellationToken);

    public async Task<PlatformOwnershipTransfer?> GetOwnershipTransferAsync(SessionRecord session, string organizationId, CancellationToken cancellationToken)
    {
        try { return await SendAsync<PlatformOwnershipTransfer>(session, HttpMethod.Get, Organization(organizationId) + "/ownership-transfer", null, cancellationToken); }
        catch (AccountAccessException error) when (error.StatusCode == StatusCodes.Status404NotFound) { return null; }
    }

    public Task StartOwnershipTransferAsync(SessionRecord session, string organizationId, string userId, CancellationToken cancellationToken) =>
        SendAsync<PlatformOwnershipTransfer>(session, HttpMethod.Post, Organization(organizationId) + "/ownership-transfer", new { user_id = userId }, cancellationToken);

    public Task ConfirmOwnershipTransferAsync(SessionRecord session, string organizationId, CancellationToken cancellationToken) =>
        SendAsync<PlatformMember>(session, HttpMethod.Post, Organization(organizationId) + "/ownership-transfer/confirm", null, cancellationToken);

    public Task CancelOwnershipTransferAsync(SessionRecord session, string organizationId, CancellationToken cancellationToken) =>
        SendAsync(session, HttpMethod.Delete, Organization(organizationId) + "/ownership-transfer", null, cancellationToken);

    public Task<IReadOnlyList<PlatformAuditEvent>> ListAuditEventsAsync(SessionRecord session, string organizationId, CancellationToken cancellationToken) =>
        PagesAsync<PlatformAuditEvent>(session, Organization(organizationId) + "/audit-events", cancellationToken);

    private static string Organization(string organizationId) => Platform + "/" + Id(organizationId);

    // ts-auth answers 404 to anything but a canonical ULID; refuse it before sending a bearer token.
    private static string Id(string id) => TerraScaleIds.IsValid(id) ? id : throw new AccountAccessException("The requested resource is unavailable.", 404);

    private static PlatformOrganization Valid(PlatformOrganization organization) =>
        TerraScaleIds.IsValid(organization.Id) ? organization : throw new IOException("ts-auth returned an invalid organization.");

    private async Task<IReadOnlyList<T>> PagesAsync<T>(SessionRecord session, string path, CancellationToken cancellationToken)
    {
        var items = new List<T>();
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? after = null;
        for (var page = 0; page < MaxPages; page++)
        {
            var result = await SendAsync<PlatformPage<T>>(session, HttpMethod.Get,
                path + "?limit=100" + (after is null ? "" : "&after=" + Uri.EscapeDataString(after)), null, cancellationToken);
            items.AddRange(result.Items ?? throw new IOException("ts-auth returned an invalid page."));
            after = result.Next;
            if (after is null) return items;
            if (!cursors.Add(after)) throw new IOException("ts-auth pagination did not advance.");
        }
        throw new IOException("ts-auth returned too many pages.");
    }

    private async Task<T> SendAsync<T>(SessionRecord session, HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        using var response = await PlatformRequestAsync(session, method, path, body, cancellationToken);
        using var document = await PlatformJson.ReadAsync(response.Content, cancellationToken);
        return document.RootElement.Deserialize<T>(PlatformWire) ?? throw new IOException("ts-auth returned an empty answer.");
    }

    private async Task SendAsync(SessionRecord session, HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        using var response = await PlatformRequestAsync(session, method, path, body, cancellationToken);
    }

    private async Task<HttpResponseMessage> PlatformRequestAsync(SessionRecord session, HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(session.AccessToken)) throw new AccountAccessException("Sign in again to continue.", 401);
        using var request = new HttpRequestMessage(method, new Uri(AuthOrigin, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body is not null) request.Content = JsonContent.Create(body, options: PlatformWire);
        var response = await http.CreateClient("platform").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.IsSuccessStatusCode) return response;
        using (response) throw await PlatformErrorAsync(response, cancellationToken);
    }

    // Client errors become user-facing account errors with ts-auth's code; server and
    // transport failures stay HttpRequestException so the dashboard answers 503.
    private static async Task<Exception> PlatformErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var status = (int)response.StatusCode;
        if (status >= 500 || status < 400) return new HttpRequestException("ts-auth is unavailable.", null, response.StatusCode);
        var (code, message) = ("", "");
        try
        {
            using var document = await PlatformJson.ReadAsync(response.Content, cancellationToken);
            if (document.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                code = error.TryGetProperty("code", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
                message = error.TryGetProperty("message", out value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
            }
        }
        catch (JsonException) { }
        if (message is not { Length: > 0 and <= 200 } || message.Any(char.IsControl)) message = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "Sign in again to continue.",
            HttpStatusCode.Forbidden => "Your role does not allow this change.",
            HttpStatusCode.NotFound => "The requested resource is unavailable.",
            HttpStatusCode.Conflict => "The change conflicts with the current state.",
            HttpStatusCode.TooManyRequests => "Too many requests. Try again shortly.",
            _ => "The request was rejected."
        };
        return new AccountAccessException(message, status == 400 ? StatusCodes.Status422UnprocessableEntity : status, code);
    }

    private sealed record PlatformPage<T>(IReadOnlyList<T>? Items, string? Next);
}
