using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.RateLimiting;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Fast.Security;

/// <summary>The token peer guard before OpenIddict, over real HTTP and the real TerraBase.</summary>
[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class TokenRateLimitTests(TerraBaseFixture fixture)
{
    private const int Capacity = 3;
    private const string TokenPath = "/terrascale/oauth/token";
    private static readonly DateTimeOffset Start = new(2026, 10, 7, 12, 0, 5, TimeSpan.Zero);
    private static readonly string MasterKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    [Test]
    [Arguments(TokenPath)]
    [Arguments("/TerraScale/OAUTH/Token/")]
    public async Task Invalid_client_credentials_exhaust_the_guard_and_forged_headers_cannot_bypass_it(string path)
    {
        var clock = new ManualClock(Start);
        await using var oidc = await OidcHarness.StartAsync(fixture, values => values["rate-limit"] = "3", clock: clock);
        for (var attempt = 0; attempt < Capacity; attempt++)
        {
            using var invalid = await InvalidClientAsync(oidc.Server, path, attempt, spoof: true);
            await Assert.That(invalid.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
            await Assert.That(await OidcHarness.ErrorAsync(invalid)).IsEqualTo("invalid_client");
        }

        using var limited = await InvalidClientAsync(oidc.Server, path, Capacity, spoof: true);
        await AssertOAuthErrorAsync(limited, HttpStatusCode.TooManyRequests, "rate_limited", TimeSpan.FromSeconds(55));
        // Rotating client IDs, forwarding headers and submitted IPs created only the socket peer's bucket.
        await Assert.That((await oidc.Database.ListDocumentsAsync(AttemptLimiter.Collection)).Items.Count).IsEqualTo(1);

        clock.Advance(TimeSpan.FromSeconds(54));
        using var lastSecond = await InvalidClientAsync(oidc.Server, path, Capacity + 1, spoof: true);
        await AssertOAuthErrorAsync(lastSecond, HttpStatusCode.TooManyRequests, "rate_limited", TimeSpan.FromSeconds(1));
        clock.Advance(TimeSpan.FromSeconds(1));
        using var nextWindow = await InvalidClientAsync(oidc.Server, path);
        await Assert.That(nextWindow.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Missing_client_authentication_and_malformed_bodies_also_charge_the_guard(bool malformed)
    {
        await using var server = await StartAsync(await SharedStateTarget.CreateAsync(fixture), new ManualClock(Start));
        for (var attempt = 0; attempt < Capacity; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, TokenPath)
            {
                Content = malformed
                    ? new StringContent("{}", Encoding.UTF8, "application/json")
                    : new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["refresh_token"] = "invalid" }),
            };
            using var invalid = await server.Client.SendAsync(request);
            await Assert.That(invalid.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized).IsTrue();
        }

        using var limited = await InvalidClientAsync(server);
        await AssertOAuthErrorAsync(limited, HttpStatusCode.TooManyRequests, "rate_limited", TimeSpan.FromSeconds(55));
    }

    [Test]
    [Arguments("127.0.0.1", "::ffff:127.0.0.1")]
    [Arguments("2001:db8:704:1::1", "2001:db8:704:1:abcd::2")]
    public async Task Replicas_share_the_normalized_peer_allowance(string firstAddress, string secondAddress)
    {
        var target = await SharedStateTarget.CreateAsync(fixture);
        var clock = new ManualClock(Start);
        await using var first = await StartAsync(target, clock, new PeerAddress(IPAddress.Parse(firstAddress)));
        await using var second = await StartAsync(target, clock, new PeerAddress(IPAddress.Parse(secondAddress)));
        for (var attempt = 0; attempt < Capacity - 1; attempt++)
        {
            using var invalid = await InvalidClientAsync(first);
            await Assert.That(invalid.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        }

        using var lastSlot = await InvalidClientAsync(second);
        await Assert.That(lastSlot.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        using var limited = await InvalidClientAsync(second);
        await AssertOAuthErrorAsync(limited, HttpStatusCode.TooManyRequests, "rate_limited", TimeSpan.FromSeconds(55));
        using var alsoLimited = await InvalidClientAsync(first);
        await AssertOAuthErrorAsync(alsoLimited, HttpStatusCode.TooManyRequests, "rate_limited", TimeSpan.FromSeconds(55));
    }

    [Test]
    public async Task An_IPv6_site_cannot_multiply_its_allowance_by_rotating_subnets_and_other_peers_are_not_locked_out()
    {
        var target = await SharedStateTarget.CreateAsync(fixture);
        var peer = new PeerAddress(IPAddress.IPv6Loopback);
        await using var server = await StartAsync(target, new ManualClock(Start), peer);
        var limiter = server.App.Services.GetRequiredService<AttemptLimiter>();
        // A deliberate keyed-fold collision shares a budget; select distinct folds to test the /48 budget itself.
        var subnets = Enumerable.Range(1, 128).Select(number => IPAddress.Parse($"2001:db8:704:{number:x}::1"))
            .DistinctBy(address => limiter.Fold(HostedAuth.Subject(address.ToString())!)).Take(5).ToArray();
        await Assert.That(subnets.Length).IsEqualTo(5);
        foreach (var address in subnets.Take(4))
        {
            peer.Address = address;
            for (var attempt = 0; attempt < Capacity; attempt++)
            {
                using var invalid = await InvalidClientAsync(server);
                await Assert.That(invalid.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
            }
        }

        peer.Address = subnets[4];
        using var siteLimited = await InvalidClientAsync(server);
        await AssertOAuthErrorAsync(siteLimited, HttpStatusCode.TooManyRequests, "rate_limited", TimeSpan.FromSeconds(55));

        var usedFolds = subnets.Select(address => limiter.Fold(HostedAuth.Subject(address.ToString())!)).ToHashSet(StringComparer.Ordinal);
        peer.Address = Enumerable.Range(1, 128).Select(number => IPAddress.Parse($"203.0.113.{number}"))
            .First(address => !usedFolds.Contains(limiter.Fold(address.ToString())));
        using var independentPeer = await InvalidClientAsync(server);
        await Assert.That(independentPeer.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task Rejected_TerraBase_credentials_fail_closed_with_an_OAuth_error_without_limiting_other_routes()
    {
        var target = await SharedStateTarget.CreateAsync(fixture);
        await using var server = await StartAsync(target, new ManualClock(Start), rejectedStorageKey: true);
        using var unavailable = await InvalidClientAsync(server);
        await AssertOAuthErrorAsync(unavailable, HttpStatusCode.ServiceUnavailable, "temporarily_unavailable", TimeSpan.FromSeconds(2));
        await Assert.That(await target.CountAsync(AttemptLimiter.Collection)).IsEqualTo(0);

        using var health = await server.Client.GetAsync("/healthz");
        await Assert.That(health.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var otherTenant = await InvalidClientAsync(server, "/other/oauth/token");
        await Assert.That(otherTenant.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        using var wrongMethod = await server.Client.GetAsync(TokenPath);
        await Assert.That(wrongMethod.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);

        using var contractResponse = await server.Client.GetAsync("/openapi/v1.json");
        var operation = (await OidcHarness.JsonAsync(contractResponse)).GetProperty("paths").GetProperty("/{slug}/oauth/token").GetProperty("post");
        foreach (var status in new[] { "429", "503" })
        {
            await Assert.That(operation.GetProperty("responses").GetProperty(status).GetProperty("content").GetProperty("application/json")
                .GetProperty("schema").GetProperty("$ref").GetString()).IsEqualTo("#/components/schemas/OAuthErrorBody");
        }

        await Assert.That(operation.GetProperty("description").GetString()).Contains("default 1000");
    }

    [Test]
    public async Task A_missing_socket_peer_cannot_be_replaced_by_forwarding_headers()
    {
        var target = await SharedStateTarget.CreateAsync(fixture);
        await using var server = await StartAsync(target, new ManualClock(Start), new PeerAddress(null));
        using var unavailable = await InvalidClientAsync(server, spoof: true);
        await AssertOAuthErrorAsync(unavailable, HttpStatusCode.ServiceUnavailable, "temporarily_unavailable", TimeSpan.FromSeconds(2));
        await Assert.That(await target.CountAsync(AttemptLimiter.Collection)).IsEqualTo(0);
    }

    private static Task<AuthTestServer> StartAsync(SharedStateTarget target, TimeProvider clock, PeerAddress? peer = null, bool rejectedStorageKey = false) =>
        AuthTestServer.StartAsync(values =>
        {
            values["master-key"] = MasterKey;
            values["rate-limit"] = "3";
        }, services =>
        {
            target.Register(services, rejectedStorageKey ? "tb_invalid_token_guard_test_key" : null);
            services.AddSingleton(clock);
            if (peer is not null)
            {
                services.AddSingleton<IStartupFilter>(peer);
            }
        });

    private static async Task<HttpResponseMessage> InvalidClientAsync(AuthTestServer server, string path = TokenPath, int attempt = 0, bool spoof = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = "invalid",
                ["ip_address"] = $"198.51.100.{attempt + 1}",
            }),
        };
        var clientId = attempt % 2 == 0 ? OidcIssuer.DashboardClientId : $"unknown-client-{attempt}";
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:wrong-secret-{attempt}")));
        if (spoof)
        {
            request.Headers.Add("Forwarded", $"for=192.0.2.{attempt + 1};proto=https");
            request.Headers.Add("X-Forwarded-For", $"203.0.113.{attempt + 1}");
            request.Headers.Add("X-Real-IP", $"198.51.100.{attempt + 1}");
        }

        return await server.Client.SendAsync(request);
    }

    private static async Task AssertOAuthErrorAsync(HttpResponseMessage response, HttpStatusCode status, string error, TimeSpan? delay = null)
    {
        var body = await OidcHarness.JsonAsync(response);
        await Assert.That(response.StatusCode).IsEqualTo(status);
        await Assert.That(body.GetProperty("error").GetString()).IsEqualTo(error);
        await Assert.That(string.IsNullOrEmpty(body.GetProperty("error_description").GetString())).IsFalse();
        await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo("application/json");
        await Assert.That(response.Headers.CacheControl!.NoStore).IsTrue();
        await Assert.That(response.Headers.RetryAfter?.Delta).IsEqualTo(delay);
    }

    // Test-only connection metadata injection covers IPv6/missing peers on the real loopback HTTP listener.
    // The guard, OpenIddict and persistence are the production implementations.
    private sealed class PeerAddress(IPAddress? initialAddress) : IStartupFilter
    {
        private IPAddress? address = initialAddress;

        public IPAddress? Address
        {
            get => Volatile.Read(ref this.address);
            set => Volatile.Write(ref this.address, value);
        }

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((HttpContext context, RequestDelegate nextRequest) =>
            {
                context.Connection.RemoteIpAddress = this.Address;
                return nextRequest(context);
            });
            next(app);
        };
    }
}
