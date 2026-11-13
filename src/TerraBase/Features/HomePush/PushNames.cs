using System.Globalization;
using TerraBase.Configuration;
using TerraBase.Primitives;
using TerraBase.Tenancy;

namespace TerraBase.Features.HomePush;

internal static class PushNames
{
    public static string Stream(string origin) => "TERRABASE_PUSH_" + origin.ToUpperInvariant();

    public static string Subjects(string origin) => $"terrabase.push.{origin}.>";

    public static string Subject(string origin, string home, TenantContext database) =>
        $"terrabase.push.{origin}.{home}.{database.OrganizationId}.{database.ProjectId}.{database.DatabaseId}";

    public static string DatabaseFilter(string origin, TenantContext database) =>
        $"terrabase.push.{origin}.*.{database.OrganizationId}.{database.ProjectId}.{database.DatabaseId}";

    public static string Consumer(string home, long epoch) => ConsumerPrefix(home) + epoch.ToString(CultureInfo.InvariantCulture);

    public static long? ConsumerEpoch(string home, string name) =>
        name.StartsWith(ConsumerPrefix(home), StringComparison.Ordinal)
        && long.TryParse(name.AsSpan(ConsumerPrefix(home).Length), NumberStyles.None, CultureInfo.InvariantCulture, out var epoch)
            ? epoch
            : null;

    private static string ConsumerPrefix(string home) => "home-" + home + "-epoch-";

    public static string Applied(string origin, TenantContext database) =>
        $"terrabase.applied.{origin}.{database.OrganizationId}.{database.ProjectId}.{database.DatabaseId}";

    public static string AppliedAll(string origin) => $"terrabase.applied.{origin}.>";

    public static string ParkStream(string home) => "TERRABASE_PUSH_PARK_" + home.ToUpperInvariant();

    public static string ParkSubjects(string home) => $"terrabase.push-park.{home}.>";

    public static string? Origin(string subject) => subject.Split('.') is [_, _, var origin, ..] && TerraBaseOptions.IsRegion(origin) ? origin : null;

    public static TenantContext? Database(string subject)
    {
        var parts = subject.Split('.');
        return parts.Length >= 5 && EntityIds.IsValid(parts[^3]) && EntityIds.IsValid(parts[^2]) && EntityIds.IsValid(parts[^1])
            ? new TenantContext(parts[^3], PlanTier.Hobby, parts[^2], parts[^1])
            : null;
    }
}
