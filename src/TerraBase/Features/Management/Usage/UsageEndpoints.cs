using System.Globalization;
using System.Text.Json.Serialization;
using TerraBase.Features.Callers;
using TerraBase.Features.Catalog;
using TerraBase.Hosting;
using TerraScale.Contracts.Cluster;

namespace TerraBase.Features.Management.Usage;

internal readonly union UsageMonthParse(string, InvalidInput);

internal readonly union UsageOutcome(UsageBody, InvalidInput, BackendUnavailable);

internal readonly union AnalyticsOutcome(AnalyticsBody, BackendUnavailable);

internal readonly union UsageResponse(UsageBody, InvalidInput, CredentialRejected, CallerForbidden, ResourceNotFound, BackendUnavailable);

internal readonly union AnalyticsResponse(AnalyticsBody, InvalidInput, CredentialRejected, CallerForbidden, ResourceNotFound, BackendUnavailable);

internal readonly union OrganizationUsageResponse(OrganizationUsageBody, InvalidInput, CredentialRejected, CallerForbidden, BackendUnavailable);

public sealed record UsageInput(string? Project, string? Database, string? Month);

public sealed record UsageBody(string Scope, string Month, IReadOnlyDictionary<string, long> Metrics);

public sealed record AnalyticsBody(string Scope, IReadOnlyList<UsageBody> Months);

public sealed record AllowanceBody(string Plan, int Databases);

public sealed record AllowancesBody(IReadOnlyList<AllowanceBody> Plans);

public sealed record OrganizationUsageInput(string? Organization, string? Month);

public sealed record UsageTotalsBody(long Requests, long Reads, long Writes, long RowsRead, long RowsWritten, long StrongCommits, long StorageByteHours)
{
    public static UsageTotalsBody From(IReadOnlyDictionary<string, long> metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        return new(metrics[UsageMetrics.Requests], metrics[UsageMetrics.Reads], metrics[UsageMetrics.Writes], metrics[UsageMetrics.RowsRead],
            metrics[UsageMetrics.RowsWritten], metrics[UsageMetrics.StrongCommits], metrics[UsageMetrics.StorageByteHours]);
    }

    public static UsageTotalsBody Sum(IEnumerable<UsageTotalsBody> items) =>
        items.Aggregate(new UsageTotalsBody(0, 0, 0, 0, 0, 0, 0), static (total, item) => new(
            checked(total.Requests + item.Requests), checked(total.Reads + item.Reads), checked(total.Writes + item.Writes), checked(total.RowsRead + item.RowsRead),
            checked(total.RowsWritten + item.RowsWritten), checked(total.StrongCommits + item.StrongCommits), checked(total.StorageByteHours + item.StorageByteHours)));
}

public sealed record ProjectUsageBody(
    string ProjectId, long Requests, long Reads, long Writes, long RowsRead, long RowsWritten, long StrongCommits, long StorageByteHours)
{
    public static ProjectUsageBody From(string projectId, UsageTotalsBody usage)
    {
        ArgumentNullException.ThrowIfNull(usage);
        return new(projectId, usage.Requests, usage.Reads, usage.Writes, usage.RowsRead, usage.RowsWritten, usage.StrongCommits, usage.StorageByteHours);
    }

    public UsageTotalsBody Totals() => new(Requests, Reads, Writes, RowsRead, RowsWritten, StrongCommits, StorageByteHours);
}

public sealed record OrganizationUsageBody(
    string OrganizationId, string Month, bool Complete, DateTimeOffset AsOf, IReadOnlyList<string> Gaps, UsageTotalsBody Totals, IReadOnlyList<ProjectUsageBody> Projects);

[JsonSourceGenerationOptions(System.Text.Json.JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(UsageInput))]
[JsonSerializable(typeof(UsageBody))]
[JsonSerializable(typeof(AnalyticsBody))]
[JsonSerializable(typeof(AllowancesBody))]
[JsonSerializable(typeof(OrganizationUsageInput))]
[JsonSerializable(typeof(OrganizationUsageBody))]
internal sealed partial class UsageJsonContext : JsonSerializerContext;

internal sealed class UsageReports(UsageMeter usage, StorageLedger ledger, StorageOwnership ownership, IClusterCoordinator coordinator, TimeProvider time)
{
    public const string PeerUnhealthy = "peer_unhealthy";

    public const string ProjectLimit = "project_limit";

    public const string FlushPending = "usage_flush_pending";

    public const string StorageIncomplete = "storage_sample_incomplete";

    private static readonly TimeSpan SampleGrace = TimeSpan.FromMinutes(15);

    public static CredentialRejected? Unauthenticated(Caller caller) => caller switch
    {
        AnonymousCaller => new CredentialRejected("organization usage needs an internal peer or service credential", "unauthorized"),
        PeerCaller or ServiceCaller or ApiKeyCaller or UserCaller or ProjectCaller or EndUserCaller => null,
    };

    public static bool ReadsOrganizations(Caller caller) => caller switch
    {
        PeerCaller => true,
        ServiceCaller service => service.Grants.Contains("usage.read") || service.Grants.Contains("admin"),
        AnonymousCaller or ApiKeyCaller or UserCaller or ProjectCaller or EndUserCaller => false,
    };

    public async ValueTask<OrganizationUsageResponse> OrganizationAsync(Caller caller, OrganizationUsageInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (Unauthenticated(caller) is { } unauthenticated)
        {
            return unauthenticated;
        }

        if (!ReadsOrganizations(caller))
        {
            return new CallerForbidden("organization usage is only for internal peers and services with the usage.read grant", "forbidden_caller");
        }

        if (input.Organization is not { Length: 26 } organization || !Ulid.TryParse(organization, out var organizationId)
            || !string.Equals(organizationId.ToString(), organization, StringComparison.Ordinal))
        {
            return new InvalidInput("organization must be a canonical uppercase ULID", "invalid_organization");
        }

        var asOf = time.GetUtcNow();
        return Month(input.Month, asOf) switch
        {
            string month => await OrganizationAsync(organization, organizationId, month, asOf, cancellationToken),
            InvalidInput invalid => invalid,
        };
    }

    public static IReadOnlyList<long> ExpectedStorageHours(string month, DateTimeOffset asOf)
    {
        var start = DateTimeOffset.ParseExact(string.Concat(month, "-01"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
        if (start >= asOf)
        {
            return [];
        }

        var end = start.AddMonths(1);
        var horizon = asOf - SampleGrace < end ? asOf - SampleGrace : end;
        var count = horizon > start ? (long)Math.Floor((horizon - start) / StorageSampler.Slot) : 0;
        var first = StorageLedger.HourIndex(start);
        return [.. Enumerable.Range(0, (int)count).Select(offset => first + offset)];
    }

    private async ValueTask<OrganizationUsageResponse> OrganizationAsync(string organization, Ulid organizationId, string month, DateTimeOffset asOf, CancellationToken cancellationToken)
    {
        var coverageOutcome = await ledger.CoverageAsync(month, cancellationToken);
        var coverage = coverageOutcome switch
        {
            IReadOnlyDictionary<string, IReadOnlySet<long>> hours => hours,
            BackendUnavailable => null,
        };
        if (coverage is null)
        {
            return new BackendUnavailable("usage is unavailable; retry", "usage_unavailable");
        }

        var countersOutcome = await usage.OrganizationAsync(organizationId, month, cancellationToken);
        return countersOutcome switch
        {
            OrganizationCounters counters => Organization(organization, month, asOf, counters, coverage),
            BackendUnavailable => new BackendUnavailable("usage is unavailable; retry", "usage_unavailable"),
        };
    }

    private OrganizationUsageBody Organization(string organization, string month, DateTimeOffset asOf, OrganizationCounters counters, IReadOnlyDictionary<string, IReadOnlySet<long>> coverage)
    {
        List<ProjectUsageBody> projects = [.. counters.Projects.Select(static project => ProjectUsageBody.From(project.ProjectId.ToString(), UsageTotalsBody.From(project.Metrics)))];
        List<string> gaps = [];
        if (coordinator.Peers.Any(static peer => peer.Status != PeerStatus.Healthy))
        {
            gaps.Add(PeerUnhealthy);
        }

        if (counters.Truncated)
        {
            gaps.Add(ProjectLimit);
        }

        if (usage.FlushFailing)
        {
            gaps.Add(FlushPending);
        }

        var expected = ExpectedStorageHours(month, asOf);
        if (expected.Count > 0 && ownership.Regions().Any(region => coverage.GetValueOrDefault(region) is not { } covered || !expected.All(covered.Contains)))
        {
            gaps.Add(StorageIncomplete);
        }

        return new OrganizationUsageBody(organization, month, gaps.Count == 0, asOf, gaps, UsageTotalsBody.Sum(projects.Select(static project => project.Totals())), projects);
    }

    public static UsageMonthParse Month(string? month, DateTimeOffset now) =>
        month is null ? UsageMeter.Month(now)
        : month is { Length: 7 } && DateTimeOffset.TryParseExact(string.Concat(month, "-01"), "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out _) ? month
        : new InvalidInput("month is YYYY-MM", "invalid_month");

    public async ValueTask<UsageOutcome> UsageAsync(UsageSubject subject, string? month, CancellationToken cancellationToken) =>
        Month(month, time.GetUtcNow()) switch
        {
            string checkedMonth => await usage.ReadAsync(subject, checkedMonth, cancellationToken) switch
            {
                IReadOnlyDictionary<string, long> metrics => new UsageBody(subject.Scope, checkedMonth, metrics),
                BackendUnavailable unavailable => unavailable,
            },
            InvalidInput invalid => invalid,
        };

    public async ValueTask<AnalyticsOutcome> AnalyticsAsync(UsageSubject subject, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subject);
        var monthsOutcome = await usage.MonthsAsync(subject, cancellationToken);
        var months = monthsOutcome switch
        {
            IReadOnlyList<string> found => found,
            BackendUnavailable => null,
        };
        if (months is null)
        {
            return new BackendUnavailable("usage is unavailable; retry", "usage_unavailable");
        }

        List<UsageBody> bodies = [];
        foreach (var month in months.Append(UsageMeter.Month(time.GetUtcNow())).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            if (await usage.ReadAsync(subject, month, cancellationToken) switch { IReadOnlyDictionary<string, long> metrics => metrics, BackendUnavailable => null } is { } values)
            {
                bodies.Add(new UsageBody(subject.Scope, month, values));
            }
        }

        return new AnalyticsBody(subject.Scope, bodies);
    }

    public static AllowancesBody Allowances() => new([.. Plans.All.Select(static plan => new AllowanceBody(plan, Plans.DatabaseLimit(plan)))]);
}
