using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Fast.Platform;

/// <summary>A TerraBase call that times out answers 503 temporarily_unavailable with Retry-After, never a 500.</summary>
[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class StorageTimeoutTests(TerraBaseFixture fixture)
{
    [Test]
    public async Task A_storage_timeout_answers_503_with_retry_after()
    {
        using var storage = new TimingOut { InnerHandler = TerraBaseFixture.NewHandler() };
        using var http = new HttpClient(storage);
        await using var oidc = await OidcHarness.StartAsync(fixture, services: services => services.AddSingleton(http));
        var access = (await oidc.SignInAndRedeemAsync()).Tokens.GetProperty("access_token").GetString()!;

        storage.Armed = true;
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/terrascale/organizations");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        using var response = await oidc.Client.SendAsync(request);
        storage.Armed = false;

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(response.Headers.RetryAfter?.Delta).IsEqualTo(TimeSpan.FromSeconds(2));
        await Assert.That(await OidcHarness.EnvelopeCodeAsync(response)).IsEqualTo("temporarily_unavailable");
    }

    // Fails every storage call the way ts-dotnet reports a TerraBase that did not answer within its request timeout.
    private sealed class TimingOut : DelegatingHandler
    {
        public volatile bool Armed;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            this.Armed ? throw new TimeoutException("TerraBase did not answer within 30 seconds.") : base.SendAsync(request, cancellationToken);
    }
}
