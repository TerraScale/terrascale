using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.DotNet.Tests.TerraBase;

public sealed class TerraBaseFunctionInvocationTests
{
    private const string Root = "/v1/projects/" + TerraBaseIntegrationFixture.ProjectId + "/databases/app/functions/hello";

    [Test]
    [Arguments(200)]
    [Arguments(201)]
    [Arguments(204)]
    [Arguments(302)]
    [Arguments(401)]
    [Arguments(403)]
    [Arguments(404)]
    [Arguments(413)]
    [Arguments(422)]
    [Arguments(500)]
    [Arguments(503)]
    public async Task Invocation_preserves_status_media_and_raw_body_after_disposal(int status)
    {
        byte[] bytes = status == 204 ? [] : [0, 255, 13, 10, 42];
        var calls = 0;
        using var reply = new HttpResponseMessage((HttpStatusCode)status) { Content = new ByteArrayContent(bytes) };
        reply.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var handler = new TerraBaseTestHandler(async (request, token) =>
        {
            calls++;
            await Assert.That(request.Method).IsEqualTo(HttpMethod.Post);
            await Assert.That(request.RequestUri!.AbsolutePath).IsEqualTo(Root + "/invoke");
            await Assert.That(request.RequestUri.Query).IsEqualTo("?a%26%3D%3F=%26%2B%3F");
            await Assert.That(request.Headers.Authorization!.ToString()).IsEqualTo("Bearer project-user-token");
            await Assert.That(request.Headers.Contains("Idempotency-Key")).IsFalse();
            await Assert.That(await request.Content!.ReadAsStringAsync(token)).IsEqualTo("raw text");
            return reply;
        });
        using var http = new HttpClient(handler);
        var db = TerraBaseIntegrationFixture.Client(http).Database(TerraBaseIntegrationFixture.ProjectId, "app");
        Task<TerraBaseFunctionResponse> Invoke() => db.InvokeFunctionAsync("hello", "raw text"u8.ToArray(), new Dictionary<string, string> { ["a&=?"] = "&+?" });
        var result = status < 300 ? await Invoke() : (await Assert.ThrowsAsync<TerraBaseFunctionInvocationException>(() => Invoke()))!.Response;
        await Assert.That(result.StatusCode).IsEqualTo((HttpStatusCode)status);
        await Assert.That(result.ContentType).IsEqualTo("application/octet-stream");
        await Assert.That(result.Body.ToArray()).IsEquivalentTo(bytes);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => reply.Content.ReadAsByteArrayAsync());
        await Assert.That(calls).IsEqualTo(1);
    }

    private sealed class WaitingBody(TaskCompletionSource started) : MemoryStream
    {
        public override async Task CopyToAsync(Stream destination, int bufferSize, CancellationToken token)
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }
    }
}
