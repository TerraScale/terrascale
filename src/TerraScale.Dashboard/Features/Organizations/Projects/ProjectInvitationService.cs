using TerraScale.Dashboard.Errors;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Platform.TsAuth;

namespace TerraScale.Dashboard.Features.Organizations.Projects
{
    using NotFound = TerraScale.Dashboard.Errors.NotFound;

    public readonly union ProjectInvitationPageResult(TsAuthPlatformPage<TsAuthPlatformInvitation>, Forbidden, Validation, Unavailable);

    public readonly union ProjectInvitationRevocation(Done, Forbidden, Validation, Unavailable, InvalidInvitation, NotFound, Conflict);

    public sealed record InvalidInvitation(string Message);

    public sealed class ProjectInvitationService(AccountService accounts, ITsAuthPlatform platform)
    {
        public async Task<ProjectInvitationPageResult> ReadAsync(AccountContext context,
            string? after, CancellationToken cancellationToken)
        {
            context = await AccountScope.RefreshAsync(accounts, context, cancellationToken, requireProject: true);
            if (!context.CanWriteProjectData || context.Project!.Role != "admin")
            {
                return new Forbidden("A project admin is required to manage project invitations.");
            }

            if (after is not null && !ValidCursor(after))
            {
                return new Validation("The page cursor is invalid. Open the first page.");
            }

            var page = await platform.ListProjectInvitationsAsync(context.Session, context.Organization!.Id, context.Project.Id, after, cancellationToken);
            if (page.Items is null || page.Items.Count > 25 || page.Items.Any(item => item is null || !TerraScaleIds.IsValid(item.Id) ||
                item.ProjectId != context.Project.Id || item.Role is not ("viewer" or "member" or "admin") ||
                item.Status is not ("pending" or "expired" or "accepted" or "declined" or "revoked") || string.IsNullOrWhiteSpace(item.Email) ||
                !TerraScaleIds.IsValid(item.InvitedBy) || item.CreatedAt == default || item.ExpiresAt <= item.CreatedAt) ||
                page.Items.Select(item => item.Id).Distinct().Count() != page.Items.Count ||
                page.Next is not null && (!ValidCursor(page.Next) || page.Next == after))
            {
                return new Unavailable("The project invitation page is invalid.");
            }

            return page;
        }

        public async Task<ProjectInvitationRevocation> RevokeAsync(AccountContext context, string invitationId, string? after, CancellationToken cancellationToken)
        {
            return await ReadAsync(context, after, cancellationToken) switch
            {
                Forbidden error => error,
                Validation error => error,
                Unavailable error => error,
                TsAuthPlatformPage<TsAuthPlatformInvitation> listed => await RevokeAsync(context, listed, invitationId, cancellationToken),
            };
        }

        private async Task<ProjectInvitationRevocation> RevokeAsync(AccountContext context,
            TsAuthPlatformPage<TsAuthPlatformInvitation> page, string invitationId, CancellationToken cancellationToken)
        {
            if (!TerraScaleIds.IsValid(invitationId))
            {
                return new InvalidInvitation("Choose a valid invitation from this project page.");
            }
            // The provider authorizes the invitation's immutable actual project. Bind it to this
            // selected project first, including when the caller administers multiple projects.
            var invitation = page.Items.SingleOrDefault(item => item.Id == invitationId);
            if (invitation is null)
            {
                return new NotFound("This invitation is unavailable on the selected project page. Reload before retrying.");
            }

            if (invitation.Status is not ("pending" or "expired"))
            {
                return new Conflict("This invitation is no longer pending. Reload its current status.");
            }

            await platform.RevokeInvitationAsync(context.Session, context.Organization!.Id, invitation.Id, cancellationToken);
            return new Done();
        }

        private static bool ValidCursor(string value)
        {
            return value.Length is > 0 and <= 512 && !value.Any(char.IsControl);
        }
    }
}
