using System.Net;
using System.Text.Json;

namespace TerraScale.Dashboard.E2E.Features.Databases;

[ClassDataSource<DashboardRootFixture>]
public sealed class ReferenceDatabaseJourneyTests(DashboardRootFixture fixture)
{
    [Test]
    public async Task CapturedDatabaseScopesAndMeasuredRowsUseNativeMcpWithoutCreatingSchedulerAuthority()
    {
        await using var scenario = await ReferenceScenario.StartAsync(fixture.Root);
        using var manifest = await ReferenceFixtures.ReadManifestAsync(fixture.Root);
        foreach (var (key, principal) in new[] { ("routes", scenario.Personas.Routes), ("seeded", scenario.Personas.Seeded) })
        {
            using var client = scenario.CreateClient();
            using var replica = scenario.CreateClient(replica: true);
            using var login = await HttpJourney.LoginAsync(client, principal.Email, principal.Password);
            using var secondLogin = await HttpJourney.LoginAsync(replica, principal.Email, principal.Password);
            await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            await Assert.That(secondLogin.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            var secret = await HttpJourney.CreatePatAsync(client, "Captured database observations", principal.Password);
            var captured = manifest.RootElement.GetProperty(key);
            foreach (var inventory in captured.GetProperty("databaseInventories").EnumerateArray())
            {
                var organizationId = inventory.GetProperty("organizationId").GetString()!;
                var projectId = inventory.GetProperty("projectId").GetString()!;
                var actual = (await CallAsync(replica, secret, "list_databases", new { organizationId, projectId })).GetProperty("databases");
                var expected = Rows(inventory.GetProperty("result").GetProperty("databases"));
                await Assert.That(actual.GetArrayLength()).IsEqualTo(expected.Length);
                foreach (var row in expected)
                {
                    var result = actual.EnumerateArray().Single(value => value.GetProperty("id").GetString() == row.GetProperty("id").GetString());
                    foreach (var field in new[] { "id", "name", "status", "region" })
                        await Assert.That(result.GetProperty(field).GetString()).IsEqualTo(row.GetProperty(field).GetString());
                    await Assert.That(result.GetProperty("createdAt").GetDateTimeOffset()).IsEqualTo(row.GetProperty("createdAt").GetDateTimeOffset());
                    await Assert.That(result.GetProperty("placement").EnumerateArray().Select(value => value.GetString()).SequenceEqual(
                        row.GetProperty("placement").EnumerateArray().Select(value => value.GetString()))).IsTrue();
                }
            }
            foreach (var observed in captured.GetProperty("databaseDetails").EnumerateArray())
            {
                var organizationId = observed.GetProperty("organizationId").GetString()!;
                var projectId = observed.GetProperty("projectId").GetString()!;
                var databaseId = observed.GetProperty("databaseId").GetString()!;
                var detail = await CallAsync(replica, secret, "get_database", new { organizationId, projectId, databaseId });
                var expected = observed.GetProperty("detail");
                foreach (var field in new[] { "collections", "documents", "bytes" })
                    await Assert.That(detail.GetProperty(field).GetInt64()).IsEqualTo(expected.GetProperty(field).GetInt64());
                foreach (var field in new[] { "canManage", "canBranch" })
                    await Assert.That(detail.GetProperty(field).GetBoolean()).IsEqualTo(expected.GetProperty(field).GetBoolean());
                await Assert.That(detail.GetProperty("regions").EnumerateArray().Select(value => value.GetString()).SequenceEqual(
                    expected.GetProperty("regions").EnumerateArray().Select(value => value.GetString()))).IsTrue();
                var snapshots = await CallAsync(replica, secret, "list_backups", new { organizationId, projectId, databaseId });
                await Assert.That(snapshots.GetProperty("backups").GetArrayLength()).IsEqualTo(Rows(observed.GetProperty("backups").GetProperty("backups")).Length);
                await Assert.That(snapshots.GetProperty("policy").ValueKind).IsEqualTo(JsonValueKind.Null);
                await Assert.That(detail.TryGetProperty("backupsEnabled", out _)).IsFalse()
                    .Because("The actual source's unsaved retention0 is not an admitted production scheduler contract.");
                var mutation = await HttpJourney.McpAsync(client, secret, "tools/call", new
                { name = "set_backup_policy", arguments = new { organizationId, projectId, databaseId, fullBackupCron = "0 2 * * *", incrementalBackupCron = "", retentionDays = 7 } });
                await Assert.That(mutation.GetProperty("result").GetProperty("isError").GetBoolean()).IsTrue()
                    .Because("Public read-only source observations cannot grant policy mutation authority.");
            }
            if (key == "seeded")
            {
                const string primary = "0Z793APJ2ECFW2F7P6J9F2RKVY";
                using var page = await replica.GetAsync("/databases/" + primary);
                await Assert.That(page.StatusCode).IsEqualTo(HttpStatusCode.OK);
                var html = await page.Content.ReadAsStringAsync();
                await Assert.That(html).Contains("270 B across all collections");
                await Assert.That(html).Contains("Available snapshots");
                using var listing = await replica.GetAsync("/api/v1/dashboard/backups?databaseId=" + primary);
                await Assert.That(listing.StatusCode).IsEqualTo(HttpStatusCode.OK);
                using var body = JsonDocument.Parse(await listing.Content.ReadAsStringAsync());
                await Assert.That(body.RootElement.GetProperty("backups").GetArrayLength()).IsEqualTo(0);
                await Assert.That(body.RootElement.TryGetProperty("policy", out _)).IsFalse();
            }
            else
            {
                using var denied = await replica.GetAsync("/api/v1/dashboard/backups?databaseId=0Z793APJ2ECFW2F7P6J9F2RKVY");
                await Assert.That(denied.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
            }
        }
    }

    private static JsonElement[] Rows(JsonElement value) => value.ValueKind == JsonValueKind.Null ? [] : value.EnumerateArray().ToArray();
    private static async Task<JsonElement> CallAsync(HttpClient client, string secret, string name, object arguments)
    {
        var result = (await HttpJourney.McpAsync(client, secret, "tools/call", new { name, arguments })).GetProperty("result");
        await Assert.That(result.TryGetProperty("isError", out var error) && error.GetBoolean()).IsFalse();
        return result.GetProperty("structuredContent").Clone();
    }
}
