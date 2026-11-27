namespace TerraScale.Dashboard.Common.Live;

public sealed record LiveError(string Id, int Status, string Message);

public static class LiveDatabaseView
{
    private static readonly TimeSpan SyncFreshness = TimeSpan.FromSeconds(30);

    public static async Task<IResult> StreamAsync(HttpContext http, string region,
        Func<AccountContext, DatabaseRecord, CancellationToken, Task<LiveFragment>> read, CancellationToken cancellationToken,
        IAsyncEnumerable<DateTimeOffset>? hints = null)
    {
        var sessions = http.RequestServices.GetRequiredService<DashboardSessions>();
        var accounts = http.RequestServices.GetRequiredService<AccountService>();
        var databases = http.RequestServices.GetRequiredService<DatabaseService>();
        var live = http.RequestServices.GetRequiredService<LiveView>();
        return await live.StreamAsync(http, token => LiveView.ReadWithAccountAsync(http, sessions, accounts, region,
            async (context, cancellationToken) =>
            {
                await databases.SyncIfStaleAsync(context, SyncFreshness, cancellationToken);
                var id = http.Request.RouteValues["databaseId"]?.ToString() ?? "";
                var (current, database) = await databases.LookupAsync(context, id, cancellationToken);
                return await read(current, database, cancellationToken);
            }, token), hints ?? live.ChangesAsync(cancellationToken), cancellationToken);
    }
}
