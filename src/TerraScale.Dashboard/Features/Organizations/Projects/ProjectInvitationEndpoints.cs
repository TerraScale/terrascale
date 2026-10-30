using TerraScale.Dashboard.Common.UI;
using TerraScale.Dashboard.Platform.TsAuth;

namespace TerraScale.Dashboard.Features.Organizations.Projects
{
    public sealed record ProjectInvitationPage(ShellModel Shell, IReadOnlyList<TsAuthPlatformInvitation> Invitations,
        string? Next = null, string Error = "", bool HasPrevious = false, string After = "", string Notice = "")
    {
        public string PostUrl => WorkspaceUrl.For(Shell.Context, "/api/v1/dashboard/project/invitations/revoke", replace: true);
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
                if (context is null)
                {
                    return AccountRequest.LoginRedirect(http);
                }

                context = await AccountScope.RefreshAsync(accounts, context, http.RequestAborted, requireProject: true);
                try
                {
                    if (http.Request.Query["after"].Count > 1)
                    {
                        throw new AccountAccessException("Choose one page cursor. Open the first page.", 400);
                    }

                    var after = http.Request.Query["after"].Count == 0 ? null : http.Request.Query["after"].ToString();
                    var page = await invitations.ReadAsync(context, after, http.RequestAborted);
                    return Page(http, context, page.Items, page.Next, previous: after is not null, after: after ?? "",
                        notice: http.Request.Query["revoked"] == "1" ? "Project invitation revoked. Its acceptance link no longer works." : "");
                }
                catch (AccountAccessException error) { return Page(http, context, [], error: error.Message, status: error.StatusCode); }
                catch (Exception error) when (error is IOException or HttpRequestException || error is OperationCanceledException && !http.RequestAborted.IsCancellationRequested)
                { return Page(http, context, [], error: "Project invitations could not be loaded. Reload this page to retry.", status: 503); }
            });
            endpoints.MapPost("/api/v1/dashboard/project/invitations/revoke", async Task<IResult> (HttpContext http, DashboardSessions sessions,
                AccountService accounts, ProjectInvitationService invitations) =>
            {
                var context = await AccountRequest.RequireAsync(http, sessions, accounts);
                if (context is null)
                {
                    return Results.Unauthorized();
                }

                context = await AccountScope.RefreshAsync(accounts, context, http.RequestAborted, requireProject: true);
                var form = await http.Request.ReadFormAsync(http.RequestAborted);
                IResult Error(string message, int status)
                {
                    return Page(http, context, [], error: message, status: status);
                }

                if (!Csrf.Validate(http, form))
                {
                    return Error("The form expired. Reload project invitations and try again.", 400);
                }

                if (form["invitationId"].Count != 1 || form["after"].Count > 1)
                {
                    return Error("Choose one invitation and page.", 400);
                }

                var after = form["after"].ToString();
                try
                {
                    await invitations.RevokeAsync(context, form["invitationId"].ToString(), after.Length == 0 ? null : after, http.RequestAborted);
                    return HttpRedirects.SeeOther(WorkspaceUrl.For(context, "/project/invitations?revoked=1", replace: true));
                }
                catch (AccountAccessException error) { return Error(error.Message, error.StatusCode); }
                catch (Exception error) when (error is IOException or HttpRequestException || error is OperationCanceledException && !http.RequestAborted.IsCancellationRequested)
                { return Error("Revocation could not be confirmed. Reload project invitations to check its current status before retrying.", 503); }
            });
            return endpoints;
        }

        private static RazorSlices.RazorSlice<ProjectInvitationPage> Page(HttpContext http, AccountContext context, IReadOnlyList<TsAuthPlatformInvitation> invitations,
            string? next = null, string error = "", int status = 200, bool previous = false, string after = "", string notice = "")
        {
            http.Response.Headers.CacheControl = "no-store";
            var page = ProjectInvitationView.Create(new(ShellModel.For(http, context, "Project invitations"), invitations, next, error, previous, after, notice));
            page.StatusCode = status;
            return page;
        }
    }
}
