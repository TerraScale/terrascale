using TerraScale.Dashboard.Common.Accounts;
using TerraScale.Dashboard.Errors;

namespace TerraScale.Dashboard.Features.Analytics
{

    public readonly union AnalyticsSelectionResult(AnalyticsSelection, DashboardNotFound, DashboardForbidden, DashboardValidation);

    public enum AnalyticsSourceState { Available, NotMetered, Unavailable }
    public sealed record AnalyticsSelection(AccountContext Context, string Range, string DatabaseId, string Region)
    {
        public bool Member { get; init; } = true;
        public static AnalyticsSelection For(AccountContext context, string projectId, string databaseId, string region, string range)
        {
            return TryFor(context, projectId, databaseId, region, range) switch
            {
                AnalyticsSelection selection => selection,
                DashboardNotFound failure => throw new AccountAccessException(failure.Message, 404),
                DashboardForbidden failure => throw new AccountAccessException(failure.Message),
                DashboardValidation failure => throw new AccountAccessException(failure.Message, 400),
            };
        }

        public static AnalyticsSelectionResult TryFor(AccountContext context, string projectId, string databaseId, string region, string range)
        {
            if (context.Session.UserId != context.UserId)
            {
                return new DashboardForbidden("Organization membership is required.");
            }

            OrganizationRecord org;
            if (projectId.Length > 0)
            {
                if (context.Organizations.FirstOrDefault(item => item.Projects.ContainsKey(projectId)) is not { } byProject)
                {
                    return new DashboardNotFound("Project is unavailable.");
                }

                org = byProject;
            }
            else if (context.Organization is { } selected)
            {
                org = selected;
            }
            else
            {
                return new DashboardNotFound("Select an organization.");
            }

            var member = org.ContainsMember(context.UserId);
            var id = projectId.Length == 0 ? context.Project?.Id ?? "" : projectId;
            if (org.Projects.GetValueOrDefault(id) is not { } project)
            {
                return new DashboardNotFound("Project is unavailable.");
            }

            if (project.OrganizationId != org.Id)
            {
                return new DashboardNotFound("Project is unavailable.");
            }

            if (!member && !ProjectCollaborator.Allows(org, project))
            {
                return new DashboardForbidden("Organization membership is required.");
            }

            if (databaseId.Length > 0 && (!project.Databases.TryGetValue(databaseId, out var database) || database.ProjectId != id))
            {
                return new DashboardNotFound("Database is unavailable.");
            }
            if (region.Length > 0)
            {
                if (DatabaseRegion.FromId(region.ToLowerInvariant())?.Code is not { } valid)
                {
                    return new DashboardValidation("Select a valid region.");
                }

                region = valid;
            }

            var selection = new AnalyticsSelection(context with { Organization = org, Project = project }, range is "1h" or "24h" or "7d" or "30d" ? range : "24h", databaseId, region)
            {
                Member = member
            };
            return new(selection);
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
