using System.Globalization;
using System.Text.Json.Serialization;

namespace TerraScale.Dashboard.Features.Home;

public sealed record HomeMcpOverview(string OrganizationId, string ProjectId, string ProjectName, int DatabaseCount,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? OrganizationMembers, int UnreadNotifications, int TotalNotifications,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? ActiveCredentials,
    HomeMcpActivity Activity, IReadOnlyList<HomeMcpTask> GettingStarted);
public sealed record HomeMcpActivity(string Range, bool Sample, bool Metered, string Note, IReadOnlyList<long> Reads,
    IReadOnlyList<long> Writes, IReadOnlyList<string> Labels,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? TotalReads,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? TotalWrites);
public sealed record HomeMcpTask(string Title,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] bool? Done, string Path);

public static class HomeMcpPresentation
{
    public static HomeMcpOverview For(AccountContext context, HomeMeasurement measurement, string range)
    {
        var organization = context.Organization ?? throw new AccountAccessException("Organization is unavailable.", 404);
        var project = context.Project ?? throw new AccountAccessException("Project is unavailable.", 404);
        var notifications = context.Account.Notifications.Where(item => item.VisibleTo(context)).ToArray();
        return new(organization.Id, project.Id, project.Name, project.Databases.Count,
            organization.ContainsMember(context.UserId) ? organization.Members.Values.Count(member => member.Status == "active") : null,
            notifications.Count(item => item.Unread), notifications.Length, measurement.ActiveCredentials,
            new(range, false, measurement.ActivityMeasured, measurement.ActivityNote,
                [.. measurement.Activity.Select(point => point.Reads)], [.. measurement.Activity.Select(point => point.Writes)],
                [.. measurement.Activity.Select(point => point.Start.UtcDateTime.ToString(range == "30d" ? "MMM d" : "MMM d, HH:mm 'UTC'", CultureInfo.InvariantCulture))],
                measurement.ActivityMeasured ? measurement.Reads : null, measurement.ActivityMeasured ? measurement.Writes : null),
            [new("Create first database", project.Databases.Count > 0, "/databases"),
                new("Create an Application credential", measurement.ActiveCredentials is { } count ? count > 0 : null, "/api-keys")]);
    }
}
