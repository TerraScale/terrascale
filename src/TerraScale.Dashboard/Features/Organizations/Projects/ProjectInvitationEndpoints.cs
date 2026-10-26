using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Shared.UI;
using TerraScale.DotNet;

namespace TerraScale.Dashboard.Features.Organizations.Projects;

public sealed record ProjectInvitationPage(ShellModel Shell, IReadOnlyList<TerraScaleAuthPlatformInvitation> Invitations,
    string? Next = null, string Error = "", bool HasPrevious = false)
{
    public string Url => WorkspaceUrl.For(Shell.Context, "/project/invitations", replace: true);
}

public static class ProjectInvitationEndpoints
{
    public static IEndpointRouteBuilder MapProjectInvitations(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/project/invitations", async Task<IResult> (HttpContext http, DashboardSessions sessions,
            AccountService accounts, ProjectInvitationService invitations) =>
        {
            var context = await AccountRequest.RequireAsync(http, sessions, accounts);
            if (context is null) return AccountRequest.LoginRedirect(http);
            context = await AccountScope.RefreshAsync(accounts, context, http.RequestAborted, requireProject: true);
            try
            {
                if (http.Request.Query["after"].Count > 1) throw new AccountAccessException("Choose one page cursor. Open the first page.", 400);
                var after = http.Request.Query["after"].Count == 0 ? null : http.Request.Query["after"].ToString();
                var page = await invitations.ReadAsync(context, after, http.RequestAborted);
                return Page(http, context, page.Items, page.Next, previous: after is not null);
            }
            catch (AccountAccessException error) { return Page(http, context, [], error: error.Message, status: error.StatusCode); }
            catch (Exception error) when (error is IOException or HttpRequestException || error is OperationCanceledException && !http.RequestAborted.IsCancellationRequested)
            { return Page(http, context, [], error: "Project invitations could not be loaded. Reload this page to retry.", status: 503); }
        });
        return endpoints;
    }

    private static IResult Page(HttpContext http, AccountContext context, IReadOnlyList<TerraScaleAuthPlatformInvitation> invitations,
        string? next = null, string error = "", int status = 200, bool previous = false)
    {
        http.Response.Headers.CacheControl = "no-store";
        var page = ProjectInvitationView.Create(new(ShellModel.For(http, context, "Project invitations"), invitations, next, error, previous));
        page.StatusCode = status;
        return page;
    }
}
