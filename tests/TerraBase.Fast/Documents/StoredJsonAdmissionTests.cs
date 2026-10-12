using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Primitives;

namespace TerraBase.Fast.Documents;

public sealed class StoredJsonAdmissionTests
{
    [Test]
    public async Task Storage_rejects_malformed_JSON_without_allowing_commit()
    {
        await using var api = await DocumentApi.StartAsync();
        var store = (await api.Server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(await api.TenantOfAsync(), CancellationToken.None))!;
        var id = DocumentApi.NewId();
        await using (var write = await store.BeginTransactionAsync(write: true, CancellationToken.None))
        {
            var malformed = Encoding.UTF8.GetBytes("{\"a\":");
            await Assert.That(async () => await write.PutAsync("items", id, malformed, CancellationToken.None)).Throws<DocumentValidationException>();
            await Assert.That(async () => await write.CommitAsync(CancellationToken.None)).Throws<InvalidOperationException>();
        }

        (await api.GetAsync(id)).Status.ShouldBe(HttpStatusCode.NotFound);
        var next = DocumentApi.NewId();
        (await api.PutAsync(next, new { ok = true })).Status.ShouldBe(HttpStatusCode.Created);
        (await api.GetAsync(next)).Status.ShouldBe(HttpStatusCode.OK);
    }
}
