using System.Globalization;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Platform.Clients;

namespace TerraScale.Dashboard.Features.Databases;

public sealed record DatabaseReplication(TerraBaseDatabaseRegions? Status, string Error = "")
{
    public static async Task<DatabaseReplication> LoadAsync(HttpContext http, AccountContext context, DatabaseRecord database, DatabaseService databases, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(databases);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        if (context.Organization is not { } organization)
        {
            return new(null, "Replication observations are unavailable.");
        }

        var token = await http.RequestServices.GetRequiredService<IProjectAccessTokens>()
            .GetAsync(context.Session, organization.Id, database.ProjectId, ["databases:read"], deadline.Token);
        await databases.HomeAsync(context, database, deadline.Token);
        var answer = await http.RequestServices.GetRequiredService<TerraBaseHttpClient>()
            .GetDatabaseRegionsAsync(ServiceCaller.User(token), database.ProjectId, database.Id, deadline.Token);
        return answer switch
        {
            TerraBaseDatabaseRegions status when Valid(status, database.Id) => new(status),
            TerraBaseDatabaseRegions => new(null, "Replication observations are unavailable."),
            TerraBaseRefusal { Status: 401 or 403 or 404 } refusal => throw refusal.Exception(),
            TerraBaseRefusal => new(null, "Replication observations are unavailable."),
        };
    }

    public static IReadOnlyList<TerraBaseRegionReplication> Rows(TerraBaseDatabaseRegions status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return [.. status.Regions.OrderBy(row => Common.UI.Regions.Rank(row.Region)).ThenBy(row => row.Region, StringComparer.Ordinal)];
    }

    public static string Timestamp(DateTimeOffset? value) => value?.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture) ?? "—";

    private static bool Valid(TerraBaseDatabaseRegions status, string databaseId) =>
        status.DatabaseId == databaseId && status.Regions.Count <= 16
        && status.Regions.All(row => !string.IsNullOrWhiteSpace(row.Region) && row.LagMs >= 0)
        && status.Regions.Select(row => row.Region).Distinct(StringComparer.Ordinal).Count() == status.Regions.Count;
}
