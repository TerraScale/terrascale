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
            if (status.DatabaseId != database.Id || status.Regions is not { Count: > 0 and <= 9 } regions || regions.All(row => row.Region != home.Id)
                || regions.Any(row => string.IsNullOrWhiteSpace(row.Region) || row.LagMs is { } lag && (!double.IsFinite(lag) || lag < 0))
                || regions.Select(row => row.Region).Distinct(StringComparer.Ordinal).Count() != regions.Count)
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

    public static IReadOnlyList<(string Region, TerraBaseReplicaStatus? Observation)> Rows(TerraBaseDatabaseRegions status) =>
        [.. (status.Regions ?? []).Select(row => (row.Region, row.LastPulledAt is null && row.Revision is null ? null : (TerraBaseReplicaStatus?)row))
            .OrderBy(row => Common.UI.Regions.Rank(row.Region)).ThenBy(row => row.Region, StringComparer.Ordinal)];

    public static string Timestamp(DateTimeOffset? value) => value?.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", System.Globalization.CultureInfo.InvariantCulture) ?? "—";
}
