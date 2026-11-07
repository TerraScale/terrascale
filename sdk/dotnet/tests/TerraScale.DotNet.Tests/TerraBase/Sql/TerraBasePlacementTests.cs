using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.DotNet.Tests.TerraBase.Sql;

public sealed class TerraBasePlacementTests
{
    private const string Project = "01JA0000000000000000000001";
    private const string Database = "01JA0000000000000000000004";

    [Test]
    public async Task Placement_operations_match_recorded_contracts()
    {
        var fixtures = new Queue<string>(["create-with-replicas", "create-all-regions", "set-replicas", "set-all-regions", "move-home"]);
        using var handler = new TerraBaseTestHandler((request, token) => ReplayAsync(fixtures.Dequeue(), request, token));
        using var http = new HttpClient(handler);
        var client = Client(http);

        var replicas = await client.CreateDatabaseAsync(Project, "orders", homeRegion: "gru", replicaRegions: ["sjc"]);
        var everywhere = await client.CreateDatabaseAsync(Project, "orders", homeRegion: "gru", allRegions: true);
        var set = await client.SetDatabasePlacementAsync(Project, Database, replicaRegions: ["fra", "sjc"]);
        var all = await client.SetDatabasePlacementAsync(Project, Database, allRegions: true);
        var moved = await client.MoveDatabaseHomeAsync(Project, Database, "sjc");

        await Assert.That((replicas.HomeRegion, string.Join(',', replicas.ReplicaRegions), replicas.AllRegions)).IsEqualTo(("gru", "sjc", false));
        await Assert.That((string.Join(',', everywhere.ReplicaRegions), everywhere.AllRegions)).IsEqualTo(("fra,sjc", true));
        await Assert.That((string.Join(',', set.ReplicaRegions), set.AllRegions, all.AllRegions)).IsEqualTo(("fra,sjc", false, true));
        await Assert.That((moved.HomeRegion, string.Join(',', moved.ReplicaRegions))).IsEqualTo(("sjc", "fra,gru"));
        await Assert.That(fixtures.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Placement_refuses_ambiguous_requests_and_resends_a_move_only_while_unresolved()
    {
        var calls = 0;
        using var handler = new TerraBaseTestHandler(async (request, token) =>
        {
            calls++;
            if (calls > 1) return await ReplayAsync("move-home-refused", request, token);
            var unresolved = TerraBaseIntegrationFixture.Json("""{"error":{"code":"move_unresolved","message":"settling"}}""", HttpStatusCode.ServiceUnavailable);
            unresolved.Headers.RetryAfter = new(TimeSpan.Zero);
            return unresolved;
        });
        using var http = new HttpClient(handler);
        var client = Client(http);

        await Assert.ThrowsAsync<ArgumentException>(() => client.SetDatabasePlacementAsync(Project, Database, ["sjc"], true));
        await Assert.ThrowsAsync<ArgumentException>(() => client.SetDatabasePlacementAsync(Project, Database));
        await Assert.That(calls).IsEqualTo(0);

        var error = await Assert.ThrowsAsync<TerraBaseException>(() => client.MoveDatabaseHomeAsync(Project, Database, "fra"));

        await Assert.That((error!.StatusCode, error.ErrorCode)).IsEqualTo((HttpStatusCode.BadRequest, "invalid_request"));
        await Assert.That(calls).IsEqualTo(2);
    }

    private static TerraBaseSqlClient Client(HttpClient http) => new(http, new TerraBaseSqlClientOptions
    {
        Region = "gru",
        AccessTokenProvider = _ => ValueTask.FromResult("placement-token"),
    });

    private static async Task<HttpResponseMessage> ReplayAsync(string fixture, HttpRequestMessage request, CancellationToken token)
    {
        using var json = JsonDocument.Parse(TerraBaseIntegrationFixture.ReadContract("placement/" + fixture + ".json"));
        var expected = json.RootElement.GetProperty("request");
        await Assert.That(request.Method.Method).IsEqualTo(expected.GetProperty("method").GetString());
        await Assert.That(request.RequestUri!.AbsolutePath).IsEqualTo(expected.GetProperty("path").GetString());
        await Assert.That(request.Content!.Headers.ContentType!.MediaType).IsEqualTo("application/json");
        await Assert.That(JsonNode.DeepEquals(JsonNode.Parse(await request.Content.ReadAsStringAsync(token)), JsonNode.Parse(expected.GetProperty("body").GetRawText()))).IsTrue();
        await Assert.That(request.Headers.Authorization!.Parameter).IsEqualTo("placement-token");
        var response = json.RootElement.GetProperty("response");
        return TerraBaseIntegrationFixture.Json(response.GetProperty("body").GetRawText(), (HttpStatusCode)response.GetProperty("status").GetInt32());
    }
}
