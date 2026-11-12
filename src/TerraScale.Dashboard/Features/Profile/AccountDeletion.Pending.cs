using Microsoft.EntityFrameworkCore;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.Dashboard.Common.UI;

namespace TerraScale.Dashboard.Features.Profile
{
    public static partial class AccountDeletion
    {
        private const int PauseDoublings = 4;

        public static Task<IResult> ProgressAsync(HttpContext http, IDbContextFactory<DashboardDbContext> storage, IDashboardIdentity identity, ITsAuthPlatform platform)
        {
            return CheckStatusAsync(http, storage, identity, platform);
        }

        // This page's continuation never calls DELETE or billing, even for an unknown receipt.
        public static Task<IResult> StatusAsync(HttpContext http, IDbContextFactory<DashboardDbContext> storage, IDashboardIdentity identity,
            ITsAuthPlatform platform, DashboardSessions sessions, AccessTokens tokens)
        {
            return CheckStatusAsync(http, storage, identity, platform, sessions, tokens);
        }

        private static async Task<IResult> CheckStatusAsync(HttpContext http, IDbContextFactory<DashboardDbContext> storage, IDashboardIdentity identity,
            ITsAuthPlatform platform, DashboardSessions? sessions = null, AccessTokens? tokens = null)
        {
            var caller = http.RequestAborted;
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(caller);
            budget.CancelAfter(TimeSpan.FromSeconds(10));
            http.RequestAborted = budget.Token;
            var attempt = 0;
            try
            {
                if (sessions is not null)
                {
                    var form = await FormRequests.ReadAsync(http, budget.Token);
                    if (!Csrf.Validate(http, form))
                    {
                        return RequestError.FormExpired(http, "/account-deletion", Results.StatusCode(403));
                    }

                    _ = int.TryParse(form["_retry"], out attempt);
                    attempt = Math.Min(PauseDoublings, Math.Clamp(attempt, 0, PauseDoublings) + 1);
                }
                else
                {
                    _ = int.TryParse(http.Request.Query["retry"], out attempt);
                }

                attempt = Math.Clamp(attempt, 0, PauseDoublings);
                if (await CompletedAsync(http, storage))
                {
                    http.Response.Cookies.Delete(Cookie);
                    return HttpRedirects.SeeOther("/account-deleted");
                }

                var operation = await ReadOperationAsync(http, storage, identity);
                if (operation is null)
                {
                    return PendingPage(http, "Deletion status needs attention", "This browser has no saved deletion receipt. Sign in to review your account; missing records do not confirm deletion.", attempt, 403, attention: true);
                }

                var completed = await platform.AccountDeletionCompletedAsync(operation.Receipt, budget.Token);
                if (completed && sessions is not null && tokens is not null)
                {
                    return await CompleteAsync(http, sessions, platform, storage, tokens, null, operation);
                }

                return PendingPage(http, completed ? "Finishing dashboard cleanup" : "Account deletion is pending",
                    completed ? "Account closure is confirmed. This page removes the dashboard records and signs you out." :
                    "Account closure is still pending on the machine that is closing it. This page keeps checking and does not name a waiting step.",
                    attempt, 202);
            }
            catch (AccountAccessException error) when (error.StatusCode is >= 400 and < 500 and not (408 or 429))
            {
                return PendingPage(http, "Deletion status needs attention",
                    "The saved receipt could not confirm account closure. Automatic checks have stopped. Your receipt has been retained. Sign in to the same account and review deletion from your profile; do not treat this error as completion.", attempt, error.StatusCode, attention: true);
            }
            catch (Exception error) when (error is HttpRequestException or IOException or TimeoutException ||
                error is AccountAccessException { StatusCode: 408 or 429 or >= 500 } || error is OperationCanceledException && !caller.IsCancellationRequested)
            {
                return PendingPage(http, "Deletion status is temporarily unavailable",
                    "The status check could not finish. Your saved receipt has been retained. An unavailable status check does not confirm deletion.", attempt, 503);
            }
            finally { http.RequestAborted = caller; }
        }

        private static RazorSlices.RazorSlice<AccountDeletionPendingPage> PendingPage(HttpContext http, string title, string message, int attempt, int statusCode, bool attention = false)
        {
            http.Response.Headers.CacheControl = "no-store";
            var view = AccountDeletionPendingView.Create(new(new(title, DocumentModel.NonceFor(http)), title, message,
                Csrf.Token(http), attempt, Math.Min(30000, 2000 << Math.Min(attempt, PauseDoublings)), !attention, attention));
            view.StatusCode = statusCode;
            return view;
        }
    }

    public sealed record AccountDeletionPendingPage(DocumentModel Document, string Title, string Message, string Csrf,
        int Attempt, int DelayMilliseconds, bool Automatic, bool Attention);
}
