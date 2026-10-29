using System.Net;
using System.Text.Json;
using TerraScale.Dashboard.E2E.Features.Databases;

namespace TerraScale.Dashboard.E2E.Features.Organizations
{
    [ClassDataSource<DashboardRootFixture>]
    public sealed class AuditEventsJourneyTests(DashboardRootFixture fixture)
    {
        // Five audited dashboard actions are listed by the API and every export with their actor, client IP and time.
        [Test]
        public async Task DashboardActionsAreListedWithActorIpAndThePlanWindow()
        {
            await using var host = await DatabaseSdkScenario.StartAsync(fixture.Root);
            using var client = host.CreateClient();
            using var replica = host.CreateClient(replica: true);
            var account = await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
            // The loopback edge forwards the browser's address; the forwarded-headers middleware trusts only loopback peers.
            replica.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.9");
            using (var login = await HttpJourney.LoginAsync(replica, account.Email, account.Password))
            {
                await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            }

            var csrf = await HttpJourney.CsrfAsync(replica, "/databases");
            using var created = await HttpJourney.PostFormAsync(replica, "/api/v1/dashboard/databases", new() { ["_csrf"] = csrf, ["name"] = "audited", ["region"] = "GRU" });
            await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
            var databaseId = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;
            using var key = await HttpJourney.PostConfirmedFormAsync(replica, "/api/v1/dashboard/credentials/key", new()
            { ["_csrf"] = csrf, ["name"] = "Audited key", ["databaseId"] = databaseId, ["scopes"] = "documents:read" });
            await Assert.That(key.StatusCode).IsEqualTo(HttpStatusCode.Created);
            var keyId = JsonDocument.Parse(await key.Content.ReadAsStringAsync()).RootElement.GetProperty("apiKey").GetProperty("keyId").GetString()!;
            using (var revoked = await HttpJourney.PostFormAsync(replica, "/api/v1/dashboard/credentials/" + keyId + "/revoke", new() { ["_csrf"] = csrf }))
            {
                await Assert.That(revoked.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
            }

            using (var deleted = await HttpJourney.PostFormAsync(replica, "/api/v1/dashboard/databases/delete", new() { ["_csrf"] = csrf, ["databaseId"] = databaseId, ["confirmName"] = "audited" }))
            {
                await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
            }

            var actions = new[] { "session.created", "database.created", "api_key.created", "api_key.revoked", "database.deleted" };
            using var listed = JsonDocument.Parse(await client.GetStringAsync("/api/v1/dashboard/audit-events?range=24h&pageSize=100"));
            await Assert.That(listed.RootElement.GetProperty("retentionDays").GetInt32()).IsEqualTo(7).Because("the Free plan keeps 7 days");
            var events = listed.RootElement.GetProperty("events").EnumerateArray().ToArray();
            foreach (var action in actions)
            {
                await Assert.That(events.Any(item => item.GetProperty("action").GetString() == action && item.GetProperty("ip").GetString() == "203.0.113.9" &&
                    item.GetProperty("actor").GetString() == account.Email && item.GetProperty("occurredAt").GetDateTimeOffset() > DateTimeOffset.UtcNow.AddHours(-1))).IsTrue().Because(action);
            }

            await Assert.That(events.Count(item => item.GetProperty("action").GetString() == "api_key.created")).IsEqualTo(1).Because("ts-auth's copy of the same change is not listed twice");

            var csv = (await client.GetStringAsync("/audit/export?format=csv&range=24h")).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            await Assert.That(csv[0]).IsEqualTo("auditEventId,occurredAt,actor,actorType,action,target,source,severity,outcome,ip");
            foreach (var action in actions)
            {
                await Assert.That(csv.Any(line => line.Contains("\"" + action + "\"", StringComparison.Ordinal) && line.EndsWith(",\"203.0.113.9\"", StringComparison.Ordinal))).IsTrue().Because(action);
            }

            using var ndjson = await client.GetAsync("/audit/export?format=ndjson&range=24h");
            await Assert.That(ndjson.Content.Headers.ContentType?.MediaType).IsEqualTo("application/x-ndjson");
            var lines = (await ndjson.Content.ReadAsStringAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonDocument.Parse(line).RootElement).ToArray();
            await Assert.That(lines.Length).IsEqualTo(listed.RootElement.GetProperty("total").GetInt32());
            await Assert.That(actions.All(action => lines.Any(line => line.GetProperty("action").GetString() == action && line.GetProperty("ip").GetString() == "203.0.113.9"))).IsTrue();
            var page = WebUtility.HtmlDecode(await client.GetStringAsync("/organization?tab=audit&range=24h"));
            await Assert.That(page).Contains("Events older than 7 days are not shown or exported on your plan.");
            await Assert.That(page).DoesNotContain("value=\"30d\"").Because("the range choices stop at the plan window");
        }
    }
}
