using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraScale.Dashboard.E2E.Features.Databases;
using TerraScale.Dashboard.Features.Usage;

namespace TerraScale.Dashboard.E2E.Features.Usage;

[ClassDataSource<DashboardRootFixture>]
public sealed class UsageStorageFormatJourneyTests(DashboardRootFixture fixture)
{
    [Test]
    public async Task NativeSdkDocumentBytesKeepExactUsageUnitsAcrossBothReplicas()
    {
        await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root, builder =>
        {
            builder.Services.Replace(ServiceDescriptor.Singleton<IUsageMeasurements>(new ReferenceUsageMeasurements()));
            builder.Services.Replace(ServiceDescriptor.Singleton<IUsageReplication>(new ReferenceUsageReplication()));
        });
        using var first = scenario.CreateClient();
        using var replica = scenario.CreateClient(replica: true);
        var account = await DatabaseSdkJourneyTests.NativeWorkspaceAsync(first);
        using var login = await HttpJourney.LoginAsync(replica, account.Email, account.Password);
        await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        using var identity = JsonDocument.Parse(await first.GetStringAsync("/api/v1/auth/me"));
        var organizationId = identity.RootElement.GetProperty("organizationId").GetString()!;
        var projectId = identity.RootElement.GetProperty("projectId").GetString()!;
        var secret = await HttpJourney.CreatePatAsync(first, "Storage unit boundary E2E", account.Password);
        var csrf = await HttpJourney.CsrfAsync(first, "/databases");
        using var created = await HttpJourney.PostFormAsync(first, "/api/v1/dashboard/databases", new()
        { ["_csrf"] = csrf, ["name"] = "storage-boundaries", ["region"] = "GRU" });
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        using var database = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var databaseId = database.RootElement.GetProperty("id").GetString()!;
        using (var collection = await HttpJourney.PostFormAsync(first, "/api/v1/dashboard/data/collections", new()
        { ["_csrf"] = csrf, ["databaseId"] = databaseId, ["name"] = "events" }))
            await Assert.That(collection.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var firstDocumentExists = false;
        var secondDocumentExists = false;
        foreach (var (bytes, label) in new[] { (999, "999 B"), (1000, "1.0 kB"), (999_949, "999.9 kB"), (999_999, "1.0 MB") })
        {
            // Larger totals use two individually valid documents. The size
            // includes their actual compact JSON syntax, not just padding.
            var firstSize = Math.Min(bytes, 1 << 19);
            await WriteAsync("01K78D0C000000000000000007", firstSize, firstDocumentExists);
            firstDocumentExists = true;
            if (bytes > firstSize)
            {
                await WriteAsync("01K78D0C000000000000000008", bytes - firstSize, secondDocumentExists);
                secondDocumentExists = true;
            }
            foreach (var client in new[] { first, replica })
            {
                var response = await HttpJourney.McpAsync(client, secret, "tools/call", new
                { name = "get_usage", arguments = new { organizationId, projectId, databaseId } });
                var result = response.GetProperty("result");
                await Assert.That(result.TryGetProperty("isError", out var error) && error.GetBoolean()).IsFalse().Because(response.ToString());
                var usage = result.GetProperty("structuredContent");
                var totals = usage.GetProperty("totals");
                await Assert.That(totals.GetProperty("logicalBytes").GetInt64()).IsEqualTo(bytes);
                await Assert.That(totals.GetProperty("storageMeasured").GetBoolean()).IsTrue();
                await Assert.That(totals.GetProperty("available").GetBoolean()).IsFalse();
                foreach (var counter in new[] { "reads", "writes" })
                    await Assert.That(totals.GetProperty(counter).ValueKind).IsEqualTo(JsonValueKind.Null);
                var row = usage.GetProperty("databases")[0];
                await Assert.That(row.GetProperty("logicalBytes").GetInt64()).IsEqualTo(bytes);
                await Assert.That(row.GetProperty("documents").GetInt64()).IsEqualTo(secondDocumentExists ? 2L : 1L);
                await Assert.That(row.GetProperty("metered").GetBoolean()).IsFalse();
                await Assert.That(usage.GetProperty("allowances").EnumerateArray().Single(item => item.GetProperty("name").GetString() == "Storage across regions").GetProperty("used").ValueKind).IsEqualTo(JsonValueKind.Null);
                using var native = await client.GetAsync("/usage?project=" + projectId + "&database=" + databaseId);
                await Assert.That(native.StatusCode).IsEqualTo(HttpStatusCode.OK);
                var html = WebUtility.HtmlDecode(await native.Content.ReadAsStringAsync());
                await Assert.That(html).Contains("<dt class=\"ts-stat-label\">Logical storage</dt><dd class=\"ts-stat-value\">" + label + "</dd>");
                await Assert.That(html).Contains("<span class=\"ts-mono\">" + label + "</span>");
                await Assert.That(html.Contains("Storage isn't measured here.", StringComparison.Ordinal)).IsFalse();
            }
        }

        async Task WriteAsync(string id, int bytes, bool exists)
        {
            const string prefix = "{\"value\":\"";
            const string suffix = "\"}";
            var payload = prefix + new string('a', bytes - Encoding.UTF8.GetByteCount(prefix + suffix)) + suffix;
            using var write = await HttpJourney.PostFormAsync(first, "/api/v1/dashboard/data/documents", new()
            { ["_csrf"] = csrf, ["databaseId"] = databaseId, ["collection"] = "events", ["operation"] = exists ? "replace" : "create", ["id"] = id, ["document"] = payload });
            await Assert.That(write.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(await write.Content.ReadAsStringAsync());
        }
    }
}
