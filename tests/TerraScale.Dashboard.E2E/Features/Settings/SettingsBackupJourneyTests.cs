using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TerraScale.Dashboard.E2E.Features.Databases;
using TerraScale.Dashboard.Features.Accounts;
using TerraScale.DotNet;

namespace TerraScale.Dashboard.E2E.Features.Settings
{
    [ClassDataSource<DashboardRootFixture>]
    public sealed class SettingsBackupJourneyTests(DashboardRootFixture fixture)
    {
        [Test]
        public async Task LegacySettingsBackupFormsShareConfirmedSnapshotsFreshAuthorizationAndNewTargetRestore()
        {
            await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root);
            using var client = scenario.CreateClient();
            using var replica = scenario.CreateClient(replica: true);
            using var producer = scenario.CreateProviderClient();
            var account = await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
            using var login = await HttpJourney.LoginAsync(replica, account.Email, account.Password);
            await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            var identity = await ReadAsync(client, "/api/v1/auth/me");
            await ControlAsync(new { command = "plan", organizationId = identity.GetProperty("organizationId").GetString(), value = OrganizationRecord.StarterPlanId });
            var secret = await HttpJourney.CreatePatAsync(client, "Legacy settings backups", account.Password);
            var csrf = await HttpJourney.CsrfAsync(client, "/databases");
            var source = await CallAsync(client, secret, "create_database", new { name = "settings-source", region = "GRU" });
            var databaseId = source.GetProperty("id").GetString()!;
            using (var collection = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/data/collections", new()
            { ["_csrf"] = csrf, ["databaseId"] = databaseId, ["name"] = "users" }))
            {
                await Assert.That(collection.StatusCode).IsEqualTo(HttpStatusCode.Created);
            }

            await CallAsync(client, secret, "create_document", new { databaseId, collection = "users", documentId = "01K78D0C000000000000000015", document = new { score = 1 } });
            await RedirectAsync("backup-now", new() { ["databaseId"] = databaseId });
            var snapshot = (await ReadAsync(replica, "/api/v1/dashboard/backups?databaseId=" + databaseId)).GetProperty("backups")[0];
            var backupId = snapshot.GetProperty("id").GetString()!;
            await CallAsync(replica, secret, "patch_document", new { databaseId, collection = "users", documentId = "01K78D0C000000000000000015", patch = new { score = 2 } });
            await RedirectAsync("backup-policy", new()
            { ["databaseId"] = databaseId, ["fullBackupCron"] = "0 3 * * *", ["incrementalBackupCron"] = "", ["retentionDays"] = "14", ["disabled"] = "on" });
            var policy = (await ReadAsync(replica, "/api/v1/dashboard/backups?databaseId=" + databaseId)).GetProperty("policy");
            await Assert.That(policy.GetProperty("fullBackupCron").GetString()).IsEqualTo("0 3 * * *");
            await Assert.That(policy.GetProperty("retentionDays").GetInt32()).IsEqualTo(14);
            await Assert.That(policy.GetProperty("disabled").GetBoolean()).IsTrue();
            await RedirectAsync("restore", new() { ["databaseId"] = databaseId, ["backupId"] = "" });
            var databases = (await CallAsync(replica, secret, "list_databases", new { })).GetProperty("databases");
            await Assert.That(databases.GetArrayLength()).IsEqualTo(2);
            var restoredId = databases.EnumerateArray().Single(database => database.GetProperty("name").GetString() == "settings-source-restore").GetProperty("id").GetString()!;
            await Assert.That(restoredId == databaseId).IsFalse();
            await Assert.That(await ScoreAsync(restoredId)).IsEqualTo(1);
            await Assert.That(await ScoreAsync(databaseId)).IsEqualTo(2)
                .Because("The legacy restore alias copies the completed producer snapshot into a new database without changing its source.");
            var observed = await ReadAsync(producer, "/__test/observations");
            var restores = observed.GetProperty("backupRestores").GetInt32();
            var creates = observed.GetProperty("backupCreates").GetInt32();
            var writes = observed.GetProperty("policyWrites").GetInt32();
            using (var forged = await PostAsync("restore", new()
            { ["databaseId"] = databaseId, ["backupId"] = new TerraScaleIdGenerator().NewId() }))
            {
                await Assert.That(forged.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
            }

            using (var legacyId = await PostAsync("restore", new() { ["databaseId"] = databaseId, ["backupId"] = "demo-backup" }))
            {
                await Assert.That(legacyId.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            }

            using (var overwrite = await PostAsync("restore", new()
            { ["databaseId"] = databaseId, ["backupId"] = backupId, ["replace"] = "true" }))
            {
                await Assert.That(overwrite.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
            }

            await ControlAsync(new { command = "role", value = "member" });
            var deniedToken = await HttpJourney.CsrfAsync(replica, "/profile");
            foreach (var (operation, fields) in new[]
            {
                ("backup-now", new Dictionary<string, string> { ["databaseId"] = databaseId }),
                ("backup-policy", new Dictionary<string, string> { ["databaseId"] = databaseId, ["fullBackupCron"] = "0 4 * * *", ["retentionDays"] = "7" }),
                ("restore", new Dictionary<string, string> { ["databaseId"] = databaseId, ["backupId"] = backupId })
            })
            {
                fields["_csrf"] = deniedToken;
                using var denied = await HttpJourney.PostFormAsync(replica, "/api/v1/dashboard/settings/" + operation, fields);
                await Assert.That(denied.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
            }
            observed = await ReadAsync(producer, "/__test/observations");
            await Assert.That(observed.GetProperty("backupRestores").GetInt32()).IsEqualTo(restores);
            await Assert.That(observed.GetProperty("backupCreates").GetInt32()).IsEqualTo(creates);
            await Assert.That(observed.GetProperty("policyWrites").GetInt32()).IsEqualTo(writes);
            await ControlAsync(new { command = "role", value = "owner" });
            await Assert.That(await ScoreAsync(databaseId)).IsEqualTo(2);
            await CallAsync(client, secret, "delete_database", new { databaseId = restoredId, confirmName = "settings-source-restore" });
            await CallAsync(replica, secret, "delete_database", new { databaseId, confirmName = "settings-source" });
            await Assert.That((await ReadAsync(producer, "/__test/observations")).GetProperty("databases").GetArrayLength()).IsEqualTo(0);

            async Task<HttpResponseMessage> PostAsync(string operation, Dictionary<string, string> fields)
            { fields["_csrf"] = csrf; return await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/settings/" + operation, fields); }
            async Task RedirectAsync(string operation, Dictionary<string, string> fields)
            {
                using var response = await PostAsync(operation, fields);
                await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
                await Assert.That(response.Headers.Location?.OriginalString).IsEqualTo("/");
            }
            async Task ControlAsync(object body)
            { using var response = await producer.PostAsJsonAsync("/__test/control", body); await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NoContent); }
            async Task<int> ScoreAsync(string id) => (await CallAsync(replica, secret, "get_document", new
            { databaseId = id, collection = "users", documentId = "01K78D0C000000000000000015" })).GetProperty("value").GetProperty("score").GetInt32();
        }

        private static async Task<JsonElement> ReadAsync(HttpClient client, string path)
        {
            using var response = await client.GetAsync(path);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
            using var value = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return value.RootElement.Clone();
        }
        private static async Task<JsonElement> CallAsync(HttpClient client, string secret, string name, object arguments)
        {
            var result = (await HttpJourney.McpAsync(client, secret, "tools/call", new { name, arguments })).GetProperty("result");
            await Assert.That(result.TryGetProperty("isError", out var error) && error.GetBoolean()).IsFalse();
            return result.GetProperty("structuredContent").Clone();
        }
    }
}
