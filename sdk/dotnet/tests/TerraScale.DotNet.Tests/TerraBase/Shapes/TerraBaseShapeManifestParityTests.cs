using System.Net;
using System.Net.Http.Headers;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.DotNet.Tests.TerraBase.Shapes;

public sealed class TerraBaseShapeManifestParityTests
{
    private const string Handle = "h1-0123456789abcdef";

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Manifest_uses_snake_case_fields_and_never_fetches_presigned_objects(bool empty)
    {
        var calls = 0;
        using var handler = new TerraBaseTestHandler((request, _) =>
        {
            calls++;
            if (request.RequestUri!.AbsolutePath != $"/v1/projects/{TerraBaseIntegrationFixture.ProjectId}/databases/app/collections/items/shape/manifest") throw new InvalidOperationException("wrong route");
            if (request.RequestUri.Query != $"?handle={Handle}&offset=2") throw new InvalidOperationException("wrong query");
            if (request.Headers.CacheControl?.NoStore != true) throw new InvalidOperationException("cacheable request");
            var objects = empty ? "[]" : """[{"offset":"2","next_offset":"4","binding":"binding","object":"object-id","url":"https://objects.example.test/sealed?fake-signature=synthetic","expires_at":"2026-10-22T00:05:00Z","epoch":"1","key_id":"01JA0000000000000000000002","sha256":"hash","size":9007199254740993}]""";
            var response = TerraBaseIntegrationFixture.Json("{\"handle\":\"" + Handle + "\",\"objects\":" + objects + "}");
            response.Headers.CacheControl = new CacheControlHeaderValue { Private = true, NoStore = true };
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        var client = new TerraBaseShapeClient(http, new TerraBaseShapeClientOptions { BaseAddress = new Uri("https://api.example.test"), ApiKey = "fake-token" });
        var manifest = await client.GetManifestAsync(TerraBaseIntegrationFixture.ProjectId, "app", "items", Handle, "2");
        await Assert.That(manifest.Objects.Count).IsEqualTo(empty ? 0 : 1);
        if (!empty)
        {
            var item = manifest.Objects.Single();
            await Assert.That(item.NextOffset).IsEqualTo("4");
            await Assert.That(item.KeyId).IsEqualTo("01JA0000000000000000000002");
            await Assert.That(item.Size).IsEqualTo(9007199254740993L);
            await Assert.That(item.ToString().Contains("signature", StringComparison.Ordinal)).IsFalse();
        }
        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    [Arguments(409, "must_refetch")]
    [Arguments(429, "shape_busy")]
    public async Task Manifest_failures_do_not_retry_or_fetch_objects(int status, string code)
    {
        var calls = 0;
        using var handler = new TerraBaseTestHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(TerraBaseIntegrationFixture.Json($$$"""{"error":{"code":"{{{code}}}","message":"refused"}}""", (HttpStatusCode)status));
        });
        using var http = new HttpClient(handler);
        var client = new TerraBaseShapeClient(http, new TerraBaseShapeClientOptions { BaseAddress = new Uri("https://api.example.test"), ApiKey = "fake-token" });
        var error = await Assert.ThrowsAsync<TerraBaseException>(() => client.GetManifestAsync(TerraBaseIntegrationFixture.ProjectId, "app", "items", Handle, "2"));
        await Assert.That(error!.ErrorCode).IsEqualTo(code);
        await Assert.That(calls).IsEqualTo(1);
    }
}
