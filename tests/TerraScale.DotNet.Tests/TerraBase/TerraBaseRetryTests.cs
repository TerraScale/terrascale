using System.Net;
using System.Net.Http.Headers;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.DotNet.Tests.TerraBase;

/// <summary>Which TerraBase refusals the connection resends; an HTTP fake answers every request.</summary>
public sealed class TerraBaseRetryTests
{
    [Test]
    [Arguments("remote_region_unavailable")]
    [Arguments("confirmation_busy")]
    [Arguments("catalog_stale")]
    public async Task RefusalsBeforeExecutionResendTheSameWrite(string code)
    {
        var bodies = new List<string>();
        using var handler = new TerraBaseTestHandler(async (request, token) =>
        {
            bodies.Add(await request.Content!.ReadAsStringAsync(token));
            return bodies.Count == 1 ? Error(code, TimeSpan.Zero) : new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        using var http = new HttpClient(handler);
        using var content = new ByteArrayContent("{\"a\":1}"u8.ToArray());
        await Connection(http).SendAsync(HttpMethod.Put, "/v1/test", content, null, CancellationToken.None);
        await Assert.That(bodies).IsEquivalentTo(["{\"a\":1}", "{\"a\":1}"]);
    }

    [Test]
    [Arguments("PUT")]
    [Arguments("GET")]
    public async Task AnUnknownWriteOutcomeIsNeverResent(string method)
    {
        var calls = 0;
        using var handler = new TerraBaseTestHandler((_, _) => { calls++; return Task.FromResult(Error("write_outcome_unknown", null)); });
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<TerraBaseUnavailableException>(() => Connection(http).SendAsync(new HttpMethod(method), "/v1/test", null, null, CancellationToken.None));
        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task BusyStorageResendsOnlyReads(bool mutation)
    {
        var calls = 0;
        using var handler = new TerraBaseTestHandler((_, _) => Task.FromResult(
            ++calls == 1 ? Error("storage_busy", TimeSpan.Zero) : new HttpResponseMessage(HttpStatusCode.NoContent)));
        using var http = new HttpClient(handler);
        var call = () => Connection(http).SendAsync(mutation ? HttpMethod.Post : HttpMethod.Get, "/v1/test", null, null, CancellationToken.None);
        if (mutation) await Assert.ThrowsAsync<TerraBaseUnavailableException>(call);
        else await call();
        await Assert.That(calls).IsEqualTo(mutation ? 1 : 2);
    }

    [Test]
    public async Task AWaitPastTheDeadlineReportsTheRefusalAtOnce()
    {
        var calls = 0;
        using var handler = new TerraBaseTestHandler((_, _) => { calls++; return Task.FromResult(Error("confirmation_busy", TimeSpan.FromSeconds(60))); });
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<TerraBaseUnavailableException>(() => Connection(http).SendAsync(HttpMethod.Get, "/v1/test", null, null, CancellationToken.None));
        await Assert.That(error!.ErrorCode).IsEqualTo("confirmation_busy");
        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    public async Task ResendsStopAfterTwo()
    {
        var calls = 0;
        using var handler = new TerraBaseTestHandler((_, _) => { calls++; return Task.FromResult(Error("remote_region_unavailable", TimeSpan.Zero)); });
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<TerraBaseUnavailableException>(() => Connection(http).SendAsync(HttpMethod.Get, "/v1/test", null, null, CancellationToken.None));
        await Assert.That(calls).IsEqualTo(3);
    }

    private static TerraBaseConnection Connection(HttpClient http) =>
        new(http, new Uri("https://terrabase.example.test/"), null, "key", "ApiKey", TimeSpan.FromSeconds(30), TerraBaseConnection.DefaultMaxResponseBytes);

    private static HttpResponseMessage Error(string code, TimeSpan? retryAfter)
    {
        var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent($"{{\"error\":{{\"code\":\"{code}\",\"message\":\"Try again.\"}}}}") };
        if (retryAfter is { } delay) response.Headers.RetryAfter = new RetryConditionHeaderValue(delay);
        return response;
    }
}
