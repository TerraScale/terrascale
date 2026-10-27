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

    [Test]
    [Arguments(429, "tenant_busy")]
    [Arguments(503, "capacity")]
    [Arguments(502, null)]
    [Arguments(504, null)]
    public async Task TransientReadFailuresResendOnlyReads(int status, string? code)
    {
        foreach (var mutation in new[] { false, true })
        {
            var calls = 0;
            using var handler = new TerraBaseTestHandler((_, _) => Task.FromResult(
                ++calls == 1 ? Error(code, TimeSpan.Zero, (HttpStatusCode)status) : new HttpResponseMessage(HttpStatusCode.NoContent)));
            using var http = new HttpClient(handler);
            var call = () => Connection(http).SendAsync(mutation ? HttpMethod.Post : HttpMethod.Get, "/v1/test", null, null, CancellationToken.None);
            if (mutation) await Assert.ThrowsAsync<TerraBaseException>(call);
            else await call();
            await Assert.That(calls).IsEqualTo(mutation ? 1 : 2);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ALostConnectionResendsOnlyReads(bool mutation)
    {
        var calls = 0;
        using var handler = new TerraBaseTestHandler((_, _) => ++calls == 1
            ? throw new HttpRequestException(HttpRequestError.ConnectionError, "reset")
            : Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)));
        using var http = new HttpClient(handler);
        var call = () => Connection(http).SendAsync(mutation ? HttpMethod.Post : HttpMethod.Get, "/v1/test", null, null, CancellationToken.None);
        if (mutation) await Assert.ThrowsAsync<HttpRequestException>(call);
        else await call();
        await Assert.That(calls).IsEqualTo(mutation ? 1 : 2);
    }

    [Test]
    public async Task EachAttemptAsksTheTokenProviderAgain()
    {
        var sent = new List<string>();
        var issued = 0;
        using var handler = new TerraBaseTestHandler((request, _) =>
        {
            sent.Add(request.Headers.Authorization?.Parameter ?? "");
            return Task.FromResult(sent.Count == 1 ? Error("auth_unavailable", TimeSpan.Zero) : new HttpResponseMessage(HttpStatusCode.NoContent));
        });
        using var http = new HttpClient(handler);
        var connection = new TerraBaseConnection(http, new Uri("https://terrabase.example.test/"), null, null, "ApiKey", TimeSpan.FromSeconds(30),
            TerraBaseConnection.DefaultMaxResponseBytes, accessTokenProvider: _ => ValueTask.FromResult($"token{++issued}"));
        await connection.SendAsync(HttpMethod.Get, "/v1/test", null, null, CancellationToken.None);
        await Assert.That(sent).IsEquivalentTo(["token1", "token2"]);
    }

    private static TerraBaseConnection Connection(HttpClient http) =>
        new(http, new Uri("https://terrabase.example.test/"), null, "key", "ApiKey", TimeSpan.FromSeconds(30), TerraBaseConnection.DefaultMaxResponseBytes);

    private static HttpResponseMessage Error(string? code, TimeSpan? retryAfter, HttpStatusCode status = HttpStatusCode.ServiceUnavailable)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(code is null ? "Bad gateway" : $"{{\"error\":{{\"code\":\"{code}\",\"message\":\"Try again.\"}}}}"),
        };
        if (retryAfter is { } delay) response.Headers.RetryAfter = new RetryConditionHeaderValue(delay);
        return response;
    }
}
