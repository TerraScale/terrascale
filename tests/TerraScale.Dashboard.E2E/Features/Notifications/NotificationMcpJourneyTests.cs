using System.Net;
using System.Text.Json;
using TerraScale.Dashboard.Platform.Persistence;

namespace TerraScale.Dashboard.E2E.Features.Notifications
{
    [ClassDataSource<DashboardE2EFixture>]
    public sealed class NotificationMcpJourneyTests(DashboardE2EFixture fixture)
    {
        [Test]
        public async Task NotificationReadTransitionsAreIdempotentAndAgreeAcrossMcpAndRest()
        {
            using var client = fixture.CreateClient();
            using var replica = fixture.CreateClient(replica: true);
            using var login = await HttpJourney.LoginAsync(client);
            await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            using var replicaLogin = await HttpJourney.LoginAsync(replica);
            await Assert.That(replicaLogin.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            var secret = await HttpJourney.CreatePatAsync(client, "TUnit notifications MCP");
            using var selected = await client.GetAsync("/api/v1/auth/me");
            await Assert.That(selected.StatusCode).IsEqualTo(HttpStatusCode.OK);
            using var me = JsonDocument.Parse(await selected.Content.ReadAsStringAsync());
            var organizationId = me.RootElement.GetProperty("organizationId").GetString()!;
            var projectId = me.RootElement.GetProperty("projectId").GetString()!;
            var seed = await SeedAsync(organizationId, projectId);
            try
            {
                var listed = await ToolAsync(client, "list_notifications", organizationId, projectId);
                await Assert.That(listed.GetProperty("unread").GetInt32()).IsEqualTo(2);
                await Assert.That(listed.GetProperty("critical").GetInt32()).IsEqualTo(1);
                await Assert.That(listed.GetProperty("total").GetInt32()).IsEqualTo(2);
                await Assert.That(listed.GetProperty("notifications").EnumerateArray()
                    .Select(item => item.GetProperty("id").GetString()!).ToArray()).IsEquivalentTo(seed);
                await Assert.That(listed.GetProperty("notifications").EnumerateArray()
                    .Single(item => item.GetProperty("severity").GetString() == "danger").GetProperty("status").GetString()).IsEqualTo("Unread");

                var marked = await ToolAsync(client, "mark_notification_read", organizationId, projectId, ("notificationId", seed[0]));
                await Assert.That(marked.GetProperty("marked").GetInt32()).IsEqualTo(1);
                var repeated = await ToolAsync(client, "mark_notification_read", organizationId, projectId, ("notificationId", seed[0]));
                await Assert.That(repeated.GetProperty("marked").GetInt32()).IsEqualTo(0);
                var between = await ToolAsync(replica, "list_notifications", organizationId, projectId);
                await Assert.That(between.GetProperty("unread").GetInt32()).IsEqualTo(1);
                await Assert.That(between.GetProperty("critical").GetInt32()).IsEqualTo(1);
                await Assert.That(between.GetProperty("notifications").EnumerateArray()
                    .Single(item => item.GetProperty("id").GetString() == seed[0]).GetProperty("status").GetString()).IsEqualTo("Read");

                var csrf = await HttpJourney.CsrfAsync(client, "/notifications");
                using (var restSingle = await HttpJourney.PostFormAsync(client, "/api/v1/notifications/" + seed[1] + "/read", new() { ["_csrf"] = csrf }))
                {
                    await Assert.That(restSingle.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
                }

                using (var page = await replica.GetAsync("/notifications"))
                {
                    await Assert.That(page.StatusCode).IsEqualTo(HttpStatusCode.OK);
                    var html = WebUtility.HtmlDecode(await page.Content.ReadAsStringAsync());
                    await Assert.That(html).Contains("0 unread");
                    await Assert.That(html).Contains("2 total");
                }

                using (var restAll = await HttpJourney.PostFormAsync(client, "/api/v1/notifications/read-all", new() { ["_csrf"] = csrf }))
                {
                    await Assert.That(restAll.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
                }

                var afterAll = await ToolAsync(replica, "list_notifications", organizationId, projectId);
                await Assert.That(afterAll.GetProperty("unread").GetInt32()).IsEqualTo(0);
                await Assert.That(afterAll.GetProperty("critical").GetInt32()).IsEqualTo(0);
                await Assert.That(afterAll.GetProperty("notifications").EnumerateArray()
                    .All(item => item.GetProperty("status").GetString() == "Read")).IsTrue();

                var unknown = await HttpJourney.McpAsync(client, secret, "tools/call", new
                { name = "mark_notification_read", arguments = new { notificationId = "01JZZZZZZZZZZZZZZZZZZZZZZZ", organizationId, projectId } });
                await Assert.That(unknown.GetProperty("result").GetProperty("isError").GetBoolean()).IsTrue();
                await Assert.That(unknown.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()).IsEqualTo("Notification is unavailable.");
                using (var restUnknown = await HttpJourney.PostFormAsync(client, "/api/v1/notifications/01JZZZZZZZZZZZZZZZZZZZZZZZ/read", new() { ["_csrf"] = csrf }))
                {
                    await Assert.That(restUnknown.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
                    await Assert.That(await restUnknown.Content.ReadAsStringAsync()).Contains("Notification is unavailable.");
                }
            }
            finally
            {
                await ClearAsync(seed);
            }

            async Task<JsonElement> ToolAsync(HttpClient caller, string name, string org, string project, (string Key, string Value)? argument = null)
            {
                var arguments = new Dictionary<string, object?> { ["organizationId"] = org, ["projectId"] = project };
                if (argument is { } pair)
                {
                    arguments[pair.Key] = pair.Value;
                }

                var rpc = await HttpJourney.McpAsync(caller, secret, "tools/call", new { name, arguments });
                var result = rpc.GetProperty("result");
                await Assert.That(result.TryGetProperty("isError", out var error) && error.GetBoolean()).IsFalse().Because(rpc.ToString());
                return result.GetProperty("structuredContent");
            }
        }

        private async Task<string[]> SeedAsync(string organizationId, string projectId)
        {
            var accounts = fixture.Service<AccountStore>();
            for (var attempt = 0; ; attempt++)
            {
                var row = await accounts.FindAsync("test-user", CancellationToken.None)
                    ?? throw new InvalidOperationException("The seeded test account is unavailable.");
                var account = row.Account;
                string[] ids = [AccountService.NewId(), AccountService.NewId()];
                account.Notifications.AddRange([
                    new NotificationRecord
                    {
                        Id = ids[0], OrganizationId = organizationId, ProjectId = projectId, Type = "backup",
                        Title = "Notification journey seed", Body = "First unread notification.", Severity = "info",
                        Status = "unread", CreatedAt = DateTimeOffset.UtcNow
                    },
                    new NotificationRecord
                    {
                        Id = ids[1], OrganizationId = organizationId, ProjectId = projectId, Type = "backup",
                        Title = "Notification journey seed danger", Body = "Second unread notification.", Severity = "danger",
                        Status = "unread", CreatedAt = DateTimeOffset.UtcNow
                    }]);
                try
                {
                    await accounts.ReplaceAsync(row, CancellationToken.None);
                    return ids;
                }
                catch (StateConflictException) when (attempt < 7) { }
            }
        }

        private async Task ClearAsync(string[] seed)
        {
            try
            {
                var accounts = fixture.Service<AccountStore>();
                for (var attempt = 0; attempt < 8; attempt++)
                {
                    if (await accounts.FindAsync("test-user", CancellationToken.None) is not { } row)
                    {
                        return;
                    }

                    var account = row.Account;
                    account.Notifications.RemoveAll(item => seed.Contains(item.Id));
                    try
                    {
                        await accounts.ReplaceAsync(row, CancellationToken.None);
                        return;
                    }
                    catch (StateConflictException) { }
                }
            }
            catch (Exception error) { Console.Error.WriteLine("E2E notification seed cleanup: " + error.Message); }
        }
    }
}
