using System.Net;

namespace TerraBase.Fast.Documents;

public sealed class SharedDocumentServerTests
{
    [Test]
    public async Task Shared_host_keeps_tenants_isolated_and_outlives_a_disposed_test_client()
    {
        var clients = await Task.WhenAll(DocumentApi.StartAsync(), DocumentApi.StartAsync());
        await using var first = clients[0];
        await using var second = clients[1];
        await Assert.That(ReferenceEquals(first.Server, second.Server)).IsTrue();
        await Assert.That(first.Tenant.OrganizationId).IsNotEqualTo(second.Tenant.OrganizationId);
        await Assert.That(first.Tenant.ProjectId).IsNotEqualTo(second.Tenant.ProjectId);

        var id = DocumentApi.NewId();
        await Assert.That((await first.PutAsync(id, new { owner = "first" })).Status).IsEqualTo(HttpStatusCode.Created);
        await Assert.That((await second.GetAsync(id)).Status).IsEqualTo(HttpStatusCode.NotFound);
        var crossTenant = await DocumentApi.SendAsync(second.Client, HttpMethod.Get, first.Document(id));
        await Assert.That(crossTenant.Status).IsEqualTo(HttpStatusCode.NotFound);
        await first.DisposeAsync();

        await Assert.That((await second.PutAsync(id, new { owner = "second" })).Status).IsEqualTo(HttpStatusCode.Created);
        await Assert.That((await second.GetAsync(id)).Json.GetProperty("document").GetProperty("owner").GetString()).IsEqualTo("second");
    }
}
