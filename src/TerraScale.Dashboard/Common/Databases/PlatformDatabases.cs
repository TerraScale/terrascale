using TerraScale.Dashboard.Platform.Clients;
using System.Net;
using System.Text.Json;
using TerraScale.Contracts.ControlPlane;

namespace TerraScale.Dashboard.Common.Databases
{
    public static class PlatformDatabases
    {
        public static readonly IReadOnlyDictionary<string, (string Name, string ProjectId)> Databases = new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            [PlatformIdentity.AuthDatabaseId] = (PlatformIdentity.AuthDatabaseName, PlatformIdentity.AuthProjectId),
            [PlatformIdentity.DashboardDatabaseId] = (PlatformIdentity.DashboardDatabaseName, PlatformIdentity.DashboardProjectId),
            [PlatformIdentity.PlatformDatabaseId] = (PlatformIdentity.PlatformDatabaseName, PlatformIdentity.PlatformProjectId),
        };

        public static bool IsPlatform(string? organizationId)
        {
            return organizationId == PlatformIdentity.OrganizationId;
        }

        public static bool IsPlatformProject(string? projectId)
        {
            return projectId is PlatformIdentity.AuthProjectId or PlatformIdentity.DashboardProjectId or PlatformIdentity.PlatformProjectId;
        }

        public static bool ReadsAsOperator(AccountContext context, ProjectRecord project)
        {
            return IsPlatformProject(project.Id) && context.Organization is { MfaRequired: false } organization && IsPlatform(organization.Id) &&
                project.OrganizationId == organization.Id && organization.ContainsMember(context.UserId) &&
                ReferenceEquals(organization.Projects.GetValueOrDefault(project.Id), project);
        }

        public static DatabaseProviderException ReadOnly()
        {
            return new(HttpStatusCode.Forbidden, "Platform databases are read-only here.");
        }

        public static async Task HydrateAsync(OrganizationRecord org, ControlPlaneClient control, CancellationToken cancellationToken)
        {
            var outcome = await control.ListPlatformDatabasesAsync(cancellationToken);
            using var json = outcome switch
            {
                JsonElement listed => JsonDocument.Parse(listed.GetRawText()),
                TerraBaseRefusal refusal => throw new DatabaseProviderException((HttpStatusCode)refusal.Status),
            };
            foreach (var item in Items(json))
            {
                var id = item.GetProperty("id").GetString()!;
                if (!Databases.TryGetValue(id, out var known) || item.GetProperty("name").GetString() != known.Name ||
                    org.Projects.GetValueOrDefault(known.ProjectId) is not { } project)
                {
                    continue;
                }

                var home = item.GetProperty("homeRegion").GetString() ?? "";
                if (project.Regions.Count == 0 && home.Length > 0)
                {
                    project.Regions = [home];
                }
                project.Databases[id] = new DatabaseRecord
                {
                    Id = id,
                    ProjectId = known.ProjectId,
                    Name = known.Name,
                    Region = home,
                    Status = "ready",
                    AllRegions = item.GetProperty("allRegions").GetBoolean(),
                    Regions = [home, .. item.GetProperty("replicaRegions").EnumerateArray().Select(region => region.GetString() ?? "").Where(region => region.Length > 0)],
                };
            }
        }

        private static JsonElement.ArrayEnumerator Items(JsonDocument json)
        {
            return json.RootElement.GetProperty("items").EnumerateArray();
        }

    }
}
