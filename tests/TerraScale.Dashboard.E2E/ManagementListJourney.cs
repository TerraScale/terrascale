using System.Net;
using System.Text.Json;

namespace TerraScale.Dashboard.E2E
{
    public static class ManagementListJourney
    {
        public static async Task<JsonElement> ReadListAllAsync(HttpClient client, string path, int limit = 100)
        {
            var identity = path.Split('?')[0] switch
            {
                "/api/v1/dashboard/databases" or "/api/v1/dashboard/data/saved-queries" => "id",
                "/api/v1/dashboard/credentials" => "keyId",
                _ => throw new ArgumentException("Expected a management list path.", nameof(path))
            };
            var rows = new List<JsonElement>();
            var cursors = new HashSet<string>(StringComparer.Ordinal);
            string? after = null, previous = null;
            do
            {
                using var response = await client.GetAsync(path + (path.Contains('?') ? "&" : "?") + "limit=" + limit +
                    (after is null ? "" : "&after=" + Uri.EscapeDataString(after)));
                await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                var page = json.RootElement;
                await Assert.That(page.GetProperty("limit").GetInt32()).IsEqualTo(limit);
                var items = page.GetProperty("items");
                await Assert.That(items.GetArrayLength()).IsLessThanOrEqualTo(limit);
                foreach (var item in items.EnumerateArray())
                {
                    var id = item.GetProperty(identity).GetString()!;
                    await Assert.That(!string.IsNullOrEmpty(id) && (previous is null || StringComparer.Ordinal.Compare(previous, id) < 0)).IsTrue();
                    previous = id;
                    rows.Add(item.Clone());
                }
                after = page.GetProperty("nextCursor").GetString();
                await Assert.That(page.GetProperty("hasMore").GetBoolean()).IsEqualTo(after is not null);
                if (after is not null)
                {
                    await Assert.That(after.Length > 0 && cursors.Add(after)).IsTrue();
                }
            } while (after is not null);
            return JsonSerializer.SerializeToElement(rows);
        }
    }
}
