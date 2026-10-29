using System.Text.Json;
using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Common.UI;

namespace TerraScale.Dashboard.Features.NotFound
{
    public static class NotFoundEndpoints
    {
        // Signed-in visitors keep the app shell. Anyone else, or an unusable account context, gets the public page.
        public static async Task<IResult> PageAsync(HttpContext http, DashboardSessions sessions, AccountService accounts)
        {
            var context = await ContextAsync(http, sessions, accounts);
            if (context is null)
            {
                var page = NotFound.Create(new DocumentModel("Page not found", DocumentModel.NonceFor(http)));
                page.StatusCode = StatusCodes.Status404NotFound;
                return page;
            }
            return Page(http, context);
        }

        internal static IResult Page(HttpContext http, AccountContext context)
        {
            var shell = NotFoundShell.Create(ShellModel.For(http, context, "Page not found"));
            shell.StatusCode = StatusCodes.Status404NotFound;
            return shell;
        }

        private static async Task<AccountContext?> ContextAsync(HttpContext http, DashboardSessions sessions, AccountService accounts)
        {
            try { return await AccountEndpoints.RequireAsync(http, sessions, accounts); }
            catch (Exception error) when (error is AccountAccessException or HttpRequestException or IOException or JsonException or TimeoutException ||
                                          error is OperationCanceledException && !http.RequestAborted.IsCancellationRequested)
            { return null; }
        }
    }
}
