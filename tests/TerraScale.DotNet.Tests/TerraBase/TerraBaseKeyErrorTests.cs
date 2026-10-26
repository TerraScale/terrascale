using System.Net;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.DotNet.Tests.TerraBase;

public sealed class TerraBaseKeyErrorTests
{
    [Test]
    [Arguments("key_revoked", typeof(TerraBaseKeyRevokedException))]
    [Arguments("key_expired", typeof(TerraBaseKeyExpiredException))]
    public async Task RefusedKeyIsTypedAndNeverRetried(string code, Type type)
    {
        var calls = 0;
        var tokens = 0;
        using var handler = new TerraBaseTestHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(Error(HttpStatusCode.Unauthorized, code));
        });
        using var http = new HttpClient(handler);
        var connection = new TerraBaseConnection(http, new Uri("https://terrabase.example.test/"), null, null, "ApiKey",
            TimeSpan.FromSeconds(30), TerraBaseConnection.DefaultMaxResponseBytes,
            accessTokenProvider: _ => { tokens++; return ValueTask.FromResult("token"); });
        var error = await Assert.ThrowsAsync<TerraBaseAuthenticationException>(() =>
            connection.SendAsync(HttpMethod.Get, "/v1/test", null, null, CancellationToken.None));
        await Assert.That(error!.GetType()).IsEqualTo(type);
        await Assert.That(error.ErrorCode).IsEqualTo(code);
        await Assert.That(error.Message).IsEqualTo("Replace the key.");
        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(tokens).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task AuthorityUnavailableRetriesOnlySafeReads(bool mutation)
    {
        var calls = 0;
        using var handler = new TerraBaseTestHandler((_, _) => Task.FromResult(
            ++calls == 1 ? Error(HttpStatusCode.ServiceUnavailable, "auth_unavailable") : new HttpResponseMessage(HttpStatusCode.NoContent)));
        using var http = new HttpClient(handler);
        var connection = new TerraBaseConnection(http, new Uri("https://terrabase.example.test/"), null, "key", "ApiKey",
            TimeSpan.FromSeconds(30), TerraBaseConnection.DefaultMaxResponseBytes);
        var call = () => connection.SendAsync(mutation ? HttpMethod.Post : HttpMethod.Get, "/v1/test", null, null, CancellationToken.None);
        if (mutation) await Assert.ThrowsAsync<TerraBaseUnavailableException>(call);
        else await call();
        await Assert.That(calls).IsEqualTo(mutation ? 1 : 2);
    }

    private static HttpResponseMessage Error(HttpStatusCode status, string code)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent($"{{\"error\":{{\"code\":\"{code}\",\"message\":\"Replace the key.\"}}}}") };
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
        return response;
    }
}
