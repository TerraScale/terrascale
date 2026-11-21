using TerraScale.Dashboard.Common.Accounts;
using TerraScale.Dashboard.Errors;

namespace TerraScale.Dashboard.Features.Analytics
{
    using NotFound = TerraScale.Dashboard.Errors.NotFound;

    public readonly union AnalyticsSelectionResult(AnalyticsSelection, NotFound, Forbidden, Validation);

    public enum AnalyticsSourceState { Available, NotMetered, Unavailable }
    public sealed record AnalyticsSelection(AccountContext Context, string Range, string DatabaseId, string Region)
    {
        public bool Member { get; init; } = true;
        public static AnalyticsSelection For(AccountContext context, string projectId, string databaseId, string region, string range)
        {
            return TryFor(context, projectId, databaseId, region, range) switch
            {
                AnalyticsSelection selection => selection,
                NotFound failure => throw new AccountAccessException(failure.Message ?? "Not found.", 404),
                Forbidden failure => throw new AccountAccessException(failure.Message ?? "Forbidden."),
                Validation failure => throw new AccountAccessException(failure.Message ?? "Invalid request.", 400),
            };
        }

        public static AnalyticsSelectionResult TryFor(AccountContext context, string projectId, string databaseId, string region, string range)
        {
            if (context.Session.UserId != context.UserId)
            {
                return new Forbidden("Organization membership is required.");
            }

            OrganizationRecord org;
            if (projectId.Length > 0)
            {
                if (context.Organizations.FirstOrDefault(item => item.Projects.ContainsKey(projectId)) is not { } byProject)
                {
                    return new NotFound("Project is unavailable.");
                }

                org = byProject;
            }
            else if (context.Organization is { } selected)
            {
                org = selected;
            }
            else
            {
                return new NotFound("Select an organization.");
            }

            var member = org.ContainsMember(context.UserId);
            var id = projectId.Length == 0 ? context.Project?.Id ?? "" : projectId;
            if (org.Projects.GetValueOrDefault(id) is not { } project)
            {
                return new NotFound("Project is unavailable.");
            }

            if (project.OrganizationId != org.Id)
            {
                return new NotFound("Project is unavailable.");
            }

            if (!member && !ProjectCollaborator.Allows(org, project))
            {
                return new Forbidden("Organization membership is required.");
            }

            if (databaseId.Length > 0 && (!project.Databases.TryGetValue(databaseId, out var database) || database.ProjectId != id))
            {
                return new NotFound("Database is unavailable.");
            }
            // Placement is not reporting scope: historical and automatically admitted regions remain queryable.
            if (region.Length > 0)
            {
                if (DatabaseRegion.FromId(region.ToLowerInvariant())?.Code is not { } valid)
                {
                    return new Validation("Select a valid region.");
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
