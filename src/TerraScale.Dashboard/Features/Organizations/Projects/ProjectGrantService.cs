using TerraScale.Dashboard.Features.Auth;
using TerraScale.DotNet;

namespace TerraScale.Dashboard.Features.Organizations.Projects;

public sealed class ProjectGrantService(AccountService accounts, ITsAuthPlatform platform)
{
    public async Task<TerraScaleAuthPlatformPage<TerraScaleAuthPlatformProjectMember>> ReadAsync(AccountContext context,
        string? after, CancellationToken cancellationToken)
    {
        context = await AccountScope.RefreshAsync(accounts, context, cancellationToken, requireProject: true);
        if (context.Project!.Role is not ("viewer" or "member" or "admin")) throw new AccountAccessException("Project is unavailable.", 404);
        if (after is not null && !ValidCursor(after)) throw new AccountAccessException("The page cursor is invalid. Open the first page.", 400);
        var page = await platform.ListProjectMembersAsync(context.Session, context.Organization!.Id, context.Project.Id, after, cancellationToken);
        if (page.Items is null || page.Items.Count > 25 || page.Items.Any(item => !TerraScaleIds.IsValid(item.UserId) ||
            item.Role is not ("viewer" or "member" or "admin")) || page.Items.Select(item => item.UserId).Distinct().Count() != page.Items.Count ||
            page.Next is not null && (!ValidCursor(page.Next) || page.Next == after))
            throw new IOException("The project grant page is invalid.");
        return page;
    }

    private static bool ValidCursor(string value) => value.Length is > 0 and <= 512 && !value.Any(char.IsControl);
}
