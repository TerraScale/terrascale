using TerraScale.Dashboard.Errors;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Platform.TsAuth;

namespace TerraScale.Dashboard.Features.Organizations.Projects
{
    using NotFound = TerraScale.Dashboard.Errors.NotFound;

    public readonly union ProjectGrantPageResult(TsAuthPlatformPage<TsAuthPlatformProjectMember>, NotFound, Validation, Unavailable);

    public readonly union ProjectGrantSet(TsAuthPlatformProjectMember, Forbidden, InvalidUser, InvalidRole, Unavailable);

    public readonly union ProjectGrantRemoval(Done, Forbidden, InvalidUser, Unavailable);

    public readonly union ProjectGrantAdmin(AccountContext, Forbidden, InvalidUser);

    public sealed record InvalidUser(string Message);

    public sealed record InvalidRole(string Message);

    public sealed class ProjectGrantService(AccountService accounts, ITsAuthPlatform platform)
    {
        public async Task<ProjectGrantPageResult> ReadAsync(AccountContext context,
            string? after, CancellationToken cancellationToken)
        {
            context = await AccountScope.RefreshAsync(accounts, context, cancellationToken, requireProject: true);
            if (context.Project!.Role is not ("viewer" or "member" or "admin"))
            {
                return new NotFound("Project is unavailable.");
            }

            if (after is not null && !ValidCursor(after))
            {
                return new Validation("The page cursor is invalid. Open the first page.");
            }

            var page = await platform.ListProjectMembersAsync(context.Session, context.Organization!.Id, context.Project.Id, after, cancellationToken);
            if (page.Items is null || page.Items.Count > 25 || page.Items.Any(item => item is null || !TerraScaleIds.IsValid(item.UserId) ||
                item.Role is not ("viewer" or "member" or "admin")) || page.Items.Select(item => item.UserId).Distinct().Count() != page.Items.Count ||
                page.Next is not null && (!ValidCursor(page.Next) || page.Next == after))
            {
                return new Unavailable("The project grant page is invalid.");
            }

            return page;
        }

        private static bool ValidCursor(string value)
        {
            return value.Length is > 0 and <= 512 && !value.Any(char.IsControl);
        }

        public async Task<ProjectGrantSet> SetAsync(AccountContext context, string userId, string role, CancellationToken cancellationToken)
        {
            return await AdminAsync(context, userId, cancellationToken) switch
            {
                Forbidden error => error,
                InvalidUser error => error,
                AccountContext granted => await WriteAsync(granted, userId, role, cancellationToken),
            };
        }

        private async Task<ProjectGrantSet> WriteAsync(AccountContext context, string userId, string role, CancellationToken cancellationToken)
        {
            if (role is not ("viewer" or "member" or "admin"))
            {
                return new InvalidRole("Choose an available role.");
            }

            var grant = await platform.SetProjectMemberRoleAsync(context.Session, context.Organization!.Id, context.Project!.Id, userId, role, cancellationToken);
            if (grant is null || grant.UserId != userId || grant.Role != role)
            {
                return new Unavailable("The project grant response does not match.");
            }

            return grant;
        }

        public async Task<ProjectGrantRemoval> RemoveAsync(AccountContext context, string userId, CancellationToken cancellationToken)
        {
            return await AdminAsync(context, userId, cancellationToken) switch
            {
                Forbidden error => error,
                InvalidUser error => error,
                AccountContext granted => await EraseAsync(granted, userId, cancellationToken),
            };
        }

        private async Task<ProjectGrantRemoval> EraseAsync(AccountContext context, string userId, CancellationToken cancellationToken)
        {
            await platform.RemoveProjectMemberAsync(context.Session, context.Organization!.Id, context.Project!.Id, userId, cancellationToken);
            return new Done();
        }

        private async Task<ProjectGrantAdmin> AdminAsync(AccountContext context, string userId, CancellationToken cancellationToken)
        {
            context = await AccountScope.RefreshAsync(accounts, context, cancellationToken, requireProject: true);
            if (context.Project!.Role != "admin" || !context.CanWriteProjectData)
            {
                return new Forbidden("Current project admin access is required to manage explicit grants.");
            }

            if (!TerraScaleIds.IsValid(userId))
            {
                return new InvalidUser("Enter a canonical user ULID. Use the user ID, not an email address.");
            }

            return context;
        }
    }
}
