using TerraScale.Dashboard.Common.Accounts;
using TerraScale.Dashboard.Common.UI;
using TerraScale.Dashboard.Errors;
using TerraScale.Dashboard.Platform.Datastar;
using TerraScale.Dashboard.Platform.TsAuth;

namespace TerraScale.Dashboard.Features.Organizations.Projects
{
    using NotFound = TerraScale.Dashboard.Errors.NotFound;

    public sealed record ProjectGrantPage(ShellModel Shell, IReadOnlyList<TsAuthPlatformProjectMember> Grants,
        string? Next = null, string Error = "", bool HasPrevious = false, string DraftUserId = "", string DraftRole = "viewer", string Status = "", string Dialog = "")
    {
        public string Url => WorkspaceUrl.For(Shell.Context, "/project/members", replace: true);
        public string PostUrl => WorkspaceUrl.For(Shell.Context, "/api/v1/dashboard/project/grants", replace: true);
        public bool CanManageGrants => Shell.Context.CanManageProject;
    }

    public static class ProjectGrantEndpoints
    {
        internal static async Task<IResult> PageAsync(HttpContext http, DashboardSessions sessions, AccountService accounts, ProjectGrantService grants)
        {
            var context = await AccountRequest.RequireAsync(http, sessions, accounts);
            if (context is null)
            {
                return AccountRequest.LoginRedirect(http);
            }

            context = await AccountScope.RefreshAsync(accounts, context, http.RequestAborted, requireProject: true);
            if (http.Request.Query["after"].Count > 1)
            {
                return Page(http, context, [], error: "Choose one page cursor. Open the first page.", status: 400);
            }

            var after = http.Request.Query["after"].Count == 0 ? null : http.Request.Query["after"].ToString();
            try
            {
                return await grants.ReadAsync(context, after, http.RequestAborted) switch
                {
                    TsAuthPlatformPage<TsAuthPlatformProjectMember> listed => Page(http, context, listed.Items, listed.Next, previous: after is not null,
                        notice: http.Request.Query["changed"] == "1" ? "Explicit project grant updated." : ""),
                    NotFound error => Page(http, context, [], error: error.Message!, status: 404),
                    Validation error => Page(http, context, [], error: error.Message, status: 400),
                    Unavailable => Page(http, context, [], error: "Project grants could not be loaded. Reload this page to retry.", status: 503),
                };
            }
            catch (AccountAccessException error) { return Page(http, context, [], error: error.Message, status: error.StatusCode); }
            catch (Exception error) when (error is IOException or HttpRequestException || error is OperationCanceledException && !http.RequestAborted.IsCancellationRequested)
            { return Page(http, context, [], error: "Project grants could not be loaded. Reload this page to retry.", status: 503); }
        }

        internal static async Task<IResult> MutateAsync(HttpContext http, DashboardSessions sessions, AccountService accounts, ProjectGrantService grants)
        {
            var context = await AccountRequest.RequireAsync(http, sessions, accounts);
            if (context is null)
            {
                return Results.Unauthorized();
            }

            context = await AccountScope.RefreshAsync(accounts, context, http.RequestAborted, requireProject: true);
            var form = await FormRequests.ReadAsync(http, http.RequestAborted);
            var userId = form["userId"].ToString();
            var role = form["role"].ToString();
            async Task<IResult> Error(string message, int status)
            {
                if (http.Request.IsDatastarRequest)
                {
                    http.Response.Headers.CacheControl = "no-store";
                    var errorShell = ShellModel.For(http, context, "Project grants") with { ReturnUrl = WorkspaceUrl.For(context, "/project/members", replace: true) };
                    var reloaded = await http.RequestServices.GetRequiredService<ProjectGrantService>().ReadAsync(context, null, http.RequestAborted);
                    var items = reloaded switch
                    {
                        TsAuthPlatformPage<TsAuthPlatformProjectMember> page => page.Items,
                        NotFound failure => throw new AccountAccessException(failure.Message!, 404),
                        Validation failure => throw new AccountAccessException(failure.Message, 400),
                        Unavailable => throw new IOException("The project grant page is invalid."),
                    };
                    await DatastarEvents.PatchElementsAsync(http, ProjectGrantPageView.Create(new(errorShell, items, null, message, false, userId, role, "",
                        form["dialog"] == "edit" ? "edit-grant-" + userId : form["dialog"] == "remove" ? "remove-grant-" + userId : "")));
                    return Results.Empty;
                }
                return Page(http, context, [], error: message, status: status, draftUserId: userId, draftRole: role, dialog: form["dialog"] == "edit" ? "edit-grant-" + userId : form["dialog"] == "remove" ? "remove-grant-" + userId : "");
            }

            if (!Csrf.Validate(http, form))
            {
                return RequestError.FormExpired(http, "/project/members", Page(http, context, [], error: "The form expired. Reload project grants and try again.", status: 400, draftUserId: userId, draftRole: role));
            }

            try
            {
                var operation = form["operation"].ToString();
                if (operation == "set")
                {
                    var written = await grants.SetAsync(context, userId, role, http.RequestAborted);
                    switch (written)
                    {
                        case Forbidden refused:
                            return await Error(refused.Message, 403);
                        case InvalidUser invalid:
                            return await Error(invalid.Message, 422);
                        case InvalidRole invalid:
                            return await Error(invalid.Message, 422);
                        case Unavailable:
                            return await Error("The grant change could not be confirmed. Reload project grants before trying again.", 503);
                        case TsAuthPlatformProjectMember:
                            break;
                    }
                }
                else if (operation == "remove")
                {
                    var removed = await grants.RemoveAsync(context, userId, http.RequestAborted);
                    switch (removed)
                    {
                        case Forbidden refused:
                            return await Error(refused.Message, 403);
                        case InvalidUser invalid:
                            return await Error(invalid.Message, 422);
                        case Unavailable:
                            return await Error("The grant change could not be confirmed. Reload project grants before trying again.", 503);
                        case Done:
                            break;
                    }

                    if (userId == context.UserId)
                    {
                        http.Response.Headers.CacheControl = "no-store";
                        return ProjectGrantRemovedView.Create(new DocumentModel("Explicit grant removed", DocumentModel.NonceFor(http)));
                    }
                }
                else
                {
                    return await Error("Choose grant/change or remove explicit grant.", 422);
                }

                if (http.Request.IsDatastarRequest)
                {
                    context = await AccountScope.RefreshAsync(accounts, context, http.RequestAborted, requireProject: true, reload: true);
                    return await grants.ReadAsync(context, null, http.RequestAborted) switch
                    {
                        TsAuthPlatformPage<TsAuthPlatformProjectMember> page => await PatchedAsync(http, context, page.Items, "Explicit project grant updated."),
                        NotFound => await Error("The project or target user is unavailable. New grants require an existing organization member; otherwise ask an organization admin about a project invitation. Invitations are not available here.", 404),
                        Validation failure => await Error(failure.Message, 400),
                        Unavailable => await Error("The grant change could not be confirmed. Reload project grants before trying again.", 503),
                    };
                }
                return HttpRedirects.SeeOther(WorkspaceUrl.For(context, "/project/members?changed=1", replace: true));
            }
            catch (AccountAccessException error)
            { return await Error(error.StatusCode == 404 ? "The project or target user is unavailable. New grants require an existing organization member; otherwise ask an organization admin about a project invitation. Invitations are not available here." : error.Message, error.StatusCode); }
            catch (Exception error) when (error is IOException or HttpRequestException || error is OperationCanceledException && !http.RequestAborted.IsCancellationRequested)
            { return await Error("The grant change could not be confirmed. Reload project grants before trying again.", 503); }
        }

        private static RazorSlices.RazorSlice<ProjectGrantPage> Page(HttpContext http, AccountContext context, IReadOnlyList<TsAuthPlatformProjectMember> grants,
            string? next = null, string error = "", int status = 200, bool previous = false, string draftUserId = "", string draftRole = "viewer", string notice = "", string dialog = "")
        {
            http.Response.Headers.CacheControl = "no-store";
            var shell = ShellModel.For(http, context, "Project grants") with { ReturnUrl = WorkspaceUrl.For(context, "/project/members", replace: true) };
            var page = ProjectGrantView.Create(new(shell, grants, next, error, previous, draftUserId, draftRole, notice, dialog));
            page.StatusCode = status;
            return page;
        }

        private static async Task<IResult> PatchedAsync(HttpContext http, AccountContext context, IReadOnlyList<TsAuthPlatformProjectMember> items, string notice)
        {
            http.Response.Headers.CacheControl = "no-store";
            var shell = ShellModel.For(http, context, "Project grants") with { ReturnUrl = WorkspaceUrl.For(context, "/project/members", replace: true) };
            await DatastarEvents.PatchElementsAsync(http, ProjectGrantPageView.Create(new(shell, items, null, "", false, "", "viewer", notice, "")));
            return Results.Empty;
        }
    }
}
