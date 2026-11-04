using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Platform.Rpc;
using TerraScale.TerraBase.Contracts;
using TerraScale.TerraBase.Contracts.Management;

namespace TerraScale.Dashboard.Features.Databases;

public sealed record DatabaseReplication(TerraBaseDatabaseRegions? Status, string Error = "")
{
    public static async Task<DatabaseReplication> LoadAsync(HttpContext http, AccountContext context, DatabaseRecord database, DatabaseService databases)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var token = await http.RequestServices.GetRequiredService<IProjectAccessTokens>()
                .GetAsync(context.Session, context.Organization!.Id, database.ProjectId, ["databases:read"], deadline.Token);
            var home = await databases.HomeAsync(context, database, deadline.Token);
            var status = await http.RequestServices.GetRequiredService<TerraBaseRpcClient>()
                .User<IManagementService>(token, home.Id).WithCancellationToken(deadline.Token)
                .GetDatabaseRegionsAsync(database.ProjectId, database.Id);
            if (status.DatabaseId != database.Id || status.HomeRegion != home.Id || status.Replicas.Count > 8
                || status.Replicas.Any(row => row.Region == status.HomeRegion || string.IsNullOrWhiteSpace(row.Region)
                    || row.LagMs is { } lag && (!double.IsFinite(lag) || lag < 0))
                || status.Replicas.Select(row => row.Region).Distinct(StringComparer.Ordinal).Count() != status.Replicas.Count)
            {
                throw new InvalidDataException("Invalid database regions response.");
            }

            return new(status);
        }
        catch (Exception error) when (error is TerraBaseException or HttpRequestException or IOException or InvalidOperationException
            || error is OperationCanceledException && !http.RequestAborted.IsCancellationRequested)
        {
            return new(null, "Replication observations are unavailable.");
        }
    }

    public static string Timestamp(DateTimeOffset? value) => value?.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", System.Globalization.CultureInfo.InvariantCulture) ?? "—";
}
