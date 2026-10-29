using TerraScale.Dashboard.Shared.UI;
using TerraScale.DotNet;

namespace TerraScale.Dashboard.Features.Organizations.Projects;

public sealed record ProjectGrantPage(ShellModel Shell, IReadOnlyList<TerraScaleAuthPlatformProjectMember> Grants,
    string? Next = null, string Error = "", bool HasPrevious = false, string DraftUserId = "", string DraftRole = "viewer", string Status = "")
{
    public string Url => WorkspaceUrl.For(Shell.Context, "/project/members", replace: true);
    public string PostUrl => WorkspaceUrl.For(Shell.Context, "/api/v1/dashboard/project/grants", replace: true);
    public bool CanManageGrants => Shell.Context.CanManageProject;
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
                return Page(http, context, page.Items, page.Next, previous: after is not null,
                    notice: http.Request.Query["changed"] == "1" ? "Explicit project grant updated." : "");
            }
            catch (AccountAccessException error) { return Page(http, context, [], error: error.Message, status: error.StatusCode); }
            catch (Exception error) when (error is IOException or HttpRequestException || error is OperationCanceledException && !http.RequestAborted.IsCancellationRequested)
            { return Page(http, context, [], error: "Project grants could not be loaded. Reload this page to retry.", status: 503); }
        });
        endpoints.MapPost("/api/v1/dashboard/project/grants", async Task<IResult> (HttpContext http, DashboardSessions sessions,
            AccountService accounts, ProjectGrantService grants) =>
        {
            var context = await AccountRequest.RequireAsync(http, sessions, accounts);
            if (context is null) return Results.Unauthorized();
            context = await AccountScope.RefreshAsync(accounts, context, http.RequestAborted, requireProject: true);
            var form = await http.Request.ReadFormAsync(http.RequestAborted);
            var userId = form["userId"].ToString();
            var role = form["role"].ToString();
            IResult Error(string message, int status) => Page(http, context, [], error: message, status: status, draftUserId: userId, draftRole: role);
            if (!Csrf.Validate(http, form)) return Error("The form expired. Reload project grants and try again.", 400);
            try
            {
                if (form["operation"] == "set") await grants.SetAsync(context, userId, role, http.RequestAborted);
                else if (form["operation"] == "remove")
                {
                    await grants.RemoveAsync(context, userId, http.RequestAborted);
                    // Self-removal may remove the last authority on this project. Show no stale project shell or controls.
                    if (userId == context.UserId)
                    {
                        http.Response.Headers.CacheControl = "no-store";
                        return ProjectGrantRemovedView.Create(new DocumentModel("Explicit grant removed", DocumentModel.NonceFor(http)));
                    }
                }
                else throw new AccountAccessException("Choose grant/change or remove explicit grant.", 422);
                return HttpRedirects.SeeOther(WorkspaceUrl.For(context, "/project/members?changed=1", replace: true));
            }
            catch (AccountAccessException error)
            { return Error(error.StatusCode == 404 ? "The project or target user is unavailable. New grants require an existing organization member; otherwise ask an organization admin about a project invitation. Invitations are not available here." : error.Message, error.StatusCode); }
            catch (Exception error) when (error is IOException or HttpRequestException || error is OperationCanceledException && !http.RequestAborted.IsCancellationRequested)
            { return Error("The grant change could not be confirmed. Reload project grants before trying again.", 503); }
        });
        return endpoints;
    }

    private static IResult Page(HttpContext http, AccountContext context, IReadOnlyList<TerraScaleAuthPlatformProjectMember> grants,
        string? next = null, string error = "", int status = 200, bool previous = false, string draftUserId = "", string draftRole = "viewer", string notice = "")
    {
        http.Response.Headers.CacheControl = "no-store";
        var shell = ShellModel.For(http, context, "Project grants") with { ReturnUrl = WorkspaceUrl.For(context, "/project/members", replace: true) };
        var page = ProjectGrantView.Create(new(shell, grants, next, error, previous, draftUserId, draftRole, notice));
        page.StatusCode = status;
        return page;
    }
}
