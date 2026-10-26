using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Shared.UI;
using TerraScale.DotNet;

namespace TerraScale.Dashboard.Features.Organizations.Projects;

public sealed record ProjectGrantPage(ShellModel Shell, IReadOnlyList<TerraScaleAuthPlatformProjectMember> Grants,
    string? Next = null, string Error = "", bool HasPrevious = false)
{
    public string Url => WorkspaceUrl.For(Shell.Context, "/project/members", replace: true);
}

public static class ProjectGrantEndpoints
{
    public static IEndpointRouteBuilder MapProjectGrants(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/project/members", async Task<IResult> (HttpContext http, DashboardSessions sessions,
            AccountService accounts, ProjectGrantService grants) =>
        {
            var context = await AccountRequest.RequireAsync(http, sessions, accounts);
            if (context is null) return AccountRequest.LoginRedirect(http);
            context = await AccountScope.RefreshAsync(accounts, context, http.RequestAborted, requireProject: true);
            try
            {
                if (http.Request.Query["after"].Count > 1) throw new AccountAccessException("Choose one page cursor. Open the first page.", 400);
                var after = http.Request.Query["after"].Count == 0 ? null : http.Request.Query["after"].ToString();
                var page = await grants.ReadAsync(context, after, http.RequestAborted);
                return Page(http, context, page.Items, page.Next, previous: after is not null);
            }
            catch (AccountAccessException error) { return Page(http, context, [], error: error.Message, status: error.StatusCode); }
            catch (Exception error) when (error is IOException or HttpRequestException || error is OperationCanceledException && !http.RequestAborted.IsCancellationRequested)
            { return Page(http, context, [], error: "Project grants could not be loaded. Reload this page to retry.", status: 503); }
        });
        return endpoints;
    }

    private static IResult Page(HttpContext http, AccountContext context, IReadOnlyList<TerraScaleAuthPlatformProjectMember> grants,
        string? next = null, string error = "", int status = 200, bool previous = false)
    {
        http.Response.Headers.CacheControl = "no-store";
        var shell = ShellModel.For(http, context, "Project grants");
        var page = ProjectGrantView.Create(new(shell, grants, next, error, previous));
        page.StatusCode = status;
        return page;
    }
}
