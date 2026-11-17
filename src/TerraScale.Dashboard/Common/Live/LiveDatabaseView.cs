using System.Text.Json;
using TerraScale.Dashboard.Features.Auth;

namespace TerraScale.Dashboard.Common.Live;

public sealed record LiveError(string Id, int Status, string Message);

public static class LiveDatabaseView
{
    public static async Task<IResult> StreamAsync(HttpContext http, string region,
        Func<AccountContext, DatabaseRecord, CancellationToken, Task<LiveFragment>> read, CancellationToken cancellationToken)
    {
        var sessions = http.RequestServices.GetRequiredService<DashboardSessions>();
        var accounts = http.RequestServices.GetRequiredService<AccountService>();
        var databases = http.RequestServices.GetRequiredService<DatabaseService>();
        var live = new LiveView(http.RequestServices.GetRequiredService<TimeProvider>());
        async Task<LiveFragment> ReadAsync(CancellationToken cancellationToken)
        {
            try
            {
                var context = await AccountRequest.RequireAsync(http, sessions, accounts);
                if (context is null)
                {
                    return new(LiveErrorView.Create(new(region, 401, "Your session may have expired. Sign in to TerraScale and try again.")), true);
                }

                await databases.SyncAsync(context, cancellationToken);
                var id = http.Request.RouteValues["databaseId"]?.ToString() ?? "";
                var (current, database) = await databases.LookupAsync(context, id, cancellationToken);
                return await read(current, database, cancellationToken);
            }
            catch (OrganizationMfaRequiredException error)
            {
                return new(LiveErrorView.Create(new(region, 403, error.Message)), true);
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested &&
                (DatabaseResults.KnownFailure(error) || error is HttpRequestException or IOException or JsonException or OperationCanceledException))
            {
                return new(LiveErrorView.Create(new(region, DatabaseResults.Status(error), DatabaseResults.Message(error))), true);
            }
        }

        await live.StreamAsync(http, ReadAsync, live.ChangesAsync(cancellationToken), cancellationToken);
        return Results.Empty;
    }
}
