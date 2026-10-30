using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Platform.TsAuth;

namespace TerraScale.Dashboard.Features.Organizations.Projects
{
    public sealed class ProjectGrantService(AccountService accounts, ITsAuthPlatform platform)
    {
        public async Task<TsAuthPlatformPage<TsAuthPlatformProjectMember>> ReadAsync(AccountContext context,
            string? after, CancellationToken cancellationToken)
        {
            context = await AccountScope.RefreshAsync(accounts, context, cancellationToken, requireProject: true);
            if (context.Project!.Role is not ("viewer" or "member" or "admin"))
            {
                throw new AccountAccessException("Project is unavailable.", 404);
            }

            if (after is not null && !ValidCursor(after))
            {
                throw new AccountAccessException("The page cursor is invalid. Open the first page.", 400);
            }

            var page = await platform.ListProjectMembersAsync(context.Session, context.Organization!.Id, context.Project.Id, after, cancellationToken);
            if (page.Items is null || page.Items.Count > 25 || page.Items.Any(item => item is null || !TerraScaleIds.IsValid(item.UserId) ||
                item.Role is not ("viewer" or "member" or "admin")) || page.Items.Select(item => item.UserId).Distinct().Count() != page.Items.Count ||
                page.Next is not null && (!ValidCursor(page.Next) || page.Next == after))
            {
                throw new IOException("The project grant page is invalid.");
            }

            return page;
        }

        private static bool ValidCursor(string value)
        {
            return value.Length is > 0 and <= 512 && !value.Any(char.IsControl);
        }

        public async Task<TsAuthPlatformProjectMember> SetAsync(AccountContext context, string userId, string role, CancellationToken cancellationToken)
        {
            context = await AdminAsync(context, userId, cancellationToken);
            OrganizationOperations.AvailableRole(role);
            var grant = await platform.SetProjectMemberRoleAsync(context.Session, context.Organization!.Id, context.Project!.Id, userId, role, cancellationToken);
            if (grant is null || grant.UserId != userId || grant.Role != role)
            {
                throw new IOException("The project grant response does not match.");
            }

            return grant;
        }

        public async Task RemoveAsync(AccountContext context, string userId, CancellationToken cancellationToken)
        {
            context = await AdminAsync(context, userId, cancellationToken);
            await platform.RemoveProjectMemberAsync(context.Session, context.Organization!.Id, context.Project!.Id, userId, cancellationToken);
        }

        private async Task<AccountContext> AdminAsync(AccountContext context, string userId, CancellationToken cancellationToken)
        {
            context = await AccountScope.RefreshAsync(accounts, context, cancellationToken, requireProject: true);
            if (context.Project!.Role != "admin" || !context.CanWriteProjectData)
            {
                throw new AccountAccessException("Current project admin access is required to manage explicit grants.");
            }

            if (!TerraScaleIds.IsValid(userId))
            {
                throw new AccountAccessException("Enter a canonical user ULID. Use the user ID, not an email address.", 422);
            }

            return context;
        }
    }
}
