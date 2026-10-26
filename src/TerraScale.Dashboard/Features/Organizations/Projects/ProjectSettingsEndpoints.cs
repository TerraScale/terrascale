using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Shared.UI;

namespace TerraScale.Dashboard.Features.Organizations;

public sealed record ProjectSettingsPage(ShellModel Shell, string Name, string Error = "", bool Renamed = false)
{
    public bool CanRename => Shell.Context.Project?.Role == "admin" && Shell.Context.CanWriteProjectData;
    public string Url => WorkspaceUrl.For(Shell.Context, "/project", replace: true);
}

public static class ProjectSettingsEndpoints
{
    public static IEndpointRouteBuilder MapProjectSettings(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/project", async Task<IResult> (HttpContext http, DashboardSessions sessions, AccountService accounts) =>
        {
            var context = await AccountRequest.RequireAsync(http, sessions, accounts);
            if (context is null) return AccountRequest.LoginRedirect(http);
            context = await AccountScope.RefreshAsync(accounts, context, http.RequestAborted, requireProject: true);
            return Page(http, context, context.Project!.Name, renamed: http.Request.Query["renamed"] == "1");
        });
        endpoints.MapPost("/api/v1/dashboard/project/rename", async Task<IResult> (HttpContext http, DashboardSessions sessions,
            AccountService accounts, OrganizationOperations operations) =>
        {
            var context = await AccountRequest.RequireAsync(http, sessions, accounts);
            if (context is null) return Results.Unauthorized();
            context = await AccountScope.RefreshAsync(accounts, context, http.RequestAborted, requireProject: true);
            var form = await http.Request.ReadFormAsync(http.RequestAborted);
            var name = form["name"].ToString();
            if (!Csrf.Validate(http, form)) return Page(http, context, name, "The form expired. Reload project settings and try again.", 400);
            try
            {
                await operations.UpdateProjectAsync(context, name, form["region"].ToString(), form["backupRegion"].ToString(),
                    form["regions"].Select(value => value ?? "").ToArray(), http.RequestAborted);
                return HttpRedirects.SeeOther(WorkspaceUrl.For(context, "/project?renamed=1", replace: true));
            }
            catch (AccountAccessException error) { return Page(http, context, name, error.Message, error.StatusCode); }
            catch (Exception error) when (error is IOException or HttpRequestException || error is OperationCanceledException && !http.RequestAborted.IsCancellationRequested)
            { return Page(http, context, name, "The rename could not be confirmed. Reload project settings to check its current name, then retry if needed.", 503); }
        });
        Projects.ProjectGrantEndpoints.MapProjectGrants(endpoints);
        return endpoints;
    }

    private static IResult Page(HttpContext http, AccountContext context, string name, string error = "", int status = 200, bool renamed = false)
    {
        http.Response.Headers.CacheControl = "no-store";
        var shell = ShellModel.For(http, context, "Project settings") with { ReturnUrl = WorkspaceUrl.For(context, "/project", replace: true) };
        var page = Projects.ProjectSettingsView.Create(new(shell, name, error, renamed));
        page.StatusCode = status;
        return page;
    }
}
