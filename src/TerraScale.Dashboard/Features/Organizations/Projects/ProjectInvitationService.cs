using TerraScale.Dashboard.Features.Auth;
using TerraScale.DotNet;

namespace TerraScale.Dashboard.Features.Organizations.Projects;

public sealed class ProjectInvitationService(AccountService accounts, ITsAuthPlatform platform)
{
    public async Task<TerraScaleAuthPlatformPage<TerraScaleAuthPlatformInvitation>> ReadAsync(AccountContext context,
        string? after, CancellationToken cancellationToken)
    {
        context = await AccountScope.RefreshAsync(accounts, context, cancellationToken, requireProject: true);
        if (!context.CanWriteProjectData || context.Project!.Role != "admin")
            throw new AccountAccessException("A project admin is required to manage project invitations.", 403);
        if (after is not null && !ValidCursor(after)) throw new AccountAccessException("The page cursor is invalid. Open the first page.", 400);
        var page = await platform.ListProjectInvitationsAsync(context.Session, context.Organization!.Id, context.Project.Id, after, cancellationToken);
        if (page.Items is null || page.Items.Count > 25 || page.Items.Any(item => item is null || !TerraScaleIds.IsValid(item.Id) ||
            item.ProjectId != context.Project.Id || item.Role is not ("viewer" or "member" or "admin") ||
            item.Status is not ("pending" or "expired" or "accepted" or "declined" or "revoked") || string.IsNullOrWhiteSpace(item.Email) ||
            !TerraScaleIds.IsValid(item.InvitedBy) || item.CreatedAt == default || item.ExpiresAt <= item.CreatedAt) ||
            page.Items.Select(item => item.Id).Distinct().Count() != page.Items.Count ||
            page.Next is not null && (!ValidCursor(page.Next) || page.Next == after))
            throw new IOException("The project invitation page is invalid.");
        return page;
    }

    public async Task RevokeAsync(AccountContext context, string invitationId, string? after, CancellationToken cancellationToken)
    {
        var page = await ReadAsync(context, after, cancellationToken);
        if (!TerraScaleIds.IsValid(invitationId)) throw new AccountAccessException("Choose a valid invitation from this project page.", 422);
        // The provider authorizes the invitation's immutable actual project. Bind it to this
        // selected project first, including when the caller administers multiple projects.
        var invitation = page.Items.SingleOrDefault(item => item.Id == invitationId)
            ?? throw new AccountAccessException("This invitation is unavailable on the selected project page. Reload before retrying.", 404);
        if (invitation.Status is not ("pending" or "expired"))
            throw new AccountAccessException("This invitation is no longer pending. Reload its current status.", 409);
        await platform.RevokeInvitationAsync(context.Session, context.Organization!.Id, invitation.Id, cancellationToken);
    }

    private static bool ValidCursor(string value) => value.Length is > 0 and <= 512 && !value.Any(char.IsControl);
}
