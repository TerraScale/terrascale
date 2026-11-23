using System.Text.Json.Serialization;
using TerraBase.Features.Catalog;

namespace TerraBase.Features.Management.Usage;

public sealed record UsageInput(string? Project, string? Database, string? Month);

public sealed record UsageBody(string Scope, string Month, IReadOnlyDictionary<string, long> Metrics);

public sealed record AnalyticsBody(string Scope, IReadOnlyList<UsageBody> Months);

public sealed record AllowanceBody(string Plan, int Databases);

public sealed record AllowancesBody(IReadOnlyList<AllowanceBody> Plans);

[JsonSourceGenerationOptions(System.Text.Json.JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(UsageInput))]
[JsonSerializable(typeof(UsageBody))]
[JsonSerializable(typeof(AnalyticsBody))]
[JsonSerializable(typeof(AllowancesBody))]
internal sealed partial class UsageJsonContext : JsonSerializerContext;

internal sealed class UsageReports(UsageMeter usage, TimeProvider time)
{
    public static Result<string, Validation> Month(string? month, DateTimeOffset now) =>
        month is null ? UsageMeter.Month(now)
        : month is { Length: 7 } && DateTimeOffset.TryParseExact(string.Concat(month, "-01"), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out _) ? month
        : new Validation("month is YYYY-MM", "invalid_month");

    public async ValueTask<Result<UsageBody, Validation, Unavailable>> UsageAsync(string scope, string? month, CancellationToken cancellationToken) =>
        Month(month, time.GetUtcNow()) switch
        {
            string checkedMonth => await usage.ReadAsync(scope, checkedMonth, cancellationToken) switch
            {
                IReadOnlyDictionary<string, long> metrics => new UsageBody(scope, checkedMonth, metrics),
                Unavailable unavailable => unavailable,
            },
            Validation invalid => invalid,
        };

    public async ValueTask<Result<AnalyticsBody, Unavailable>> AnalyticsAsync(string scope, CancellationToken cancellationToken)
    {
        var months = await usage.MonthsAsync(scope, cancellationToken) switch
        {
            IReadOnlyList<string> found => found,
            Unavailable => null,
        };
        if (months is null)
        {
            return new Unavailable("usage is unavailable; retry", "usage_unavailable");
        }

        List<UsageBody> bodies = [];
        foreach (var month in months.Append(UsageMeter.Month(time.GetUtcNow())).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            if (await usage.ReadAsync(scope, month, cancellationToken) switch { IReadOnlyDictionary<string, long> metrics => metrics, Unavailable => null } is { } values)
            {
                bodies.Add(new UsageBody(scope, month, values));
            }
        }

        return new AnalyticsBody(scope, bodies);
    }

    public static AllowancesBody Allowances() => new([.. Plans.All.Select(static plan => new AllowanceBody(plan, Plans.DatabaseLimit(plan)))]);
}
