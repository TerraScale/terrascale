using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TerraScale.Dashboard.E2E.Features.Notifications;

[ClassDataSource<DashboardRootFixture>]
public sealed class AllowanceNotificationJourneyTests(DashboardRootFixture fixture)
{
    [Test]
    public async Task EightyOnePercentCreatesOneDurableAlertAndOwnerInboxAcrossRestarts()
    {
        await using var scenario = new AllowanceNotificationScenario(fixture.Root);
        await scenario.StartAsync();
        await CycleAsync(scenario, new() { ["operations"] = "81" });
        var first = await SnapshotAsync(scenario);
        var alertId = first.GetProperty("alerts")[0].GetProperty("id").GetString();
        await AssertInboxAsync(scenario, 1, "Operations allowance: 80% reached");
        await AssertInboxAsync(scenario, 1, "Operations allowance: 80% reached");
        await scenario.RestartAsync();
        await CycleAsync(scenario, new() { ["operations"] = "81" });
        var restarted = await SnapshotAsync(scenario);
        await Assert.That(restarted.GetProperty("attempts").GetInt32()).IsEqualTo(1);
        await Assert.That(restarted.GetProperty("sent").GetInt32()).IsEqualTo(1);
        await Assert.That(restarted.GetProperty("alerts").GetArrayLength()).IsEqualTo(1);
        await Assert.That(restarted.GetProperty("alerts")[0].GetProperty("id").GetString()).IsEqualTo(alertId);
        await Assert.That(restarted.GetProperty("inbox").GetArrayLength()).IsEqualTo(1);
        await Assert.That(restarted.GetProperty("inbox")[0].GetProperty("type").GetString()).IsEqualTo("allowance-operations");
        await AssertInboxAsync(scenario, 1, "Operations allowance: 80% reached");
        await RoleAsync(scenario, "member");
        await AssertInboxAsync(scenario, 0, "No notifications in this scope.");
        await Assert.That((await SnapshotAsync(scenario)).GetProperty("inbox").GetArrayLength()).IsEqualTo(1);
    }

    [Test]
    public async Task OperationThresholdsIgnoreStorageAndResetNextUtcMonth()
    {
        await using var scenario = new AllowanceNotificationScenario(fixture.Root);
        await scenario.StartAsync();
        await CycleAsync(scenario, new() { ["operations"] = "79", ["storage"] = "100" });
        await Assert.That((await SnapshotAsync(scenario)).GetProperty("sent").GetInt32()).IsEqualTo(0);
        await AssertInboxAsync(scenario, 0, "No notifications in this scope.");
        await CycleAsync(scenario, new() { ["operations"] = "80", ["storage"] = "100" });
        await Assert.That((await SnapshotAsync(scenario)).GetProperty("sent").GetInt32()).IsEqualTo(1);
        await AssertInboxAsync(scenario, 1, "Operations allowance: 80% reached");
        await CycleAsync(scenario, new() { ["operations"] = "100", ["storage"] = "100" });
        await CycleAsync(scenario, new() { ["operations"] = "100", ["storage"] = "100" });
        var initial = await SnapshotAsync(scenario);
        await Assert.That(initial.GetProperty("sent").GetInt32()).IsEqualTo(2);
        var oldIds = initial.GetProperty("alerts").EnumerateArray().Select(alert => alert.GetProperty("id").GetString()).ToArray();
        var oldMonth = initial.GetProperty("alerts")[0].GetProperty("billingMonth").GetString();
        await AssertInboxAsync(scenario, 2, "Operations allowance: 100% reached");
        var inbox = await scenario.Client.GetStringAsync("/notifications");
        foreach (var threshold in new[] { 80, 100 })
            await Assert.That(inbox).Contains($"Operations allowance: {threshold}% reached");
        await Assert.That(inbox.Contains("Storage allowance:", StringComparison.Ordinal)).IsFalse();
        await Assert.That(initial.GetProperty("alerts").EnumerateArray().All(alert => alert.GetProperty("metric").GetString() == "operations")).IsTrue();
        await CycleAsync(scenario, new() { ["operations"] = "100", ["storage"] = "100", ["nextMonth"] = "true" });
        var next = await SnapshotAsync(scenario);
        await Assert.That(next.GetProperty("attempts").GetInt32()).IsEqualTo(4);
        await Assert.That(next.GetProperty("sent").GetInt32()).IsEqualTo(4);
        var alerts = next.GetProperty("alerts").EnumerateArray().ToArray();
        await Assert.That(alerts.Length).IsEqualTo(2);
        await Assert.That(alerts.All(alert => alert.GetProperty("billingMonth").GetString() != oldMonth &&
            !oldIds.Contains(alert.GetProperty("id").GetString()))).IsTrue();
        await Assert.That(alerts.Select(alert => (alert.GetProperty("metric").GetString(), alert.GetProperty("threshold").GetInt32())).Distinct().Count()).IsEqualTo(2);
        await AssertInboxAsync(scenario, 4, "Operations allowance: 100% reached");
        await HttpJourney.CsrfAsync(scenario.Client);
    }

    [Test]
    [Arguments("unknown", 2, 0)]
    [Arguments("accepted", 2, 1)]
    public async Task KnownRefusalRetriesButUnknownDeliveryNeverResends(string recovery, int attempts, int sent)
    {
        await using var scenario = new AllowanceNotificationScenario(fixture.Root);
        await scenario.StartAsync();
        await CycleAsync(scenario, new() { ["operations"] = "81", ["outcome"] = "retryable" });
        var refused = await SnapshotAsync(scenario);
        await Assert.That(refused.GetProperty("attempts").GetInt32()).IsEqualTo(1);
        await Assert.That(refused.GetProperty("alerts")[0].GetProperty("emailStatus").GetString()).IsEqualTo("pending");
        await CycleAsync(scenario, new() { ["operations"] = "81", ["outcome"] = recovery });
        await scenario.RestartAsync();
        await CycleAsync(scenario, new() { ["operations"] = "81" });
        var recovered = await SnapshotAsync(scenario);
        await Assert.That(recovered.GetProperty("attempts").GetInt32()).IsEqualTo(attempts);
        await Assert.That(recovered.GetProperty("sent").GetInt32()).IsEqualTo(sent);
        await Assert.That(recovered.GetProperty("alerts")[0].GetProperty("emailStatus").GetString()).IsEqualTo(recovery);
        await AssertInboxAsync(scenario, 1, "Operations allowance: 80% reached");
    }

    [Test]
    public async Task UnavailableMeasurementsAndNonOwnersNeverInventAlertsOrInboxItems()
    {
        await using var scenario = new AllowanceNotificationScenario(fixture.Root);
        await scenario.StartAsync();
        await CycleAsync(scenario, new());
        var unavailable = await SnapshotAsync(scenario);
        await Assert.That(unavailable.GetProperty("sent").GetInt32()).IsEqualTo(0);
        await Assert.That(unavailable.GetProperty("alerts").GetArrayLength()).IsEqualTo(0);
        await AssertInboxAsync(scenario, 0, "No notifications in this scope.");
        await RoleAsync(scenario, "admin");
        await CycleAsync(scenario, new() { ["operations"] = "100" });
        await AssertInboxAsync(scenario, 0, "No notifications in this scope.");
        var nonowner = await SnapshotAsync(scenario);
        await Assert.That(nonowner.GetProperty("sent").GetInt32()).IsEqualTo(2);
        await Assert.That(nonowner.GetProperty("inbox").GetArrayLength()).IsEqualTo(0);
        await RoleAsync(scenario, "owner");
        await AssertInboxAsync(scenario, 2, "Operations allowance: 100% reached");
        await Assert.That((await SnapshotAsync(scenario)).GetProperty("inbox").GetArrayLength()).IsEqualTo(2);
        await RoleAsync(scenario, "admin");
        await AssertInboxAsync(scenario, 0, "No notifications in this scope.");
    }

    [Test]
    [Arguments("document-operations-node-v1")]
    [Arguments("operation-units-4kib-node-v1")]
    [Arguments("operation-units-4kib-selected-regions-global-v1")]
    public async Task UnsupportedOperationsAndNodeLocalStorageFailClosedBehindTheHostedWorker(string unsupportedOperations)
    {
        await using var scenario = new AllowanceNotificationScenario(fixture.Root);
        await scenario.StartAsync();
        await CycleAsync(scenario, new() { ["operations"] = "100", ["storage"] = "100", ["unsupported"] = "true", ["operationsContract"] = unsupportedOperations });
        var invalid = await SnapshotAsync(scenario);
        await Assert.That(invalid.GetProperty("attempts").GetInt32()).IsEqualTo(0);
        await Assert.That(invalid.GetProperty("ledgerExists").GetBoolean()).IsFalse();
        await AssertInboxAsync(scenario, 0, "No notifications in this scope.");
        await CycleAsync(scenario, new() { ["operations"] = "81", ["storage"] = "100", ["unsupported"] = "true", ["operationsContract"] = "operation-units-4kib-global-v1" });
        await AssertInboxAsync(scenario, 1, "Operations allowance: 80% reached");
        await Assert.That((await SnapshotAsync(scenario)).GetProperty("sent").GetInt32()).IsEqualTo(1);
        await CycleAsync(scenario, new() { ["operations"] = "100", ["storage"] = "100", ["unsupported"] = "true", ["operationsContract"] = "operation-units-4kib-global-v1" });
        await AssertInboxAsync(scenario, 2, "Operations allowance: 100% reached");
        await Assert.That((await SnapshotAsync(scenario)).GetProperty("sent").GetInt32()).IsEqualTo(2);
    }

    private static async Task CycleAsync(AllowanceNotificationScenario scenario, Dictionary<string, string> form)
    {
        form["_csrf"] = scenario.Csrf;
        using var response = await HttpJourney.PostFormAsync(scenario.Client, "/__test/allowance/cycle", form);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
    }

    private static async Task RoleAsync(AllowanceNotificationScenario scenario, string role)
    {
        using var response = await HttpJourney.PostFormAsync(scenario.Client, "/__test/allowance/role", new() { ["_csrf"] = scenario.Csrf, ["role"] = role });
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
    }

    private static async Task<JsonElement> SnapshotAsync(AllowanceNotificationScenario scenario)
    {
        using var response = await scenario.Client.GetAsync("/__test/allowance/snapshot");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.Clone();
    }

    private static async Task AssertInboxAsync(AllowanceNotificationScenario scenario, int count, string text)
    {
        using var response = await scenario.Client.GetAsync("/notifications");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var page = await response.Content.ReadAsStringAsync();
        await Assert.That(Regex.Matches(page, "<article class=\"ts-notification ").Count).IsEqualTo(count);
        await Assert.That(page).Contains(text);
    }
}
