using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Primitives;

namespace TerraBase.Fast.Documents;

public sealed class StoredJsonAdmissionTests
{
    [Test]
    public async Task Storage_rejects_malformed_JSON_without_advancing_shapes_or_allowing_commit()
    {
        await using var api = await DocumentApi.StartAsync();
        using var shapes = await api.ClientWithScopesAsync("shapes:read");
        var store = (await api.Server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(await api.TenantOfAsync(), CancellationToken.None))!;
        await using (var write = await store.BeginTransactionAsync(write: true, CancellationToken.None))
        {
            var malformed = Encoding.UTF8.GetBytes("{\"a\":");
            var id = DocumentApi.NewId();
            await Assert.That(async () => await write.PutAsync("items", id, malformed, CancellationToken.None)).Throws<DocumentValidationException>();
            await Assert.That(async () => await write.CommitAsync(CancellationToken.None)).Throws<InvalidOperationException>();
        }

        using var start = await shapes.GetAsync(new Uri(api.Shape + "?offset=-1", UriKind.Relative));
        await Assert.That(start.StatusCode).IsEqualTo(HttpStatusCode.TemporaryRedirect);
        var snapshot = await DocumentApi.SendAsync(shapes, HttpMethod.Get, start.Headers.Location!.OriginalString);
        var handle = start.Headers.GetValues("TB-Shape-Handle").Single();
        var log = await DocumentApi.SendAsync(shapes, HttpMethod.Get, $"{api.Shape}?handle={handle}&offset=0");
        await Assert.That(snapshot.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(log.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(snapshot.Json.GetProperty("changes").EnumerateArray().SelectMany(change => change.GetProperty("ops").EnumerateArray())).IsEmpty();
        await Assert.That(log.Json.GetProperty("changes").GetArrayLength()).IsEqualTo(0);
        var next = DocumentApi.NewId();
        (await api.PutAsync(next, new { ok = true })).Status.ShouldBe(HttpStatusCode.Created);
        (await api.GetAsync(next)).Status.ShouldBe(HttpStatusCode.OK);
    }
}
