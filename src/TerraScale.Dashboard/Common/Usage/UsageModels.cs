namespace TerraScale.Dashboard.Common.Usage
{
    public sealed record UsageSelection(AccountContext Context, string Range, string DatabaseId, bool ExplicitProjectFilter)
    {
        public static UsageSelection For(AccountContext context, string projectId, string databaseId, string range)
        {
            var org = context.Organization ?? throw new AccountAccessException("Selected organization is unavailable.");
            if (context.Session.UserId != context.UserId || !org.ContainsMember(context.UserId))
            {
                throw new AccountAccessException("Organization membership is required.");
            }

            var project = projectId.Length == 0 ? context.Project : org.Projects.GetValueOrDefault(projectId)
                ?? throw new AccountAccessException("Project is unavailable.", 404);
            if (project is not null && (project.OrganizationId != org.Id || !org.Projects.ContainsKey(project.Id)))
            {
                throw new AccountAccessException("Project is unavailable.", 404);
            }

            if (databaseId.Length > 0 && (project is null || !project.Databases.TryGetValue(databaseId, out var database) || database.ProjectId != project.Id))
            {
                throw new AccountAccessException("Database is unavailable.", 404);
            }

            return new(context with { Project = project }, range is "1h" or "24h" or "30d" ? range : "7d", databaseId, project is not null && project.Id == projectId);
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
    public sealed record UsageReading(string ProjectId, string DatabaseId, long LogicalBytes, long Documents, UsagePeriod Period, IReadOnlyList<ActivityPoint> Series, IReadOnlyList<UsageReading> Items, UsageMonth? Month = null);
    // TerraBase's billed 4 KiB operation units for one UTC month ("yyyy-MM"); older TerraBase omits it.
    public sealed record UsageMonth(string Month, long ReadUnits, long WriteUnits, long Units, long? OrganizationUnits = null);
    public sealed record UsageObservation(UsageSourceState State, UsageReading? Reading = null);
    public sealed record UsageMeasurements(UsageObservation Scope, IReadOnlyDictionary<string, UsageObservation> Monthly)
    {
        public DateTimeOffset? MonthStart { get; init; }
        // The organization ledger retains deleted projects; never reconstruct it from current project totals.
        public long? OperationUnits(IEnumerable<string> projectIds, DateTimeOffset now)
        {
            var month = now.UtcDateTime.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
            long? units = null;
            long projectUnits = 0;
            foreach (var id in projectIds)
            {
                if (Monthly.GetValueOrDefault(id) is not { State: UsageSourceState.Measured, Reading.Month: { OrganizationUnits: { } organizationUnits } reading } ||
                    reading.Month != month || reading.ReadUnits < 0 || reading.WriteUnits < 0 || organizationUnits < reading.Units ||
                    units is not null && units != organizationUnits)
                {
                    return null;
                }

                try { if (checked(reading.ReadUnits + reading.WriteUnits) != reading.Units) { return null; } projectUnits = checked(projectUnits + reading.Units); } catch (OverflowException) { return null; }
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
