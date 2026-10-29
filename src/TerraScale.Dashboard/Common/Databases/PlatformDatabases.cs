using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.Common.Databases
{
    // TerraScale's own databases, as TerraBase seeds them (terrascale docs/terrabase/platform-databases.md). Platform admins
    // browse them in the same pages customers use, read-only, through TerraBase's redacted and audited explore API with
    // their own ts-auth access token. TerraBase checks platform_role=admin on every call; nothing here writes.
    public static class PlatformDatabases
    {
        public const string OrganizationId = "00000000000000000000000001";

        // Fixed database id to its name and owning project.
        public static readonly IReadOnlyDictionary<string, (string Name, string ProjectId)> Databases = new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            ["00000000000000000000000003"] = ("ts-auth", "00000000000000000000000002"),
            ["00000000000000000000000004"] = ("ts-dashboard", "00000000000000000000000005"),
        };

        private const string Explore = "/v1/platform/databases";

        public static bool IsPlatform(string? organizationId)
        {
            return organizationId == OrganizationId;
        }

        // Everything explore does not serve, writes included, stops here.
        public static DatabaseProviderException ReadOnly()
        {
            return new(HttpStatusCode.Forbidden, "Platform databases are read-only here.");
        }

        // The platform organization's projects get their databases from TerraBase; the dashboard stores none for them.
        public static async Task HydrateAsync(OrganizationRecord org, HttpClient http, SessionRecord session, CancellationToken cancellationToken)
        {
            using var document = await GetAsync(http, session, Explore, cancellationToken) ?? throw new DatabaseProviderException(HttpStatusCode.NotFound);
            foreach (var item in Items(document))
            {
                var id = item.GetProperty("id").GetString()!;
                if (!Databases.TryGetValue(id, out var known) || item.GetProperty("name").GetString() != known.Name ||
                    org.Projects.GetValueOrDefault(known.ProjectId) is not { } project)
                {
                    continue;
                }

                var home = item.GetProperty("homeRegion").GetString() ?? "";
                project.Region = project.Region.Length > 0 ? project.Region : home;
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

        public static async Task<IReadOnlyList<TerraBaseCollection>> CollectionsAsync(HttpClient http, SessionRecord session, string database, CancellationToken cancellationToken)
        {
            using var document = await GetAsync(http, session, $"{Explore}/{Name(database)}/collections", cancellationToken) ?? throw new DatabaseProviderException(HttpStatusCode.NotFound);
            return [.. Items(document).Select(item => new TerraBaseCollection(item.GetProperty("name").GetString()!,
                DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(item.GetProperty("created_at").GetString()!, System.Globalization.CultureInfo.InvariantCulture)),
                item.TryGetProperty("id", out var id) ? id.GetString() ?? "" : ""))];
        }

        // Documents in id order only: explore has no filters or sorts.
        public static async Task<TerraBaseDocumentPage> DocumentsAsync(HttpClient http, SessionRecord session, string database, string collection, TerraBaseQuery query, CancellationToken cancellationToken)
        {
            if (query.Filter is not null || query.Project is not null || query.Sort is { Count: > 0 } sort && (sort.Count > 1 || sort[0].Path != "$id" || sort[0].Order is not (null or "asc")))
            {
                throw new AccountAccessException("Platform databases list documents in id order only.", 422);
            }

            return await ListAsync(http, session, database, collection, Math.Clamp(query.Limit ?? 50, 1, 1000), query.Cursor, cancellationToken);
        }

        public static async Task<TerraBaseDocumentPage> ListAsync(HttpClient http, SessionRecord session, string database, string collection, int limit, string? cursor, CancellationToken cancellationToken)
        {
            var path = $"{Explore}/{Name(database)}/collections/{Uri.EscapeDataString(collection)}/documents?limit={limit}" + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor));
            using var document = await GetAsync(http, session, path, cancellationToken) ?? throw new DatabaseProviderException(HttpStatusCode.NotFound);
            var next = document.RootElement.TryGetProperty("next_cursor", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            return new([.. Items(document).Select(Document)], next);
        }

        public static async Task<TerraBaseDocument?> GetAsync(HttpClient http, SessionRecord session, string database, string collection, string id, CancellationToken cancellationToken)
        {
            using var document = await GetAsync(http, session, $"{Explore}/{Name(database)}/collections/{Uri.EscapeDataString(collection)}/documents/by-key?id={Uri.EscapeDataString(id)}", cancellationToken);
            return document is null ? null : Document(document.RootElement);
        }

        // Only the two platform names reach TerraBase.
        private static string Name(string database)
        {
            return Databases.Values.Any(known => known.Name == database) ? database : throw new DatabaseProviderException(HttpStatusCode.NotFound);
        }

        private static IEnumerable<JsonElement> Items(JsonDocument document)
        {
            return document.RootElement.GetProperty("items").EnumerateArray();
        }

        private static TerraBaseDocument Document(JsonElement item)
        {
            return new(item.GetProperty("id").GetString()!, item.GetProperty("version").GetString()!, item.GetProperty("state").GetString()!, item.GetProperty("document").Clone());
        }

        // null on 404; 401 and 403 mean the caller is not (or no longer) a platform admin.
        private static async Task<JsonDocument?> GetAsync(HttpClient http, SessionRecord session, string path, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(TerraBaseDiscovery.Origin, path));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new AccountAccessException("Platform admin access is required.");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new DatabaseProviderException(response.StatusCode);
            }

            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            }
            catch (JsonException error) { throw new HttpRequestException("TerraBase returned an invalid platform answer.", error); }
        }
    }
}
