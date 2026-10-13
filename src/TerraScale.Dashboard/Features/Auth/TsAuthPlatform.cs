using System.Net;
using TerraScale.Dashboard.Features.Accounts;
using TerraScale.DotNet;

namespace TerraScale.Dashboard.Features.Auth;

// ts-auth owns organizations, projects, memberships, invitations, ownership
// transfer and audit events. Every call carries the signed-in user's own
// access token, so ts-auth checks the current membership and role each time.
public interface ITsAuthPlatform
{
    Task<IReadOnlyList<TerraScaleAuthPlatformOrganization>> ListOrganizationsAsync(SessionRecord session, CancellationToken cancellationToken);
    Task<TerraScaleAuthPlatformOrganization> CreateOrganizationAsync(SessionRecord session, string name, CancellationToken cancellationToken);
    Task RenameOrganizationAsync(SessionRecord session, string organizationId, string name, CancellationToken cancellationToken);
    Task LeaveOrganizationAsync(SessionRecord session, string organizationId, CancellationToken cancellationToken);
    Task<IReadOnlyList<TerraScaleAuthPlatformMember>> ListMembersAsync(SessionRecord session, string organizationId, CancellationToken cancellationToken);
    Task ChangeMemberRoleAsync(SessionRecord session, string organizationId, string userId, string role, CancellationToken cancellationToken);
    Task RemoveMemberAsync(SessionRecord session, string organizationId, string userId, CancellationToken cancellationToken);
    Task<IReadOnlyList<TerraScaleAuthPlatformProject>> ListProjectsAsync(SessionRecord session, string organizationId, CancellationToken cancellationToken);
    Task<TerraScaleAuthPlatformProject> CreateProjectAsync(SessionRecord session, string organizationId, string name, CancellationToken cancellationToken);
    Task RenameProjectAsync(SessionRecord session, string organizationId, string projectId, string name, CancellationToken cancellationToken);
    Task DeleteProjectAsync(SessionRecord session, string organizationId, string projectId, CancellationToken cancellationToken);
    Task<IReadOnlyList<TerraScaleAuthPlatformInvitation>> ListInvitationsAsync(SessionRecord session, string organizationId, CancellationToken cancellationToken);
    Task<TerraScaleAuthPlatformCreatedInvitation> CreateInvitationAsync(SessionRecord session, string organizationId, string email, string role, CancellationToken cancellationToken);
    Task RevokeInvitationAsync(SessionRecord session, string organizationId, string invitationId, CancellationToken cancellationToken);
    Task<TerraScaleAuthPlatformInvitationLookup?> LookupInvitationAsync(SessionRecord session, string token, CancellationToken cancellationToken);
    Task<TerraScaleAuthPlatformAcceptedInvitation> AcceptInvitationAsync(SessionRecord session, string token, CancellationToken cancellationToken);
    Task DeclineInvitationAsync(SessionRecord session, string token, CancellationToken cancellationToken);
    Task<TerraScaleAuthPlatformOwnershipTransfer?> GetOwnershipTransferAsync(SessionRecord session, string organizationId, CancellationToken cancellationToken);
    Task StartOwnershipTransferAsync(SessionRecord session, string organizationId, string userId, CancellationToken cancellationToken);
    Task ConfirmOwnershipTransferAsync(SessionRecord session, string organizationId, CancellationToken cancellationToken);
    Task CancelOwnershipTransferAsync(SessionRecord session, string organizationId, CancellationToken cancellationToken);
    Task<IReadOnlyList<TerraScaleAuthPlatformAuditEvent>> ListAuditEventsAsync(SessionRecord session, string organizationId, CancellationToken cancellationToken);
}

// The official ts-dotnet platform client, called with the user's own sign-in token.
public sealed partial class TsAuthGateway : ITsAuthPlatform
{
    private const int MaxPages = 100;

    public Task<IReadOnlyList<TerraScaleAuthPlatformOrganization>> ListOrganizationsAsync(SessionRecord session, CancellationToken cancellationToken) =>
        PagesAsync(after => Client().Platform.ListOrganizationsAsync(Bearer(session), 100, after, cancellationToken));

    public Task<TerraScaleAuthPlatformOrganization> CreateOrganizationAsync(SessionRecord session, string name, CancellationToken cancellationToken) =>
        PlatformAsync(() => Client().Platform.CreateOrganizationAsync(Bearer(session), new(name), cancellationToken));

    public Task RenameOrganizationAsync(SessionRecord session, string organizationId, string name, CancellationToken cancellationToken) =>
        PlatformAsync(() => Client().Platform.RenameOrganizationAsync(Bearer(session), Id(organizationId), new(name), cancellationToken));

    public Task LeaveOrganizationAsync(SessionRecord session, string organizationId, CancellationToken cancellationToken) =>
        PlatformAsync(() => Client().Platform.LeaveOrganizationAsync(Bearer(session), Id(organizationId), cancellationToken));

    public Task<IReadOnlyList<TerraScaleAuthPlatformMember>> ListMembersAsync(SessionRecord session, string organizationId, CancellationToken cancellationToken) =>
        PagesAsync(after => Client().Platform.ListMembersAsync(Bearer(session), Id(organizationId), 100, after, cancellationToken));

    public Task ChangeMemberRoleAsync(SessionRecord session, string organizationId, string userId, string role, CancellationToken cancellationToken) =>
        PlatformAsync(() => Client().Platform.ChangeMemberRoleAsync(Bearer(session), Id(organizationId), Id(userId), new(role), cancellationToken));

    public Task RemoveMemberAsync(SessionRecord session, string organizationId, string userId, CancellationToken cancellationToken) =>
        PlatformAsync(() => Client().Platform.RemoveMemberAsync(Bearer(session), Id(organizationId), Id(userId), cancellationToken));

    public Task<IReadOnlyList<TerraScaleAuthPlatformProject>> ListProjectsAsync(SessionRecord session, string organizationId, CancellationToken cancellationToken) =>
        PagesAsync(after => Client().Platform.ListProjectsAsync(Bearer(session), Id(organizationId), 100, after, cancellationToken));

    public async Task<TerraScaleAuthPlatformProject> CreateProjectAsync(SessionRecord session, string organizationId, string name, CancellationToken cancellationToken)
    {
        var project = await PlatformAsync(() => Client().Platform.CreateProjectAsync(Bearer(session), Id(organizationId), new(name), cancellationToken));
        if (!TerraScaleIds.IsValid(project.Id) || project.OrganizationId != organizationId) throw new IOException("ts-auth returned an invalid project.");
        return project;
    }

    public Task RenameProjectAsync(SessionRecord session, string organizationId, string projectId, string name, CancellationToken cancellationToken) =>
        PlatformAsync(() => Client().Platform.RenameProjectAsync(Bearer(session), Id(organizationId), Id(projectId), new(name), cancellationToken));

    public Task DeleteProjectAsync(SessionRecord session, string organizationId, string projectId, CancellationToken cancellationToken) =>
        PlatformAsync(() => Client().Platform.DeleteProjectAsync(Bearer(session), Id(organizationId), Id(projectId), cancellationToken));

    public Task<IReadOnlyList<TerraScaleAuthPlatformInvitation>> ListInvitationsAsync(SessionRecord session, string organizationId, CancellationToken cancellationToken) =>
        PagesAsync(after => Client().Platform.ListInvitationsAsync(Bearer(session), Id(organizationId), 100, after, null, cancellationToken));

    public async Task<TerraScaleAuthPlatformCreatedInvitation> CreateInvitationAsync(SessionRecord session, string organizationId, string email, string role, CancellationToken cancellationToken)
    {
        var created = await PlatformAsync(() => Client().Platform.CreateInvitationAsync(Bearer(session), Id(organizationId), new(email, role), cancellationToken));
        if (created.Invitation is null || !TerraScaleIds.IsValid(created.Invitation.Id) || created.Token is not { Length: > 0 and <= 4096 } ||
            !created.Invitation.Email.Equals(email, StringComparison.OrdinalIgnoreCase))
            throw new IOException("ts-auth returned an invalid invitation.");
        return created;
    }

    public Task RevokeInvitationAsync(SessionRecord session, string organizationId, string invitationId, CancellationToken cancellationToken) =>
        PlatformAsync(() => Client().Platform.RevokeInvitationAsync(Bearer(session), Id(organizationId), Id(invitationId), cancellationToken));

    public async Task<TerraScaleAuthPlatformInvitationLookup?> LookupInvitationAsync(SessionRecord session, string token, CancellationToken cancellationToken)
    {
        try { return await PlatformAsync(() => Client().Platform.LookupInvitationAsync(Bearer(session), new(token), cancellationToken)); }
        catch (AccountAccessException error) when (error.StatusCode == StatusCodes.Status404NotFound) { return null; }
    }

    public Task<TerraScaleAuthPlatformAcceptedInvitation> AcceptInvitationAsync(SessionRecord session, string token, CancellationToken cancellationToken) =>
        PlatformAsync(() => Client().Platform.AcceptInvitationAsync(Bearer(session), new(token), cancellationToken));

    public Task DeclineInvitationAsync(SessionRecord session, string token, CancellationToken cancellationToken) =>
        PlatformAsync(() => Client().Platform.DeclineInvitationAsync(Bearer(session), new(token), cancellationToken));

    public async Task<TerraScaleAuthPlatformOwnershipTransfer?> GetOwnershipTransferAsync(SessionRecord session, string organizationId, CancellationToken cancellationToken)
    {
        try { return await PlatformAsync(() => Client().Platform.GetOwnershipTransferAsync(Bearer(session), Id(organizationId), cancellationToken)); }
        catch (AccountAccessException error) when (error.StatusCode == StatusCodes.Status404NotFound) { return null; }
    }

    public Task StartOwnershipTransferAsync(SessionRecord session, string organizationId, string userId, CancellationToken cancellationToken) =>
        PlatformAsync(() => Client().Platform.StartOwnershipTransferAsync(Bearer(session), Id(organizationId), new(Id(userId)), cancellationToken));

    public Task ConfirmOwnershipTransferAsync(SessionRecord session, string organizationId, CancellationToken cancellationToken) =>
        PlatformAsync(() => Client().Platform.ConfirmOwnershipTransferAsync(Bearer(session), Id(organizationId), cancellationToken));

    public Task CancelOwnershipTransferAsync(SessionRecord session, string organizationId, CancellationToken cancellationToken) =>
        PlatformAsync(() => Client().Platform.CancelOwnershipTransferAsync(Bearer(session), Id(organizationId), cancellationToken));

    public Task<IReadOnlyList<TerraScaleAuthPlatformAuditEvent>> ListAuditEventsAsync(SessionRecord session, string organizationId, CancellationToken cancellationToken) =>
        PagesAsync(after => Client().Platform.ListAuditEventsAsync(Bearer(session), Id(organizationId), 100, after, cancellationToken));

    private static string Bearer(SessionRecord session) =>
        string.IsNullOrWhiteSpace(session.AccessToken) ? throw new AccountAccessException("Sign in again to continue.", 401) : session.AccessToken;

    // ts-auth answers 404 to anything but a canonical ULID; refuse it before sending a bearer token.
    private static string Id(string id) => TerraScaleIds.IsValid(id) ? id : throw new AccountAccessException("The requested resource is unavailable.", 404);

    private static async Task<IReadOnlyList<T>> PagesAsync<T>(Func<string?, Task<TerraScaleAuthPlatformPage<T>>> page)
    {
        var items = new List<T>();
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? after = null;
        for (var count = 0; count < MaxPages; count++)
        {
            var result = await PlatformAsync(() => page(after));
            items.AddRange(result.Items ?? throw new IOException("ts-auth returned an invalid page."));
            if (result.Next is null) return items;
            if (!cursors.Add(result.Next)) throw new IOException("ts-auth pagination did not advance.");
            after = result.Next;
        }
        throw new IOException("ts-auth returned too many pages.");
    }

    private static async Task PlatformAsync(Func<Task> call) => await PlatformAsync(async () => { await call(); return true; });

    // ts-auth refusals become account errors with its fixed code and a dashboard message; the SDK
    // keeps no producer text. Server and transport failures stay HttpRequestException (503 page).
    private static async Task<T> PlatformAsync<T>(Func<Task<T>> call)
    {
        try { return await call(); }
        catch (TerraScaleAuthException error) when ((int?)error.StatusCode is >= 400 and < 500)
        {
            var status = (int)error.StatusCode!;
            throw new AccountAccessException(Message(error.ErrorCode, error.StatusCode!.Value), status == 400 ? StatusCodes.Status422UnprocessableEntity : status, error.ErrorCode ?? "");
        }
    }

    private static string Message(string? code, HttpStatusCode status) => code switch
    {
        "name_taken" => "That name is already used in this organization.",
        "last_owner" => "The organization needs another owner first.",
        "not_empty" => "Delete the organization's projects first.",
        "invitation_pending" => "This email already has a pending invitation.",
        "already_member" => "This email is already an active member.",
        "email_mismatch" => "Sign in with the invited email to answer this invitation.",
        "invitation_expired" or "not_pending" => "Invitation is unavailable.",
        _ => status switch
        {
            HttpStatusCode.Unauthorized => "Sign in again to continue.",
            HttpStatusCode.Forbidden => "Your role does not allow this change.",
            HttpStatusCode.NotFound => "The requested resource is unavailable.",
            HttpStatusCode.Conflict => "The change conflicts with the current state.",
            HttpStatusCode.TooManyRequests => "Too many requests. Try again shortly.",
            _ => "The request was rejected."
        }
    };
}
