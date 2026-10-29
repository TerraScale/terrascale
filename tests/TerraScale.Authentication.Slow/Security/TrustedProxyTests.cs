using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.RateLimiting;
using TerraScale.Authentication.Tests;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Authentication.Slow.Security;

/// <summary>Opt-in, single-hop client IP resolution over real HTTP and the real TerraBase.</summary>
[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class TrustedProxyTests(TerraBaseFixture fixture)
{
    private const string Proxy = "10.0.0.1";
    private const int Capacity = 3;
    private const string TokenPath = "/terrascale/oauth/token";
    private static readonly DateTimeOffset Start = new(2026, 10, 7, 12, 0, 5, TimeSpan.Zero);
    private static readonly string MasterKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    [Test]
    [Arguments(Proxy, "203.0.113.10", "::ffff:203.0.113.10")]
    [Arguments("::ffff:10.0.0.1", "203.0.113.10", "::ffff:203.0.113.10")]
    [Arguments("2001:db8:ffff::1", "2001:db8:704:1::1", "2001:db8:704:1:abcd::2")]
    public async Task Trusted_clients_share_normalized_allowances_across_replicas_without_locking_out_other_clients(
        string proxy, string firstAddress, string secondAddress)
    {
        var target = await SharedStateTarget.CreateAsync(fixture);
        var clock = new ManualClock(Start);
        await using var first = await StartAsync(target, clock, new PeerAddress(IPAddress.Parse(proxy)), proxy);
        await using var second = await StartAsync(target, clock, new PeerAddress(IPAddress.Parse(proxy)), proxy);
        for (var attempt = 0; attempt < Capacity; attempt++)
        {
            using var invalid = await InvalidClientAsync(attempt % 2 == 0 ? first : second, attempt % 2 == 0 ? firstAddress : secondAddress, attempt);
            await AssertTokenAsync(invalid, HttpStatusCode.Unauthorized, "invalid_client");
        }

        using var limited = await InvalidClientAsync(second, secondAddress, Capacity);
        await AssertTokenAsync(limited, HttpStatusCode.TooManyRequests, "rate_limited", TimeSpan.FromSeconds(55));
        using var alsoLimited = await InvalidClientAsync(first, firstAddress, Capacity + 1);
        await AssertTokenAsync(alsoLimited, HttpStatusCode.TooManyRequests, "rate_limited", TimeSpan.FromSeconds(55));

        var limiter = first.App.Services.GetRequiredService<AttemptLimiter>();
        var independent = Clients(limiter, ipv6: false).First(address => limiter.Fold(address) != limiter.Fold(HostedAuth.Subject(firstAddress)!));
        using var otherClient = await InvalidClientAsync(second, independent, Capacity + 2);
        await AssertTokenAsync(otherClient, HttpStatusCode.Unauthorized, "invalid_client");
        // Unauthenticated token requests never charge their attacker-submitted ip_address or email.
        var ipv6 = IPAddress.Parse(proxy).AddressFamily == AddressFamily.InterNetworkV6 && !IPAddress.Parse(proxy).IsIPv4MappedToIPv6;
        await Assert.That(await target.CountAsync(AttemptLimiter.Collection)).IsEqualTo(ipv6 ? 5 : 3);

        clock.Advance(TimeSpan.FromSeconds(55));
        using var renewed = await InvalidClientAsync(first, secondAddress);
        await AssertTokenAsync(renewed, HttpStatusCode.Unauthorized, "invalid_client");
    }

    [Test]
    [Arguments(null, "127.0.0.1", "rightmost", false)]
    [Arguments(null, "::1", "rightmost", false)]
    [Arguments(Proxy, "127.0.0.1", "rightmost", false)]
    [Arguments(Proxy, "::1", "rightmost", false)]
    [Arguments(Proxy, "10.0.0.2", "rightmost", false)]
    [Arguments(Proxy, Proxy, "rightmost|missing|malformed|empty|trailing|quoted-empty|quoted-whitespace|duplicate-rightmost|duplicate-empty|duplicate-blank|duplicate-whitespace", true)]
    public async Task Only_an_exact_trusted_peer_can_supply_one_rightmost_hop_and_invalid_tails_share_the_socket_budget(
        string? proxies, string socket, string header, bool trusted)
    {
        var target = await SharedStateTarget.CreateAsync(fixture);
        var clock = new ManualClock(Start);
        await using var server = await StartAsync(target, clock, new PeerAddress(IPAddress.Parse(socket)), proxies);
        foreach (var tailCase in header.Split('|'))
        {
            for (var attempt = 0; attempt <= Capacity; attempt++)
            {
                if (tailCase.StartsWith("duplicate-", StringComparison.Ordinal))
                {
                    var tail = tailCase switch
                    {
                        "duplicate-empty" => "\"\"",
                        "duplicate-blank" => string.Empty,
                        "duplicate-whitespace" => " \t ",
                        _ => "198.51.100.200",
                    };
                    var (status, retryAfter) = await RawHttp.DuplicateForwardedAsync(server.BaseAddress, TokenPath, $"203.0.113.{attempt + 1}", tail, attempt);
                    await Assert.That(status).IsEqualTo(attempt < Capacity ? HttpStatusCode.Unauthorized : HttpStatusCode.TooManyRequests);
                    await Assert.That(retryAfter).IsEqualTo(attempt < Capacity ? null : (TimeSpan?)TimeSpan.FromSeconds(55));
                    continue;
                }

                var forwarded = tailCase switch
                {
                    "missing" => null,
                    "empty" => string.Empty,
                    "malformed" => $"203.0.113.{attempt + 1}, not-an-ip",
                    "trailing" => $"203.0.113.{attempt + 1}, ",
                    "quoted-empty" => $"203.0.113.{attempt + 1}, \"\"",
                    "quoted-whitespace" => $"203.0.113.{attempt + 1}, \" \t \"",
                    // A forged leftmost hop varies; exactly the rightmost hop must be used.
                    _ => $"203.0.113.{attempt + 1}, 198.51.100.200",
                };
                using var response = await InvalidClientAsync(server, forwarded, attempt);
                await AssertTokenAsync(response, attempt < Capacity ? HttpStatusCode.Unauthorized : HttpStatusCode.TooManyRequests,
                    attempt < Capacity ? "invalid_client" : "rate_limited", attempt < Capacity ? null : TimeSpan.FromSeconds(55));
            }

            var perGuard = IPAddress.Parse(socket).AddressFamily == AddressFamily.InterNetworkV6 ? 2 : 1;
            await Assert.That(await target.CountAsync(AttemptLimiter.Collection)).IsEqualTo(perGuard * (trusted ? 2 : 1));
            // Each parser case starts with empty buckets on the same isolated test database.
            foreach (var bucket in (await target.Database.ListDocumentsAsync(AttemptLimiter.Collection)).Items)
            {
                await target.Database.DeleteDocumentAsync(AttemptLimiter.Collection, bucket.Id);
            }

            clock.Advance(TimeSpan.FromMinutes(1));
        }
    }

    [Test]
    [Arguments(false, Capacity)]
    [Arguments(true, Capacity)]
    [Arguments(false, int.MaxValue)]
    public async Task Proxy_aggregate_and_IPv6_site_budgets_stop_rotating_clients(bool ipv6Site, int capacity)
    {
        var target = await SharedStateTarget.CreateAsync(fixture);
        var clock = new ManualClock(Start);
        const string secondProxy = "10.0.0.2";
        await using var first = await StartAsync(target, clock, new PeerAddress(IPAddress.Parse(Proxy)), Proxy + ";" + secondProxy, capacity);
        await using var second = await StartAsync(target, clock, new PeerAddress(IPAddress.Parse(ipv6Site ? secondProxy : Proxy)), Proxy + ";" + secondProxy, capacity);
        var limiter = first.App.Services.GetRequiredService<AttemptLimiter>();
        var factor = ipv6Site ? HostedAuth.SiteFactor : TokenRateLimitMiddleware.ProxyFactor;
        var aggregateCapacity = (int)Math.Min((long)capacity * factor, int.MaxValue);
        const int saturationClientCount = 4;
        var neededClients = capacity == int.MaxValue ? saturationClientCount : (ipv6Site ? factor : aggregateCapacity) + 1;
        var clients = Clients(limiter, ipv6Site).Take(neededClients).ToArray();
        if (capacity == int.MaxValue)
        {
            using var initial = await InvalidClientAsync(first, clients[0]);
            await AssertTokenAsync(initial, HttpStatusCode.Unauthorized, "invalid_client");
            using var another = await InvalidClientAsync(second, clients[1]);
            await AssertTokenAsync(another, HttpStatusCode.Unauthorized, "invalid_client");
            var peer = (await target.Database.ListDocumentsAsync(AttemptLimiter.Collection)).Items
                .Single(item => item.Document.GetProperty("used").GetInt32() == 2);
            await target.Database.PutDocumentAsync(AttemptLimiter.Collection, peer.Id,
                JsonSerializer.SerializeToElement(new { window = Start.ToUnixTimeSeconds() / 60, used = int.MaxValue - 1 }), TerraBasePrecondition.Matches(peer.Version));
            using var last = await InvalidClientAsync(first, clients[2]);
            await AssertTokenAsync(last, HttpStatusCode.Unauthorized, "invalid_client");
            using var saturated = await InvalidClientAsync(second, clients[3]);
            await AssertTokenAsync(saturated, HttpStatusCode.TooManyRequests, "rate_limited", TimeSpan.FromSeconds(55));
            return;
        }

        for (var attempt = 0; attempt < aggregateCapacity; attempt++)
        {
            // Different proxy peers keep the /48 test below either proxy's own aggregate cap.
            using var invalid = await InvalidClientAsync(attempt % 2 == 0 ? first : second,
                clients[ipv6Site ? attempt / capacity : attempt], attempt);
            await AssertTokenAsync(invalid, HttpStatusCode.Unauthorized, "invalid_client");
        }

        var before = await target.CountAsync(AttemptLimiter.Collection);
        using var exhausted = await InvalidClientAsync(second, clients[ipv6Site ? factor : aggregateCapacity], aggregateCapacity);
        await AssertTokenAsync(exhausted, HttpStatusCode.TooManyRequests, "rate_limited", TimeSpan.FromSeconds(55));
        await Assert.That(await target.CountAsync(AttemptLimiter.Collection)).IsEqualTo(before + (ipv6Site ? 1 : 0));
        if (ipv6Site)
        {
            var usedFolds = clients.Select(limiter.Fold).ToHashSet(StringComparer.Ordinal);
            using var otherSite = await InvalidClientAsync(first, Clients(limiter, ipv6: false).First(address => !usedFolds.Contains(limiter.Fold(address))));
            await AssertTokenAsync(otherSite, HttpStatusCode.Unauthorized, "invalid_client");
        }
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task Trusted_and_ordinary_peers_in_one_IPv6_prefix_exhaust_independent_aggregate_budgets_across_replicas(
        bool ipv6Site, bool trustedFirst)
    {
        var target = await SharedStateTarget.CreateAsync(fixture);
        var clock = new ManualClock(Start);
        var configuredProxies = Enumerable.Range(1, 8).Select(number => $"2001:db8:704:{number:x}::1").ToArray();
        var firstPeer = new PeerAddress(IPAddress.Parse(configuredProxies[0]));
        var secondPeer = new PeerAddress(IPAddress.Parse(configuredProxies[0]));
        var proxies = string.Join(';', configuredProxies);
        await using var first = await StartAsync(target, clock, firstPeer, proxies);
        await using var second = await StartAsync(target, clock, secondPeer, proxies);
        var replicas = new[] { (Server: first, Peer: firstPeer), (Server: second, Peer: secondPeer) };
        var limiter = first.App.Services.GetRequiredService<AttemptLimiter>();
        var peerCount = ipv6Site ? HostedAuth.SiteFactor + 1 : 1;
        var trustedPeers = configuredProxies.DistinctBy(address => limiter.Fold(HostedAuth.Subject(address)!))
            .Take(peerCount).Select(IPAddress.Parse).ToArray();
        var trustedFolds = trustedPeers.Select(address => limiter.Fold(HostedAuth.Subject(address.ToString())!)).ToHashSet(StringComparer.Ordinal);
        // Separate /64s isolate the shared /48; a fresh final /64 must reach the site guard.
        var ordinaryPeers = ipv6Site
            ? Clients(limiter, ipv6: true).Where(address => !trustedFolds.Contains(limiter.Fold(address)))
                .Take(peerCount).Select(address => IPAddress.Parse(address + "2")).ToArray()
            : [IPAddress.Parse(HostedAuth.Subject(trustedPeers[0].ToString()) + "2")];
        var siteFactor = ipv6Site ? HostedAuth.SiteFactor : 1;
        var proxyCapacity = Capacity * TokenRateLimitMiddleware.ProxyFactor * siteFactor;
        var clients = Clients(limiter, ipv6: false).Take(proxyCapacity + replicas.Length).ToArray();
        await Assert.That(trustedPeers.Length).IsEqualTo(peerCount);
        await Assert.That(ordinaryPeers.Length).IsEqualTo(peerCount);
        await Assert.That(clients.Length).IsEqualTo(proxyCapacity + replicas.Length);

        foreach (var trusted in new[] { trustedFirst, !trustedFirst })
        {
            var peerCapacity = Capacity * (trusted ? TokenRateLimitMiddleware.ProxyFactor : 1);
            var aggregateCapacity = peerCapacity * siteFactor;
            var peers = trusted ? trustedPeers : ordinaryPeers;
            for (var attempt = 0; attempt < aggregateCapacity + replicas.Length; attempt++)
            {
                var (server, peer) = replicas[attempt % replicas.Length];
                peer.Address = peers[ipv6Site ? attempt / peerCapacity : 0];
                // Rotate forwarded clients even for ordinary peers: forged headers cannot buy proxy capacity.
                using var response = await InvalidClientAsync(server, clients[attempt], attempt);
                await AssertTokenAsync(response, attempt < aggregateCapacity ? HttpStatusCode.Unauthorized : HttpStatusCode.TooManyRequests,
                    attempt < aggregateCapacity ? "invalid_client" : "rate_limited", attempt < aggregateCapacity ? null : TimeSpan.FromSeconds(55));
            }
        }

        clock.Advance(TimeSpan.FromSeconds(55));
        foreach (var (server, peer) in replicas)
        {
            foreach (var peers in new[] { trustedPeers, ordinaryPeers })
            {
                peer.Address = peers[0];
                using var renewed = await InvalidClientAsync(server, clients[0]);
                await AssertTokenAsync(renewed, HttpStatusCode.Unauthorized, "invalid_client");
            }
        }
    }

    [Test]
    [Arguments("socket-storage")]
    [Arguments("missing-peer")]
    [Arguments("client-bucket")]
    public async Task Missing_peers_and_uncheckable_socket_or_client_budgets_fail_closed_before_client_authentication(string failure)
    {
        var target = await SharedStateTarget.CreateAsync(fixture);
        await using var server = await StartAsync(target, new ManualClock(Start),
            new PeerAddress(failure == "missing-peer" ? null : IPAddress.Parse(Proxy)), rejectedStorageKey: failure == "socket-storage");
        var clients = Clients(server.App.Services.GetRequiredService<AttemptLimiter>(), ipv6: false).Take(2).ToArray();
        if (failure == "client-bucket")
        {
            using var first = await InvalidClientAsync(server, clients[0]);
            await AssertTokenAsync(first, HttpStatusCode.Unauthorized, "invalid_client");
            var before = (await target.Database.ListDocumentsAsync(AttemptLimiter.Collection)).Items.Select(item => item.Id).ToHashSet();
            using var second = await InvalidClientAsync(server, clients[1]);
            await AssertTokenAsync(second, HttpStatusCode.Unauthorized, "invalid_client");
            var client = (await target.Database.ListDocumentsAsync(AttemptLimiter.Collection)).Items.Single(item => !before.Contains(item.Id));
            await target.Database.PutDocumentAsync(AttemptLimiter.Collection, client.Id,
                JsonSerializer.SerializeToElement(new { window = -1, used = 1 }), TerraBasePrecondition.Matches(client.Version));
        }

        using var unavailable = await InvalidClientAsync(server, clients[1]);
        await AssertTokenAsync(unavailable, HttpStatusCode.ServiceUnavailable, "temporarily_unavailable", TimeSpan.FromSeconds(2));
        var buckets = (await target.Database.ListDocumentsAsync(AttemptLimiter.Collection)).Items;
        await Assert.That(buckets.Count).IsEqualTo(failure == "client-bucket" ? 3 : 0);
        if (failure == "client-bucket")
        {
            // The socket guard admitted this attempt, but the damaged resolved-client guard refused it.
            await Assert.That(buckets.Count(item => item.Document.GetProperty("used").GetInt32() == 3)).IsEqualTo(1);
        }

        using var health = await server.Client.GetAsync("/healthz");
        await Assert.That(health.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    [Arguments(false, Proxy)]
    [Arguments(true, Proxy)]
    [Arguments(false, null)]
    [Arguments(true, null)]
    [Arguments(false, "10.0.0.2")]
    [Arguments(true, "10.0.0.2")]
    public async Task Hosted_request_and_verify_limits_use_only_trusted_IPs_and_spoofed_host_and_proto_never_change_the_issuer(bool verify, string? proxies)
    {
        var clock = new ManualClock(Start);
        var trusted = proxies == Proxy;
        var socket = trusted ? IPAddress.Parse(Proxy) : IPAddress.Loopback;
        await using var oidc = await OidcHarness.StartAsync(fixture, values =>
        {
            if (proxies is not null)
            {
                values["trusted-proxies"] = proxies;
            }
        }, clock: clock, services: services => services.AddSingleton<IStartupFilter>(new PeerAddress(socket)));
        var limiter = oidc.Server.App.Services.GetRequiredService<AttemptLimiter>();
        var clients = Clients(limiter, ipv6: false).Where(address => limiter.Fold(address) != limiter.Fold(socket.ToString())).Take(2).ToArray();
        var maximum = verify ? HostedAuth.PerIp : HostedAuth.MailPerIp;
        // Distinct email IDs can share the keyed email cooldown bucket. Isolate the IP guard from those collisions,
        // including requests sent over raw HTTP for duplicate forwarding headers.
        var attempts = Enumerable.Range(0, 65_536).DistinctBy(attempt => limiter.Fold($"unknown-{attempt}@example.test"))
            .Take(maximum * 2 + 4).ToArray();
        for (var attempt = 0; attempt < maximum; attempt++)
        {
            using var response = await HostedAsync(oidc.Server, clients[0], verify, attempts[attempt]);
            await Assert.That(response.StatusCode).IsEqualTo(verify ? HttpStatusCode.Unauthorized : HttpStatusCode.OK);
        }

        using var limited = await HostedAsync(oidc.Server, clients[0], verify, attempts[maximum]);
        await Assert.That(limited.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
        await Assert.That(limited.Headers.RetryAfter?.Delta).IsEqualTo((verify ? HostedAuth.IpInterval : HostedAuth.MailInterval));
        await Assert.That(limited.Content.Headers.ContentType?.MediaType).IsEqualTo("text/html");
        using var independent = await HostedAsync(oidc.Server, clients[1], verify, attempts[maximum + 1]);
        await Assert.That(independent.StatusCode).IsEqualTo(trusted ? verify ? HttpStatusCode.Unauthorized : HttpStatusCode.OK : HttpStatusCode.TooManyRequests);

        if (trusted)
        {
            // A missing final hop must use the socket, never the preceding, already exhausted client.
            // Mix literal/quoted empty tails and actual repeated header lines into one socket allowance.
            var path = "/ui/terrascale/sign-in" + (verify ? "/verify" : string.Empty);
            var expected = verify ? HttpStatusCode.Unauthorized : HttpStatusCode.OK;
            for (var attempt = 0; attempt < maximum; attempt++)
            {
                if (attempt % 6 >= 3)
                {
                    var tail = (attempt % 6) switch { 3 => "\" \t \"", 4 => string.Empty, _ => " \t " };
                    var (status, _) = await RawHttp.DuplicateForwardedAsync(oidc.Server.BaseAddress, path, clients[0], tail, attempts[maximum + 2 + attempt]);
                    await Assert.That(status).IsEqualTo(expected);
                }
                else
                {
                    var tail = (attempt % 6) switch { 0 => string.Empty, 1 => "\"\"", _ => "\" \t \"" };
                    using var fallback = await HostedAsync(oidc.Server, clients[0], verify, attempts[maximum + 2 + attempt], tail);
                    await Assert.That(fallback.StatusCode).IsEqualTo(expected);
                }
            }

            using var socketLimited = await HostedAsync(oidc.Server, clients[0], verify, attempts[maximum * 2 + 2], "\"\"");
            await Assert.That(socketLimited.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
            await Assert.That(socketLimited.Headers.RetryAfter?.Delta).IsEqualTo((verify ? HostedAuth.IpInterval : HostedAuth.MailInterval));
            foreach (var tail in new[] { "\"\"", string.Empty, " \t " })
            {
                var (duplicateLimited, duplicateDelay) = await RawHttp.DuplicateForwardedAsync(oidc.Server.BaseAddress, path, clients[1], tail, attempts[maximum * 2 + 3]);
                await Assert.That(duplicateLimited).IsEqualTo(HttpStatusCode.TooManyRequests);
                await Assert.That(duplicateDelay).IsEqualTo((verify ? HostedAuth.IpInterval : HostedAuth.MailInterval));
            }
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, "/terrascale/.well-known/openid-configuration");
        SpoofHeaders(request, 0);
        request.Headers.Add("X-Forwarded-For", clients[1]);
        using var discovery = await oidc.Client.SendAsync(request);
        await Assert.That(discovery.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var document = await OidcHarness.JsonAsync(discovery);
        await Assert.That(document.GetProperty("issuer").GetString()).IsEqualTo(OidcHarness.Issuer);
        await Assert.That(document.GetProperty("token_endpoint").GetString()).IsEqualTo(OidcHarness.Issuer + "/oauth/token");
    }

    private static IEnumerable<string> Clients(AttemptLimiter limiter, bool ipv6) =>
        Enumerable.Range(1, 254).Select(number => ipv6 ? $"2001:db8:704:{number:x}::" : $"203.0.113.{number}")
            .DistinctBy(limiter.Fold);

    private static Task<AuthTestServer> StartAsync(SharedStateTarget target, TimeProvider clock, PeerAddress peer, string? proxies = Proxy,
        int capacity = Capacity, bool rejectedStorageKey = false) => AuthTestServer.StartAsync(values =>
        {
            values["master-key"] = MasterKey;
            values["rate-limit"] = capacity.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (proxies is not null)
            {
                values["trusted-proxies"] = proxies;
            }
        }, services =>
        {
            target.Register(services, rejectedStorageKey ? "tb_invalid_proxy_test_storage_key" : null);
            services.AddSingleton(clock);
            services.AddSingleton<IStartupFilter>(peer);
        });

    private static async Task<HttpResponseMessage> InvalidClientAsync(AuthTestServer server, string? forwarded, int attempt = 0)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenPath)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = "invalid",
                ["email"] = $"unknown-{attempt}@example.test",
                ["ip_address"] = $"192.0.2.{attempt + 1}",
            }),
        };
        var client = attempt % 2 == 0 ? OidcIssuer.DashboardClientId : $"unknown-client-{attempt}";
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{client}:wrong-secret-{attempt}")));
        SpoofHeaders(request, attempt);
        if (forwarded is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Forwarded-For", forwarded);
        }

        return await server.Client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> HostedAsync(AuthTestServer server, string client, bool verify, int attempt, string? tail = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/ui/terrascale/sign-in" + (verify ? "/verify" : string.Empty))
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["email"] = $"unknown-{attempt}@example.test", ["code"] = "000000" }),
        };
        request.Headers.Add("Origin", OidcHarness.PublicOrigin);
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", tail is null ? $"198.51.100.{attempt + 1}, {client}" : $"{client}, {tail}");
        SpoofHeaders(request, attempt);
        return await server.Client.SendAsync(request);
    }

    private static void SpoofHeaders(HttpRequestMessage request, int attempt)
    {
        request.Headers.Host = "spoofed.example.invalid";
        request.Headers.Add("Forwarded", $"for=192.0.2.{attempt + 1};host=spoofed.example.invalid;proto=http");
        request.Headers.Add("X-Real-IP", $"198.51.100.{attempt + 1}");
        request.Headers.Add("X-Forwarded-Host", "spoofed.example.invalid");
        request.Headers.Add("X-Forwarded-Proto", "http");
    }

    private static async Task AssertTokenAsync(HttpResponseMessage response, HttpStatusCode status, string error, TimeSpan? delay = null)
    {
        await Assert.That(response.StatusCode).IsEqualTo(status);
        await Assert.That(await OidcHarness.ErrorAsync(response)).IsEqualTo(error);
        await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo("application/json");
        await Assert.That(response.Headers.CacheControl!.NoStore).IsTrue();
        await Assert.That(response.Headers.RetryAfter?.Delta).IsEqualTo(delay);
    }

    // Test-only socket metadata; forwarding, client authentication and TerraBase remain real.
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
