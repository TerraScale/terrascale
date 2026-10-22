using System.Net;
using System.Net.Http.Headers;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.DotNet.Tests.TerraBase;

public sealed class TerraBaseDocumentReadParityTests
{
    [Test]
    public async Task Conditional_reads_preserve_headers_304_and_tombstone_ETags()
    {
        const string etag = "\"tbv1-0123456789abcdef0123456789abcdef\"";
        var calls = 0;
        using var handler = new TerraBaseTestHandler(async (request, _) =>
        {
            calls++;
            await Assert.That(request.RequestUri!.Query).IsEqualTo("?id=01JA00000000000000000000D1");
            await Assert.That(request.Headers.IfMatch.ToString()).IsEqualTo(calls < 3 ? "*" : "");
            await Assert.That(request.Headers.IfNoneMatch.ToString()).IsEqualTo(calls == 1 ? "W/\"old\"" : calls == 2 ? "W/" + etag : "");
            var response = calls switch
            {
                1 => TerraBaseIntegrationFixture.Json("""{"id":"01JA00000000000000000000D1","version":"tbv1-0123456789abcdef0123456789abcdef","state":"value","document":{"title":"hello"}}"""),
                2 => new HttpResponseMessage(HttpStatusCode.NotModified),
                _ => TerraBaseIntegrationFixture.Json("""{"error":{"code":"not_found","message":"the document does not exist"}}""", HttpStatusCode.NotFound),
            };
            response.Headers.ETag = EntityTagHeaderValue.Parse(etag);
            return response;
        });
        using var http = new HttpClient(handler);
        var db = TerraBaseIntegrationFixture.Client(http).Database(TerraBaseIntegrationFixture.ProjectId, "app");
        var live = await db.ReadDocumentAsync("items", "01JA00000000000000000000D1", "*", "W/\"old\"");
        await Assert.That(live.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(live.Document!.Document.GetProperty("title").GetString()).IsEqualTo("hello");
        var cached = await db.ReadDocumentAsync("items", "01JA00000000000000000000D1", "*", "W/" + etag);
        await Assert.That(cached.Status).IsEqualTo(HttpStatusCode.NotModified);
        await Assert.That(cached.Document).IsNull();
        await Assert.That(cached.ETag).IsEqualTo(etag);
        var deleted = await db.ReadDocumentAsync("items", "01JA00000000000000000000D1");
        await Assert.That(deleted.Status).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(deleted.Document).IsNull();
        await Assert.That(deleted.ETag).IsEqualTo(etag);
        await Assert.That(calls).IsEqualTo(3);
    }

    [Test]
    public async Task Conditional_read_errors_are_not_absence_or_retried()
    {
        var calls = 0;
        using var handler = new TerraBaseTestHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(calls == 1
                ? TerraBaseIntegrationFixture.Json("""{"error":{"code":"unknown_database","message":"unknown database"}}""", HttpStatusCode.NotFound)
                : TerraBaseIntegrationFixture.Json("""{"error":{"code":"precondition_failed","message":"version does not match"}}""", HttpStatusCode.PreconditionFailed));
        });
        using var http = new HttpClient(handler);
        var db = TerraBaseIntegrationFixture.Client(http).Database(TerraBaseIntegrationFixture.ProjectId, "app");
        var missing = await Assert.ThrowsAsync<TerraBaseNotFoundException>(() => db.ReadDocumentAsync("items", "01JA00000000000000000000D1"));
        await Assert.That(missing!.ErrorCode).IsEqualTo("unknown_database");
        var failed = await Assert.ThrowsAsync<TerraBasePreconditionFailedException>(() => db.ReadDocumentAsync("items", "01JA00000000000000000000D1", "*"));
        await Assert.That(failed!.StatusCode).IsEqualTo(HttpStatusCode.PreconditionFailed);
        await Assert.That(calls).IsEqualTo(2);
    }
}
