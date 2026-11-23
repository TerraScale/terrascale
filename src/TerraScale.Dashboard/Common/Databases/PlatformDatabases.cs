using TerraScale.Dashboard.Platform.Clients;
using System.Net;
using System.Text.Json;

namespace TerraScale.Dashboard.Common.Databases
{
    public static class PlatformDatabases
    {
        public const string OrganizationId = "00000000000000000000000001";
        public const string AuthProjectId = "00000000000000000000000002";

        public static readonly IReadOnlyDictionary<string, (string Name, string ProjectId)> Databases = new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            ["00000000000000000000000003"] = ("ts-auth", AuthProjectId),
            ["00000000000000000000000004"] = ("ts-dashboard", "00000000000000000000000005"),
        };

        public static bool IsPlatform(string? organizationId)
        {
            return organizationId == OrganizationId;
        }

        public static DatabaseProviderException ReadOnly()
        {
            return new(HttpStatusCode.Forbidden, "Platform databases are read-only here.");
        }

        public static async Task HydrateAsync(OrganizationRecord org, ControlPlaneClient control, SessionRecord session, CancellationToken cancellationToken)
        {
            var outcome = await control.ListPlatformDatabasesAsync(ServiceCaller.User(session.AccessToken), cancellationToken);
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
