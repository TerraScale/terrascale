using System.Net;
using TerraScale.TerraBase.Testing;

namespace TerraScale.TerraBase.Slow.Management;

/// <summary>ts-auth access tokens against a real clock and, once TS-491 lands, a real ts-auth.</summary>
public sealed class UserAuthTests
{
    [Test]
    public async Task A_server_started_while_ts_auth_was_down_becomes_ready_once_it_answers()
    {
        using var tls = new ClientTls();
        using var issuer = new TestIssuer { Available = false };
        await using var server = await TerraBaseTestServer.StartAsync(
            settings =>
            {
                tls.Apply(settings);
                TestIssuer.Configure(settings);
            },
            issuer.Route);
        using var client = tls.CreateClient(server.ClientAddress!, issuer.Mint(Guid.NewGuid().ToString()));
        var rejected = await StatusAsync(client, "/v1/management/organizations");
        var notReady = await StatusAsync(server.Client, "/readyz");

        issuer.Available = true;
        // The background fetch retries every 2 s: wait for it the way a load balancer does, by probing. The deadline bounds
        // the delays and every probe, so nothing outlives it.
        var ready = HttpStatusCode.ServiceUnavailable;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            while (ready != HttpStatusCode.OK)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), deadline.Token);
                ready = await StatusAsync(server.Client, "/readyz", deadline.Token);
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            // Still not ready: the assertion below reports it.
        }

        var accepted = await StatusAsync(client, "/v1/management/organizations");

        await Assert.That(rejected).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(notReady).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(ready).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(accepted).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    [Skip("TS-491: ts-auth main (C#) has no OIDC provider yet, so no binary serves discovery, a JWKS or at+jwt access tokens. "
        + "The removed Rust provider issued typ JWT access tokens without email, which docs/decisions/0007 does not accept. "
        + "When TS-491 lands: start the ts-auth binary, register a client for TerraBase's audience, obtain an access token and call the Management API with it.")]
    public Task Access_tokens_from_the_real_ts_auth_binary_authenticate() => Task.CompletedTask;

    private static async Task<HttpStatusCode> StatusAsync(HttpClient client, string path, CancellationToken cancellationToken = default)
    {
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), cancellationToken);
        return response.StatusCode;
    }
}
