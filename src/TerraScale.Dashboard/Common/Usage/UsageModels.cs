using TerraScale.Dashboard.Common.Accounts;
using TerraScale.Dashboard.Errors;

namespace TerraScale.Dashboard.Common.Usage
{
    public readonly union UsageSelectionResult(UsageSelection, NotFound, Forbidden);

    public sealed record UsageSelection(AccountContext Context, string Range, string DatabaseId, bool ExplicitProjectFilter)
    {
        public bool Member { get; init; } = true;
        public static UsageSelectionResult TryFor(AccountContext context, string projectId, string databaseId, string range)
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
                return new Forbidden("Selected organization is unavailable.");
            }

            var member = org.ContainsMember(context.UserId);
            var project = projectId.Length == 0 ? context.Project : org.Projects[projectId];
            if (project is not null && (project.OrganizationId != org.Id || !org.Projects.ContainsKey(project.Id)))
            {
                return new NotFound("Project is unavailable.");
            }

            if (!member && (projectId.Length == 0 || !ProjectCollaborator.Allows(org, project)))
            {
                return new Forbidden("Organization membership is required.");
            }

            if (databaseId.Length > 0 && (project is null || !project.Databases.TryGetValue(databaseId, out var database) || database.ProjectId != project.Id))
            {
                return new NotFound("Database is unavailable.");
            }

            var selection = new UsageSelection(context with { Organization = org, Project = project }, range is "1h" or "24h" or "30d" ? range : "7d", databaseId, project is not null && project.Id == projectId)
            {
                Member = member
            };
            return new(selection);
        }
        public string RangeLabel => Range switch { "1h" => "Current UTC hour", "24h" => "Last 24 hours", "30d" => "Last 30 days", _ => "Last 7 days" };
        public static (string Granularity, DateTimeOffset From) Buckets(string range, DateTimeOffset now)
        {
            var utc = now.UtcDateTime;
            var hour = new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, TimeSpan.Zero);
            return range switch { "1h" => ("hour", hour), "24h" => ("hour", hour.AddHours(-23)), "30d" => ("day", new DateTimeOffset(utc.Date, TimeSpan.Zero).AddDays(-29)), _ => ("hour", hour.AddHours(-167)) };
        }
    }
    public sealed record ActivityPoint(DateTimeOffset Start, long Reads, long Writes);
    public enum UsageSourceState { Measured, NotMetered, Unauthorized, Unavailable, NoProducer, Restricted, QueryTooExpensive }
    public sealed record UsagePeriod(DateTimeOffset From, DateTimeOffset To, string Granularity, long Reads, long Writes);
    public sealed record UsageReading(string ProjectId, string DatabaseId, long LogicalBytes, UsagePeriod Period, IReadOnlyList<ActivityPoint> Series, IReadOnlyList<UsageReading> Items, UsageMonth? Month = null);
    public sealed record UsageMonth(string Month, long ReadUnits, long WriteUnits, long Units, long? OrganizationUnits = null, long? OrganizationReadUnits = null,
        long Invocations = 0, long? OrganizationInvocations = null);
    public sealed record UsageObservation(UsageSourceState State, UsageReading? Reading = null);
    public sealed record UsageMeasurements(UsageObservation Scope, IReadOnlyDictionary<string, UsageObservation> Monthly)
    {
        public DateTimeOffset? MonthStart { get; init; }
        // The organization ledger retains deleted projects; never reconstruct it from current project totals.
        public long? OperationUnits(IEnumerable<string> projectIds, DateTimeOffset now)
        {
            return Units(projectIds, now, month => (month.OrganizationUnits, month.Units));
        }

        public long? SqlReadUnits(IEnumerable<string> projectIds, DateTimeOffset now)
        {
            return Units(projectIds, now, month => (month.OrganizationReadUnits, month.ReadUnits));
        }

        public long? FunctionInvocations(IEnumerable<string> projectIds, DateTimeOffset now)
        {
            var month = now.UtcDateTime.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
            long? published = null;
            long projects = 0;
            var counted = false;
            foreach (var id in projectIds)
            {
                if (Monthly.GetValueOrDefault(id) is not { State: UsageSourceState.Measured, Reading.Month: { } reading }
                    || reading.Month != month || reading.ReadUnits < 0 || reading.Invocations < 0 || reading.Units < reading.WriteUnits)
                {
                    return null;
                }

                counted = true;
                try { projects = checked(projects + reading.Invocations); }
                catch (OverflowException) { return null; }
                if (reading.OrganizationInvocations is { } organization)
                {
                    if (organization < reading.Invocations || published is not null && published != organization)
                    {
                        return null;
                    }

                    published = organization;
                }
                else if (published is not null)
                {
                    return null;
                }
            }

            if (!counted)
            {
                return null;
            }

            return published is { } total ? total >= projects ? total : null : projects;
        }

        private long? Units(IEnumerable<string> projectIds, DateTimeOffset now, Func<UsageMonth, (long? Organization, long Project)> counter)
        {
            var month = now.UtcDateTime.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
            long? units = null;
            long projectUnits = 0;
            foreach (var id in projectIds)
            {
                if (Monthly.GetValueOrDefault(id) is not { State: UsageSourceState.Measured, Reading.Month: { } reading } || counter(reading) is not ({ } organizationUnits, var project) ||
                    reading.Month != month || reading.ReadUnits < 0 || reading.Invocations < 0 || reading.Units < reading.WriteUnits)
                {
                    return null;
                }

                if (organizationUnits < project || units is not null && units != organizationUnits)
                {
                    return null;
                }

                try { projectUnits = checked(projectUnits + project); } catch (OverflowException) { return null; }
                units = organizationUnits;
            }
            return units is { } total && total >= projectUnits ? total : null;
        }
    }
    public interface IUsageMeasurements
    {
        Task<UsageMeasurements> ReadAsync(UsageSelection selection, CancellationToken cancellationToken);
    }
}
