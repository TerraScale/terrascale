
using TerraScale.Dashboard.Common.Accounts;

namespace TerraScale.Dashboard.Features.Analytics
{
    public enum AnalyticsSourceState { Available, NotMetered, Unavailable }
    public sealed record AnalyticsSelection(AccountContext Context, string Range, string DatabaseId, string Region)
    {
        public bool Member { get; init; } = true;
        public static AnalyticsSelection For(AccountContext context, string projectId, string databaseId, string region, string range)
        {
            if (context.Session.UserId != context.UserId)
            {
                throw new AccountAccessException("Organization membership is required.");
            }

            var org = projectId.Length > 0
                ? context.Organizations.FirstOrDefault(item => item.Projects.ContainsKey(projectId))
                    ?? throw new AccountAccessException("Project is unavailable.", 404)
                : context.Organization ?? throw new AccountAccessException("Select an organization.", 404);
            var member = org.ContainsMember(context.UserId);
            var id = projectId.Length == 0 ? context.Project?.Id ?? "" : projectId;
            var project = org.Projects.GetValueOrDefault(id) ?? throw new AccountAccessException("Project is unavailable.", 404);
            if (project.OrganizationId != org.Id)
            {
                throw new AccountAccessException("Project is unavailable.", 404);
            }

            if (!member && !ProjectCollaborator.Allows(org, project))
            {
                throw new AccountAccessException("Organization membership is required.");
            }

            if (databaseId.Length > 0 && (!project.Databases.TryGetValue(databaseId, out var database) || database.ProjectId != id))
            {
                throw new AccountAccessException("Database is unavailable.", 404);
            }
            // Placement is not reporting scope: historical and automatically admitted regions remain queryable.
            if (region.Length > 0)
            {
                region = DatabaseRegion.FromId(region.ToLowerInvariant())?.Code ?? throw new AccountAccessException("Select a valid region.", 400);
            }

            return new(context with { Organization = org, Project = project }, range is "1h" or "24h" or "7d" or "30d" ? range : "24h", databaseId, region)
            {
                Member = member
            };
        }
        public IReadOnlyList<string> ObservedRegions { get; init; } = [];
        public IReadOnlyList<string> Regions => [.. Context.Project!.Databases.Values.Where(db => DatabaseId.Length == 0 || db.Id == DatabaseId)
            .SelectMany(db => db.Regions.Prepend(db.Region)).Concat(ObservedRegions).Append(Region).Where(code => code.Length > 0)
            .Select(code => code.ToUpperInvariant()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }

    public sealed record AnalyticsPoint(string DatabaseId, string Region, DateTimeOffset Start, IReadOnlyDictionary<string, long> Requests,
        IReadOnlyDictionary<string, long> Operations, long Errors, double? P50Ms, double? P95Ms, double? P99Ms,
        long? StorageBytes, long ClientSentBytes, long ClientReceivedBytes);
    public sealed record AnalyticsBatch(AnalyticsSourceState State, IReadOnlyList<AnalyticsPoint> Points);
    public interface IAnalyticsBackend
    {
        Task<AnalyticsBatch> ReadAsync(AnalyticsSelection selection, CancellationToken cancellationToken);
    }
}
