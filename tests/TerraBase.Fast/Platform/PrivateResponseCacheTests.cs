using System.Net;
using Bogus;
using TerraBase.Testing;

namespace TerraBase.Fast.Platform;

public sealed class PrivateResponseCacheTests
{
    [Test]
    public async Task Private_documents_and_validators_are_not_stored_even_when_not_modified()
    {
        await using var api = await DocumentApi.StartAsync();
        var id = DocumentApi.NewId();
        await Assert.That((await api.PutAsync(id, new { name = new Faker().Name.FirstName() })).Status).IsEqualTo(HttpStatusCode.Created);
        var uri = new Uri(api.Document(id), UriKind.Relative);
        using var found = await api.Client.GetAsync(uri);
        await Assert.That(found.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(found.Headers.CacheControl?.NoStore).IsTrue();
        await Assert.That(found.Headers.ETag).IsNotNull();

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.IfNoneMatch.Add(found.Headers.ETag!);
        using var unchanged = await api.Client.SendAsync(request);
        await Assert.That(unchanged.StatusCode).IsEqualTo(HttpStatusCode.NotModified);
        await Assert.That(unchanged.Headers.CacheControl?.NoStore).IsTrue();
        await Assert.That(unchanged.Headers.ETag).IsEqualTo(found.Headers.ETag);

        using var missing = await api.Client.GetAsync(new Uri(api.Document(DocumentApi.NewId()), UriKind.Relative));
        await Assert.That(missing.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(missing.Headers.CacheControl?.NoStore).IsTrue();
    }

    [Test]
    public async Task Explicit_shape_and_stream_policies_and_public_region_caching_are_preserved()
    {
        await using var api = await DocumentApi.StartAsync();
        await Assert.That((await api.PutAsync(DocumentApi.NewId(), new { name = new Faker().Name.FirstName() })).Status).IsEqualTo(HttpStatusCode.Created);
        using var bootstrap = await api.Client.GetAsync(new Uri($"{api.Collections}/{DocumentApi.Collection}/shape?offset=-1", UriKind.Relative));
        await Assert.That(bootstrap.StatusCode).IsEqualTo(HttpStatusCode.TemporaryRedirect);
        await CacheControlAssert.EqualAsync(bootstrap.Headers.CacheControl?.ToString());
        using var page = await api.Client.GetAsync(bootstrap.Headers.Location);
        await Assert.That(page.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await CacheControlAssert.EqualAsync(page.Headers.CacheControl?.ToString());
        using var conditional = new HttpRequestMessage(HttpMethod.Get, bootstrap.Headers.Location);
        conditional.Headers.IfNoneMatch.Add(page.Headers.ETag!);
        using var unchanged = await api.Client.SendAsync(conditional);
        await Assert.That(unchanged.StatusCode).IsEqualTo(HttpStatusCode.NotModified);
        await CacheControlAssert.EqualAsync(unchanged.Headers.CacheControl?.ToString());

        using (var stream = await api.Client.GetAsync(new Uri($"{api.DatabasePath()}/changes?cursor=now", UriKind.Relative), HttpCompletionOption.ResponseHeadersRead))
        {
            await Assert.That(stream.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(stream.Content.Headers.ContentType?.MediaType).IsEqualTo("text/event-stream");
            await Assert.That(stream.Headers.CacheControl?.NoStore).IsTrue();
        }

        using var regions = await api.Client.GetAsync(new Uri("/v1/regions", UriKind.Relative));
        await Assert.That(regions.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await CacheControlAssert.EqualAsync(regions.Headers.CacheControl?.ToString(), "public, max-age=5");
    }
}
