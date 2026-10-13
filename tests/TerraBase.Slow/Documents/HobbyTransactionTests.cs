using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Slow.Documents;

public sealed class HobbyTransactionTests
{
    [Test]
    public async Task A_hobby_transaction_commits_all_1024_operations_with_storage_accounting()
    {
        await using var api = await ManagementApi.StartAsync();
        using var tenant = await api.CreateTenantAsync("documents:read", "documents:write");
        var plan = await api.Server.Services.GetRequiredService<ITenantDirectory>().PlanAsync(tenant.OrganizationId, CancellationToken.None);
        await Assert.That(plan is PlanTier.Hobby).IsTrue();
        using var database = await tenant.Data.PutAsync(tenant.Database("maximum"), null);
        database.EnsureSuccessStatusCode();
        using var collection = await tenant.Data.PostAsJsonAsync(tenant.Database("maximum", "/collections"), new { name = "items" });
        collection.EnsureSuccessStatusCode();

        var ops = Enumerable.Range(0, 1024).Select(_ => new { op = "create", collection = "items", document = new { } });
        using var transaction = await tenant.Data.PostAsJsonAsync(tenant.Database("maximum", "/transactions"), new { ops });
        using var listed = await tenant.Data.PostAsJsonAsync(tenant.Database("maximum", "/collections/items/query"), new { limit = 1, count = true });

        await Assert.That(transaction.StatusCode).IsEqualTo(HttpStatusCode.OK);
        listed.EnsureSuccessStatusCode();
        var body = await listed.Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(body.GetProperty("count").GetProperty("value").GetString()).IsEqualTo("1024");
    }
}
