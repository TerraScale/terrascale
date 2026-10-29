using System.Globalization;
using System.Net;
using System.Text.Json;
using TerraBase.Testing;

namespace TerraBase.Fast.Documents;

public sealed class AggregateGeoQueryTests
{
    private static readonly string[] Status = ["/status"];
    private static readonly string[] Amount = ["/amount"];
    private static readonly string[] AmountAndMissing = ["/amount", "/missing"];
    private static readonly string[] Ops = ["sum", "avg", "min", "max"];
    private static readonly string[] LatLon = ["loc.lat", "loc.lon"];

    [Test]
    public async Task Aggregate_groups_matches_and_summarizes_numbers_in_key_order()
    {
        await using var api = await DocumentApi.StartAsync();
        foreach (var (status, amount) in new (string?, object)[] { ("paid", 10), ("paid", 2.5), ("open", 4), ("open", "n/a"), (null, 1), ("void", -3) })
        {
            (await api.PutAsync(DocumentApi.NewId(), status is null ? new { amount } : new { status, amount })).Status.ShouldBe(HttpStatusCode.Created);
        }

        var grouped = await api.QueryAsync(new
        {
            filter = new { ne = new object[] { "/status", "void" } },
            aggregate = new { group_by = Status, sum = Amount, avg = Amount, min = Amount, max = Amount },
        });
        var total = await api.QueryAsync(new { aggregate = new { sum = AmountAndMissing } });

        await Assert.That(grouped.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(grouped.Json.GetProperty("items").GetArrayLength()).IsEqualTo(0);
        await Assert.That(Groups(grouped)).IsEqualTo("null 1 sum=1 avg=1 min=1 max=1; \"open\" 2 sum=4 avg=4 min=4 max=4; \"paid\" 2 sum=12.5 avg=6.25 min=2.5 max=10");
        await Assert.That(Groups(total)).IsEqualTo(" 6 sum=14.5 sum=0");
    }

    [Test]
    public async Task Within_filters_by_radius_and_box_and_near_orders_by_meters()
    {
        await using var api = await DocumentApi.StartAsync();
        using (var owner = api.Management.As(api.Tenant.User))
        {
            using var index = await owner.PostAsJsonAsync(ManagementApi.Uri(
                $"/v1/management/organizations/{api.Tenant.OrganizationId}/projects/{api.Tenant.ProjectId}/databases/{(await api.TenantOfAsync()).DatabaseId}/indexes"),
                new { name = "places_lat", collection = "items", kind = "btree", fields = LatLon });
            index.EnsureSuccessStatusCode();
        }

        // About 111 m per 0.001 degree of latitude; 0.009 degrees is about 1 km.
        var ids = new Dictionary<string, string>();
        foreach (var (name, loc) in new (string, object)[]
        {
            ("center", new { lat = -23.55, lon = -46.63 }),
            ("north500", new { lat = -23.5455, lon = -46.63 }),
            ("east800", new { lat = -23.55, lon = -46.6222 }),
            ("north1200", new { lat = -23.5392, lon = -46.63 }),
            ("corner", new { lat = -23.5428, lon = -46.6228 }),
            ("fiji", new { lat = -17.7, lon = 179.9 }),
            ("samoa", new { lat = -13.8, lon = -171.8 }),
            ("text", new { lat = "-23.55", lon = -46.63 }),
        })
        {
            ids[name] = DocumentApi.NewId();
            (await api.PutAsync(ids[name], new { loc })).Status.ShouldBe(HttpStatusCode.Created);
        }

        var circle = new { within = new object[] { "/loc", new { lat = -23.55, lon = -46.63, meters = 1000 } } };
        var near = await api.QueryAsync(new { filter = circle, sort = new[] { new { near = new { path = "/loc", lat = -23.55, lon = -46.63 } } } });
        var box = await api.QueryAsync(new { filter = new { within = new object[] { "/loc", new { south = -20, west = 179, north = -10, east = -170 } } } });
        var count = await api.QueryAsync(new { filter = new { not = circle }, count = true, limit = 1 });
        var invalid = await api.QueryAsync(new { filter = new { within = new object[] { "/loc", new { lat = 91, lon = 0, meters = 1 } } } });

        await Assert.That(near.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(string.Join(", ", near.Json.GetProperty("items").EnumerateArray().Select(item =>
            $"{ids.Single(pair => pair.Value == item.GetProperty("id").GetString()).Key} {Math.Round(item.GetProperty("distance").GetDouble() / 100).ToString(CultureInfo.InvariantCulture)}")))
            .IsEqualTo("center 0, north500 5, east800 8");
        await Assert.That(box.Json.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetString()!).Order())
            .IsEquivalentTo(new[] { ids["fiji"], ids["samoa"] }.Order());
        await Assert.That(count.Json.GetProperty("count").GetProperty("value").GetString()).IsEqualTo("5");
        await Assert.That((invalid.Status, invalid.Message)).IsEqualTo((HttpStatusCode.BadRequest, "lat is -90 to 90 and lon is -180 to 180"));
    }

    private static string Groups(ApiResponse response) => string.Join("; ", response.Json.GetProperty("groups").EnumerateArray().Select(group =>
        string.Join(',', group.GetProperty("key").EnumerateArray().Select(key => key.GetRawText())) + " " + group.GetProperty("count").GetString()
        + string.Concat(Ops.Where(op => group.TryGetProperty(op, out _)).SelectMany(op => group.GetProperty(op).EnumerateObject()
            .Select(value => $" {op}={(value.Value.ValueKind == JsonValueKind.Null ? "null" : value.Value.GetDouble().ToString(CultureInfo.InvariantCulture))}")))));
}
