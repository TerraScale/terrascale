
namespace TerraScale.Dashboard.Features.Analytics;

public enum AnalyticsSourceState { Available, NotMetered, Unavailable }
public sealed record AnalyticsSelection(AccountContext Context, string Range, string DatabaseId, string Region)
{
    public static AnalyticsSelection For(AccountContext context, string projectId, string databaseId, string region, string range)
    {
        var org = context.Organization ?? throw new AccountAccessException("Select an organization.", 404);
        if (context.Session.UserId != context.UserId || !org.ContainsMember(context.UserId)) throw new AccountAccessException("Organization membership is required.");
        var id = projectId.Length == 0 ? context.Project?.Id ?? "" : projectId;
        var project = org.Projects.GetValueOrDefault(id) ?? throw new AccountAccessException("Project is unavailable.", 404);
        if (project.OrganizationId != org.Id) throw new AccountAccessException("Project is unavailable.", 404);
        if (databaseId.Length > 0 && (!project.Databases.TryGetValue(databaseId, out var database) || database.ProjectId != id))
            throw new AccountAccessException("Database is unavailable.", 404);
        var projectRegions = project.Databases.Values.SelectMany(db => db.Regions.Prepend(db.Region)).Where(code => code.Length > 0).Distinct().ToArray();
        var databaseRegions = project.Databases.Values.Where(db => databaseId.Length == 0 || db.Id == databaseId).SelectMany(db => db.Regions.Prepend(db.Region)).Where(code => code.Length > 0).Distinct().ToArray();
        if (region.Length > 0 && !databaseRegions.Contains(region))
        {
            if (databaseId.Length == 0 || !projectRegions.Contains(region)) throw new AccountAccessException("Select a region used by this database or project.", 400);
            region = "";
        }
        return new(context with { Project = project }, range is "1h" or "24h" or "7d" or "30d" ? range : "24h", databaseId, region);
    }
    public IReadOnlyList<string> Regions => Context.Project!.Databases.Values.Where(db => DatabaseId.Length == 0 || db.Id == DatabaseId)
        .SelectMany(db => db.Regions.Prepend(db.Region)).Where(code => code.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
}

public sealed record AnalyticsPoint(string DatabaseId, string Region, DateTimeOffset Start, IReadOnlyDictionary<string, long> Requests,
    IReadOnlyDictionary<string, long> Operations, long Errors, double? P50Ms, double? P95Ms, double? P99Ms,
    long? StorageBytes, long ClientSentBytes, long ClientReceivedBytes, long ReplicationSentBytes, long ReplicationReceivedBytes);
public sealed record AnalyticsBatch(AnalyticsSourceState State, IReadOnlyList<AnalyticsPoint> Points);
public interface IAnalyticsBackend
{
    Task<AnalyticsBatch> ReadAsync(AnalyticsSelection selection, CancellationToken cancellationToken);
}
