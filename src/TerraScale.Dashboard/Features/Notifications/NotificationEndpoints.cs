using TerraScale.Dashboard.Features.ApiKeys;
using TerraScale.Dashboard.Platform.Datastar;
using TerraScale.Dashboard.Common.UI;

namespace TerraScale.Dashboard.Features.Notifications
{
    public sealed record NotificationPage(ShellModel Shell, IReadOnlyList<NotificationRecord> Items, string Status, int Unread, int Critical, int Total, int Page, int PageCount, bool Dialog = false)
    {
        public static NotificationPage For(ShellModel shell, string? status, string? rawPage, bool dialog = false)
        {
            var inbox = NotificationCommands.Inbox(shell.Context, status, int.TryParse(rawPage, out var parsed) ? parsed : 1);
            return new(shell, inbox.Items, inbox.Status, inbox.Unread, inbox.Critical, inbox.Total, inbox.Page, inbox.PageCount, dialog);
        }
        public string Href(int page, bool dialog = false)
        {
            return WorkspaceUrl.For(Shell.Context, (dialog ? "/notifications/dialog" : "/notifications") +
            (page > 1 || Status == "unread" ? "?" + string.Join('&', new[] { page > 1 ? "page=" + page : "", Status == "unread" ? "status=unread" : "" }.Where(item => item.Length > 0)) : ""));
        }
    }

    public sealed record NotificationReadFormModel(NotificationPage Page, string? Id = null)
    {
        public string Action => WorkspaceUrl.For(Page.Shell.Context, Id is null ? "/api/v1/notifications/read-all" : "/api/v1/notifications/" + Uri.EscapeDataString(Id) + "/read");
    }

    public static class NotificationEndpoints
    {
        internal static NotificationPage Page(HttpContext http, AccountContext context, bool dialog = false)
        {
            return NotificationPage.For(ShellModel.For(http, context, "Notifications"), http.Request.Query["status"], http.Request.Query["page"], dialog);
        }

        internal static async Task<IResult> MarkAsync(HttpContext http, DashboardSessions sessions, AccountService accounts, string? id)
        {
            var context = await AccountRequest.RequireAsync(http, sessions, accounts);
            if (context is null)
            {
                return Results.Unauthorized();
            }

            var form = await FormRequests.ReadAsync(http, http.RequestAborted);
            if (!Csrf.Validate(http, form))
            {
                return RequestError.FormExpired(http, "/notifications", Results.BadRequest("Invalid form token."));
            }

            try
            {
                await NotificationCommands.MarkAsync(accounts, context, id, http.RequestAborted);
            }
            catch (AccountAccessException error) { return Results.Problem(error.Message, statusCode: error.StatusCode); }
            context = await NotificationCommands.RefreshAsync(accounts, context, http.RequestAborted, reload: true);
            var page = NotificationPage.For(ShellModel.For(http, context, "Notifications"), form["status"], form["page"], true);
            if (http.Request.Headers["Datastar-Request"] == "true")
            {
                await DatastarEvents.PatchElementsAsync(http, NotificationPatch.Create(page));
                return Results.Empty;
            }
            return HttpRedirects.SeeOther(page.Href(page.Page));
        }
    }
}
