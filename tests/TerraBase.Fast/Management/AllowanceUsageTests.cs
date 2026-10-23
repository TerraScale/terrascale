using System.Net;
using System.Text.Json;
using TerraBase.Fast.Documents;

namespace TerraBase.Fast.Management;

public sealed class AllowanceUsageTests
{
    [Test]
    public async Task Internal_feed_keeps_operation_utilization_unavailable_and_public_clients_cannot_read_it()
    {
        await using var api = await DocumentApi.StartAsync();
        (await api.PutAsync(DocumentApi.NewId(), new { value = "stored" })).Status.ShouldBe(HttpStatusCode.Created);
        using var response = await api.Server.Client.GetAsync(new Uri("/v1/usage/allowances", UriKind.Relative));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var page = await response.Content.ReadFromJsonAsync<JsonElement>();
        var row = page.GetProperty("items").EnumerateArray().Single(item => item.GetProperty("organizationId").GetString() == api.Tenant.OrganizationId);
        await Assert.That(row.GetProperty("operations").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(row.GetProperty("logicalBytes").GetInt64()).IsGreaterThan(0);
        await Assert.That(page.GetProperty("operationsContract").GetString()).IsEqualTo("document-operations-node-v1");
        await Assert.That(page.GetProperty("storageContract").GetString()).IsEqualTo("logical-bytes-node-v1");
        using var forbidden = await api.Client.GetAsync(new Uri("/v1/usage/allowances", UriKind.Relative));
        await Assert.That(forbidden.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        using var invalid = await api.Server.Client.GetAsync(new Uri("/v1/usage/allowances?after=not-a-ulid", UriKind.Relative));
        await Assert.That(invalid.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }
}
