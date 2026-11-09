using System.Text.Json;
using TerraScale.Dashboard.E2E.Features.Databases;

namespace TerraScale.Dashboard.E2E.Features.Data;

[ClassDataSource<DashboardRootFixture>]
public sealed class SavedQueryConflictJourneyTests(DashboardRootFixture fixture)
{
    [Test]
    public async Task ConcurrentSavesOfOneNameKeepOneQueryAndAnswerAConflict()
    {
        await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root);
        using var client = scenario.CreateClient();
        var account = await DatabaseJourney.NativeWorkspaceAsync(client);
        var token = await HttpJourney.CreatePatAsync(client, "Saved query conflict", account.Password);
        var saves = await Task.WhenAll(Enumerable.Range(0, 4).Select(index => HttpJourney.McpAsync(client, token, "tools/call",
            new { name = "save_query", arguments = new { name = index % 2 == 0 ? "Daily orders" : "DAILY ORDERS", sql = "SELECT 1" } })));
        var results = saves.Select(save => save.GetProperty("result")).ToArray();
        await Assert.That(results.Count(result => !Failed(result))).IsEqualTo(1);
        await Assert.That(results.Where(Failed).All(result => result.GetProperty("content")[0].GetProperty("text").GetString()!.Contains("already exists", StringComparison.Ordinal)))
            .IsTrue();
        var listed = (await HttpJourney.McpAsync(client, token, "tools/call", new { name = "list_saved_queries", arguments = new { } })).GetProperty("result");
        await Assert.That(listed.GetProperty("structuredContent").GetProperty("queries").GetArrayLength()).IsEqualTo(1);
    }

    private static bool Failed(JsonElement result)
    {
        return result.TryGetProperty("isError", out var error) && error.GetBoolean();
    }
}
