using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Fast.Documents;
using TerraBase.Features.Qos;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Qos;

public sealed class UniqueQuotaTests
{
    [Test]
    [Arguments(false, 0)]
    [Arguments(false, 1)]
    [Arguments(false, 2)]
    [Arguments(true, 0)]
    [Arguments(true, 1)]
    [Arguments(true, 2)]
    public async Task Hidden_current_JSON_counts_until_it_is_repaired_or_deleted(bool invalid, int operation)
    {
        const string Visible = "{\"email\":\"same\"}";
        var hidden = invalid ? "{\"email\":[],\"padding\":\"x\"}" : Visible;
        var limit = Encoding.UTF8.GetByteCount(Visible) + Encoding.UTF8.GetByteCount(hidden);
        await using var api = await DocumentApi.StartAsync(services: services => services.AddSingleton<IStorageQuota>(provider =>
            new StorageQuota(provider.GetRequiredService<IStorageEngine>(), provider.GetRequiredService<ITenantDirectory>(), _ => limit)));
        var ids = new[] { DocumentApi.NewId(), DocumentApi.NewId() }.Order(StringComparer.Ordinal).ToArray();
        using var first = JsonDocument.Parse(Visible);
        using var second = JsonDocument.Parse(hidden);
        (await api.PutAsync(ids[0], first.RootElement)).Status.ShouldBe(HttpStatusCode.Created);
        (await api.PutAsync(ids[1], second.RootElement)).Status.ShouldBe(HttpStatusCode.Created);
        var tenant = await api.TenantOfAsync();
        var store = (await api.Server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(tenant, CancellationToken.None))!;
        // Incoming uniqueness metadata projects the existing raw state into a hidden contender.
        await using (var transaction = await store.BeginTransactionAsync(write: true, CancellationToken.None))
        {
            await transaction.ApplyAsync(Deltas.Write("_unique", "items",
                "{\"constraints\":[{\"name\":\"email\",\"fields\":[\"email\"],\"normalization\":\"none\"}]}"), CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
        }

        await Assert.That((await api.GetAsync(ids[1])).Status).IsEqualTo(HttpStatusCode.Conflict);
        await using (var transaction = await store.BeginTransactionAsync(write: false, CancellationToken.None))
        {
            await Assert.That(await transaction.GetStoredDocumentBytesAsync(CancellationToken.None)).IsEqualTo((long)limit);
            await Assert.That(await transaction.GetDocumentBytesAsync("items", ids[1], CancellationToken.None))
                .IsEqualTo((long)Encoding.UTF8.GetByteCount(hidden));
        }

        await Assert.That((await api.PutAsync(DocumentApi.NewId(), new { n = 1 })).Code).IsEqualTo("storage_quota");
        using var owner = api.Management.As(api.Tenant.User);
        var managed = $"/v1/management/organizations/{tenant.OrganizationId}/projects/{tenant.ProjectId}/databases/{tenant.DatabaseId}";
        using var branch = await owner.PostAsJsonAsync(ManagementApi.Uri(managed + "/branches"), new { name = "copy" });
        await Assert.That(branch.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
        if (operation == 0)
        {
            (await api.SendAsync(HttpMethod.Delete, api.Document(ids[1]))).Status.ShouldBe(HttpStatusCode.OK);
            (await api.PutAsync(DocumentApi.NewId(), new { n = 1 })).Status.ShouldBe(HttpStatusCode.Created);
        }
        else if (operation == 1)
        {
            (await api.PutAsync(ids[1], new { email = "solo" })).Status.ShouldBe(HttpStatusCode.OK);
            (await api.GetAsync(ids[1])).Status.ShouldBe(HttpStatusCode.OK);
            await Assert.That((await api.PutAsync(DocumentApi.NewId(), new { padding = new string('x', limit) })).Code)
                .IsEqualTo("storage_quota");
        }
        else
        {
            (await api.SendAsync(HttpMethod.Delete, api.Document(ids[0]))).Status.ShouldBe(HttpStatusCode.OK);
            (await api.GetAsync(ids[1])).Status.ShouldBe(invalid ? HttpStatusCode.Conflict : HttpStatusCode.OK);
            (await api.PutAsync(DocumentApi.NewId(), new { n = 1 })).Status.ShouldBe(HttpStatusCode.Created);
        }
    }
}
