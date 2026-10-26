using System.Globalization;
using System.Net;
using TerraBase.Testing;

namespace TerraBase.Fast.Documents;

public sealed class QueryExplainTests
{
    [Test]
    public async Task Explain_reports_a_full_scan_with_a_suggestion_then_the_suggested_index()
    {
        await using var api = await DocumentApi.StartAsync();
        for (var i = 0; i < 100; i++)
        {
            (await api.PutAsync(DocumentApi.NewId(), new { status = "s" + (i % 10).ToString(CultureInfo.InvariantCulture), amount = i })).Status.ShouldBe(HttpStatusCode.Created);
        }

        var query = new { filter = new { and = new object[] { new { gte = new object[] { "/amount", 30 } }, new { eq = new object[] { "/status", "s4" } } } }, explain = true };
        var beforeScan = await api.ReadUsageAsync();
        var scan = await api.QueryAsync(query);
        await Assert.That(await api.ReadUsageAsync()).IsEqualTo((beforeScan.Units + 7, beforeScan.Reads + 1));
        using (var owner = api.Management.As(api.Tenant.User))
        {
            using var index = await owner.PostAsJsonAsync(ManagementApi.Uri(
                $"/v1/management/organizations/{api.Tenant.OrganizationId}/projects/{api.Tenant.ProjectId}/databases/{(await api.TenantOfAsync()).DatabaseId}/indexes"),
                new { name = "by_status", collection = "items", kind = "btree", fields = scan.Json.GetProperty("explain").GetProperty("suggested_index") });
            index.EnsureSuccessStatusCode();
        }

        var beforeSeek = await api.ReadUsageAsync();
        var seek = await api.QueryAsync(query);
        await Assert.That(await api.ReadUsageAsync()).IsEqualTo((beforeSeek.Units + 7, beforeSeek.Reads + 1));

        await Assert.That(scan.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(scan.Json.GetProperty("items").GetArrayLength()).IsEqualTo(7);
        var before = scan.Json.GetProperty("explain");
        await Assert.That(before.GetProperty("full_scan").GetBoolean()).IsTrue();
        await Assert.That(before.GetProperty("suggested_index").EnumerateArray().Select(field => field.GetString()!).ToArray()).IsEquivalentTo(["status"]);
        await Assert.That(before.GetProperty("rows_scanned").GetInt64()).IsEqualTo(100);
        await Assert.That(before.GetProperty("work_units").GetInt64()).IsGreaterThan(0);
        var after = seek.Json.GetProperty("explain");
        await Assert.That(seek.Json.GetProperty("items").GetArrayLength()).IsEqualTo(7);
        await Assert.That(after.GetProperty("full_scan").GetBoolean()).IsFalse();
        await Assert.That(after.GetProperty("indexes").EnumerateArray().Select(name => name.GetString()!).ToArray()).IsEquivalentTo(["by_status"]);
        await Assert.That(after.TryGetProperty("suggested_index", out _)).IsFalse();
        await Assert.That(after.GetProperty("rows_scanned").GetInt64()).IsLessThan(100);
    }
}
