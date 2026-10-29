using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.RateLimiting;
using TerraScale.Authentication.Store;
using TerraScale.Authentication.Tests;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Authentication.Slow.Platform;

public sealed class ConfigurationTests
{
    private const string TrustedProxiesError = "TS_AUTH_TRUSTED_PROXIES must contain unadorned numeric IP addresses";
    private const string UnspecifiedProxiesError = "TS_AUTH_TRUSTED_PROXIES must not contain unspecified addresses";
    private const string DuplicateProxiesError = "TS_AUTH_TRUSTED_PROXIES must contain distinct normalized addresses";
    private const string TooManyProxiesError = "TS_AUTH_TRUSTED_PROXIES must contain at most 8 addresses";

    [Test]
    [Arguments("master-key", "TS_AUTH_MASTER_KEY is required")]
    public async Task Startup_refuses_missing_required_settings(string key, string message)
    {
        var exception = await Assert.ThrowsAsync<AuthConfigurationException>(() => AuthTestServer.StartAsync(settings: values => values.Remove(key)));

        await Assert.That(exception!.Message).IsEqualTo(message);
    }

    [Test]
    public async Task Production_requires_an_https_public_origin()
    {
        var exception = await Assert.ThrowsAsync<AuthConfigurationException>(() => AuthTestServer.StartAsync(settings: values =>
        {
            values.Remove("environment");
            values["public-origin"] = "http://auth.example.invalid";
        }));

        await Assert.That(exception!.Message).IsEqualTo("TS_AUTH_PUBLIC_ORIGIN must use HTTPS outside Development");
    }

    [Test]
    [Arguments("not a dsn secretvalue")]
    [Arguments("https://glitchtip.example/secretvalue")]
    [Arguments("ftp://secretvalue@glitchtip.example/3")]
    [Arguments("https://:secretvalue@glitchtip.example/3")]
    public async Task The_service_binary_rejects_a_malformed_dsn_without_echoing_it(string dsn)
    {
        var settings = AuthTestServer.DefaultSettings();
        settings["sentry-dsn"] = dsn;
        var (exitCode, output) = await RunBinaryAsync("--rate-limit=1000", settings);

        await Assert.That(exitCode).IsEqualTo(2);
        await Assert.That(output).Contains("TS_AUTH_SENTRY_DSN must be a valid DSN");
        await Assert.That(output).DoesNotContain("secretvalue");
        await Assert.That(output).DoesNotContain("   at ");
    }

    [Test]
    public async Task Production_requires_an_explicit_storage_url()
    {
        // Only Development may use the implicit loopback TerraBase.
        var exception = await Assert.ThrowsAsync<AuthConfigurationException>(() => AuthTestServer.StartAsync(settings: values => values.Remove("environment")));
        var settings = AuthTestServer.DefaultSettings();
        settings.Remove("environment");
        var (exitCode, output) = await RunBinaryAsync("--rate-limit=1000", settings);

        await Assert.That(exception!.Message).IsEqualTo(StorageUrlRequired);
        await Assert.That(exitCode).IsEqualTo(2);
        await Assert.That(output).Contains(StorageUrlRequired);
        await Assert.That(output).DoesNotContain("   at ");
    }

    [Test]
    public async Task Development_without_a_storage_url_uses_the_loopback_terrabase()
    {
        var sent = new System.Collections.Concurrent.ConcurrentQueue<Uri>();
        await using var server = await AuthTestServer.StartAsync(services: services =>
            services.AddSingleton(new HttpClient(new RecordingTransport(sent))));

        await Assert.That(await ReadinessWait.UntilAsync(() => Task.FromResult(!sent.IsEmpty), TimeSpan.FromSeconds(15))).IsTrue();
        await Assert.That(sent.Select(uri => uri.GetLeftPart(UriPartial.Authority)).Distinct()).IsEquivalentTo(["http://127.0.0.1:8081"]);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task An_explicit_url_receives_all_regional_storage_requests(bool development)
    {
        const string storage = "http://127.0.0.1:18081";
        var reads = new System.Collections.Concurrent.ConcurrentQueue<Uri>();
        await using var server = await AuthTestServer.StartAsync(
            settings: values =>
            {
                if (!development)
                {
                    values.Remove("environment");
                    values["cloudflare-account-id"] = "0123456789abcdef0123456789abcdef";
                    values["cloudflare-email-token"] = "synthetic-email-token";
                }

                values["terrabase-url"] = storage;
            },
            services: services =>
            {
                services.AddSingleton(new HttpClient(new RecordingTransport(reads)));
            });

        // The JWKS reads signing keys (a GET); startup provisioning writes.
        using var jwks = await server.Client.GetAsync($"/{OidcIssuer.Slug}/.well-known/jwks.json");
        await Assert.That(await ReadinessWait.UntilAsync(() => Task.FromResult(!reads.IsEmpty), TimeSpan.FromSeconds(15))).IsTrue();
        await Assert.That(reads.Select(uri => uri.GetLeftPart(UriPartial.Authority)).Distinct()).IsEquivalentTo([storage]);
    }

    [Test]
    public async Task Production_requires_an_audit_signing_key()
    {
        var exception = await Assert.ThrowsAsync<AuthConfigurationException>(() => AuthTestServer.StartAsync(settings: values =>
        {
            values.Remove("environment");
            values.Remove("audit-signing-key");
        }));

        await Assert.That(exception!.Message).IsEqualTo("TS_AUTH_AUDIT_SIGNING_KEY is required outside Development");
    }

    [Test]
    [Arguments("master-key", "c2hvcnQ=", "TS_AUTH_MASTER_KEY must decode to 32 bytes")]
    [Arguments("master-key", "not base64!", "TS_AUTH_MASTER_KEY must be base64")]
    [Arguments("public-origin", "https://auth.example/?next=x", "TS_AUTH_PUBLIC_ORIGIN must be an absolute HTTP(S) URL")]
    [Arguments("public-origin", "https://auth.example/subpath", "TS_AUTH_PUBLIC_ORIGIN must be an origin without a path")]
    [Arguments("listen", "https://0.0.0.0:8080", "TS_AUTH_LISTEN must be a plain HTTP listen URL")]
    [Arguments("listen", "http://[::1]", "TS_AUTH_LISTEN must use a host and port")]
    [Arguments("listen", "http://127.0.0.1", "TS_AUTH_LISTEN must use a host and port")]
    [Arguments("listen", "http://auth.internal:8080", "TS_AUTH_LISTEN must use a host and port")]
    [Arguments("listen", "http://127.0.0.1:65536", "TS_AUTH_LISTEN must use a host and port")]
    [Arguments("cloudflare-account-id", "0123456789abcdef0123456789abcdef", "CLOUDFLARE_ACCOUNT_ID and CLOUDFLARE_EMAIL_TOKEN must be set together")]
    [Arguments("rate-limit", "0", "TS_AUTH_RATE_LIMIT must be positive")]
    [Arguments("trusted-proxies", "proxy.example.invalid", TrustedProxiesError)]
    [Arguments("trusted-proxies", "10.0.0.0/24", TrustedProxiesError)]
    [Arguments("trusted-proxies", "10.0.0.1:443", TrustedProxiesError)]
    [Arguments("trusted-proxies", "[2001:db8::1]:443", TrustedProxiesError)]
    [Arguments("trusted-proxies", "[2001:db8::1]", TrustedProxiesError)]
    [Arguments("trusted-proxies", "0.0.0.0", UnspecifiedProxiesError)]
    [Arguments("trusted-proxies", "::", UnspecifiedProxiesError)]
    [Arguments("trusted-proxies", "::ffff:0.0.0.0", UnspecifiedProxiesError)]
    [Arguments("trusted-proxies", "fe80::1%2", TrustedProxiesError)]
    [Arguments("trusted-proxies", "10.1", TrustedProxiesError)]
    [Arguments("trusted-proxies", "167772161", TrustedProxiesError)]
    [Arguments("trusted-proxies", "0x0a000001", TrustedProxiesError)]
    [Arguments("trusted-proxies", "010.0.0.1", TrustedProxiesError)]
    [Arguments("trusted-proxies", "::ffff:010.0.0.1", TrustedProxiesError)]
    [Arguments("trusted-proxies", "10.0.0.256", TrustedProxiesError)]
    [Arguments("trusted-proxies", "10.0.0.1;", TrustedProxiesError)]
    [Arguments("trusted-proxies", "10.0.0.1;;10.0.0.2", TrustedProxiesError)]
    [Arguments("trusted-proxies", "10.0.0.1;10.0.0.1", DuplicateProxiesError)]
    [Arguments("trusted-proxies", "10.0.0.1;::ffff:10.0.0.1", DuplicateProxiesError)]
    [Arguments("trusted-proxies", "2001:db8::1;2001:0db8:0000:0000:0000:0000:0000:0001", DuplicateProxiesError)]
    [Arguments("trusted-proxies", "10.0.0.1;10.0.0.2;10.0.0.3;10.0.0.4;10.0.0.5;10.0.0.6;10.0.0.7;10.0.0.8;10.0.0.9", TooManyProxiesError)]
    public async Task Startup_rejects_malformed_settings(string key, string value, string message)
    {
        var exception = await Assert.ThrowsAsync<AuthConfigurationException>(() => AuthTestServer.StartAsync(settings: values => values[key] = value));

        await Assert.That(exception!.Message).IsEqualTo(message);
    }

    private const string DashboardSecret = "dashboard-config-secret-0123456789abcdef";

    [Test]
    [Arguments(DashboardSecret, null, null, "TS_AUTH_DASHBOARD_CLIENT_SECRET and TS_AUTH_DASHBOARD_REDIRECT_URIS are required together")]
    [Arguments(null, "https://dash.example/cb", null, "TS_AUTH_DASHBOARD_CLIENT_SECRET and TS_AUTH_DASHBOARD_REDIRECT_URIS are required together")]
    [Arguments(null, null, "https://dash.example/out", "TS_AUTH_DASHBOARD_CLIENT_SECRET and TS_AUTH_DASHBOARD_REDIRECT_URIS are required together")]
    [Arguments("too-short-dashboard-secret", "https://dash.example/cb", null, "TS_AUTH_DASHBOARD_CLIENT_SECRET must contain 32 to 512 characters without whitespace or controls")]
    [Arguments("dashboard secret with spaces 0123456789", "https://dash.example/cb", null, "TS_AUTH_DASHBOARD_CLIENT_SECRET must contain 32 to 512 characters without whitespace or controls")]
    [Arguments(DashboardSecret, "http://dash.example/cb", null, "TS_AUTH_DASHBOARD_REDIRECT_URIS must hold absolute HTTPS URIs (HTTP only for loopback) without credentials or fragments")]
    [Arguments(DashboardSecret, "https://dash.example/cb#fragment", null, "TS_AUTH_DASHBOARD_REDIRECT_URIS must hold absolute HTTPS URIs (HTTP only for loopback) without credentials or fragments")]
    [Arguments(DashboardSecret, "https://user:pass@dash.example/cb", null, "TS_AUTH_DASHBOARD_REDIRECT_URIS must hold absolute HTTPS URIs (HTTP only for loopback) without credentials or fragments")]
    [Arguments(DashboardSecret, "/relative/cb", null, "TS_AUTH_DASHBOARD_REDIRECT_URIS must hold absolute HTTPS URIs (HTTP only for loopback) without credentials or fragments")]
    [Arguments(DashboardSecret, "https://a.example/1;https://a.example/2;https://a.example/3;https://a.example/4;https://a.example/5;https://a.example/6;https://a.example/7;https://a.example/8;https://a.example/9;https://a.example/10;https://a.example/11", null, "TS_AUTH_DASHBOARD_REDIRECT_URIS may contain at most 10 URIs")]
    [Arguments(DashboardSecret, "https://dash.example/cb", "http://dash.example/out", "TS_AUTH_DASHBOARD_POST_LOGOUT_REDIRECT_URIS must hold absolute HTTPS URIs (HTTP only for loopback) without credentials or fragments")]
    public async Task The_dashboard_client_settings_are_validated_together(string? secret, string? redirects, string? postLogoutRedirects, string message)
    {
        var exception = await Assert.ThrowsAsync<AuthConfigurationException>(() => AuthTestServer.StartAsync(settings: values =>
        {
            foreach (var (key, value) in new[] { ("dashboard-client-secret", secret), ("dashboard-redirect-uris", redirects), ("dashboard-post-logout-redirect-uris", postLogoutRedirects) })
            {
                if (value is not null)
                {
                    values[key] = value;
                }
            }
        }));

        await Assert.That(exception!.Message).IsEqualTo(message);
    }

    private const string ArgumentSecret = "sktestvaluethatmustnotbeprinted";

    [Test]
    [Arguments("--database-nam=" + ArgumentSecret, "Unknown option (expected --key=value with a TS_AUTH_* setting name)")]
    [Arguments("--correct-horse-battery-staple-" + ArgumentSecret + "=x", "Unknown option (expected --key=value with a TS_AUTH_* setting name)")]
    [Arguments("--master-key:" + ArgumentSecret, "Malformed option (expected --key=value)")]
    [Arguments("--" + ArgumentSecret, "Malformed option (expected --key=value)")]
    [Arguments("--master-key:" + ArgumentSecret + "=x", "Unknown option (expected --key=value with a TS_AUTH_* setting name)")]
    public async Task The_service_binary_rejects_bad_options_without_echoing_their_values(string argument, string message)
    {
        var (exitCode, output) = await RunBinaryAsync(argument);

        await Assert.That(exitCode).IsEqualTo(2);
        await Assert.That(output).Contains(message);
        await Assert.That(output).DoesNotContain(ArgumentSecret);
    }

    [Test]
    public async Task The_service_binary_reports_configuration_errors_without_values_or_a_stack_trace()
    {
        var (exitCode, output) = await RunBinaryAsync("--master-key=not-base64!", AuthTestServer.DefaultSettings());

        await Assert.That(exitCode).IsEqualTo(2);
        await Assert.That(output).Contains("TS_AUTH_MASTER_KEY must be base64");
        await Assert.That(output).DoesNotContain("not-base64!");
        await Assert.That(output).DoesNotContain("   at ");
    }

    [Test]
    public async Task Command_line_options_override_the_environment()
    {
        var settings = AuthTestServer.DefaultSettings();
        settings["public-origin"] = "https://env.example.invalid";
        await using var service = await RunningBinary.StartAsync(settings, "--public-origin=https://cli.example.invalid");

        using var response = await service.Client.GetAsync("/openapi/v1.json");
        var document = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(await response.Content.ReadAsStringAsync());

        await Assert.That(document.GetProperty("servers")[0].GetProperty("url").GetString()).IsEqualTo("https://cli.example.invalid");
    }

    [Test]
    [Arguments(null, false)]
    [Arguments("10.0.0.1;2001:db8::1;::ffff:10.0.0.2", false)]
    [Arguments("10.0.0.1;10.0.0.2;10.0.0.3;10.0.0.4;10.0.0.5;10.0.0.6;10.0.0.7;10.0.0.8", false)]
    [Arguments("10.0.0.1;2001:db8::1", true)]
    public async Task The_service_binary_accepts_numeric_proxy_settings_from_the_environment_or_command_line(string? proxies, bool commandLine)
    {
        var settings = AuthTestServer.DefaultSettings();
        if (proxies is not null)
        {
            settings["trusted-proxies"] = commandLine ? "invalid-proxy.example.invalid" : proxies;
        }

        await using var service = await RunningBinary.StartAsync(settings, commandLine ? ["--trusted-proxies=" + proxies] : []);
        using var health = await service.Client.GetAsync("/healthz");
        await Assert.That(health.StatusCode).IsEqualTo(System.Net.HttpStatusCode.OK);
    }

    [Test]
    [ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
    public async Task Global_forwarding_flags_cannot_enable_untrusted_hops_bypass_empty_tails_or_remove_the_proxy_aggregate(TerraBaseFixture fixture)
    {
        const string path = "/terrascale/oauth/token";
        const int clientCapacity = 1;
        const int emptyTailAttempts = 2;
        var proxyCapacity = clientCapacity * TokenRateLimitMiddleware.ProxyFactor;
        var forwardedClientCount = proxyCapacity - emptyTailAttempts;
        foreach (var (proxies, listen) in new (string? Proxies, string Listen)[]
        {
            (null, "http://127.0.0.1:0"),
            ("10.0.0.1", "http://127.0.0.1:0"),
            ("127.0.0.1", "http://127.0.0.1:0"),
            // IPv4 connections to a dual-stack listener arrive as IPv4-mapped IPv6 peers.
            ("127.0.0.1", "http://[::]:0"),
        })
        {
            var scenario = $"trusted-proxies={proxies ?? "<unset>"}, listen={listen}";
            var trusted = proxies == "127.0.0.1";
            var target = await SharedStateTarget.CreateAsync(fixture);
            var settings = AuthTestServer.DefaultSettings();
            settings["rate-limit"] = clientCapacity.ToString(System.Globalization.CultureInfo.InvariantCulture);
            settings["listen"] = listen;
            if (proxies is not null)
            {
                settings["trusted-proxies"] = proxies;
            }

            // A persisted ahead-of-clock window cannot roll over during the live binary's bounded request sequence.
            var clock = new ManualClock(DateTimeOffset.UtcNow.AddDays(1));
            var limiter = new AttemptLimiter(new TerraBaseDocumentStore(new AuthStore(target.Database, clock)), clock, AuthOptions.Load(settings));
            var clients = Enumerable.Range(1, 254).Select(number => $"203.0.113.{number}")
                .Where(address => limiter.Fold(address) != limiter.Fold("127.0.0.1"))
                .DistinctBy(limiter.Fold).Take(forwardedClientCount + 1).ToArray();
            var subjects = new List<(string Purpose, string Subject)> { (trusted ? "token-proxy-ip" : "token-peer-ip", "127.0.0.1") };
            if (trusted)
            {
                subjects.Add(("token-client-ip", "127.0.0.1"));
                subjects.AddRange(clients.Take(forwardedClientCount).Select(subject => ("token-client-ip", subject)));
            }

            foreach (var (purpose, subject) in subjects)
            {
                await Assert.That(await limiter.TryAcquireAsync(OidcIssuer.InstanceId, purpose, limiter.Fold(subject), clientCapacity, CancellationToken.None)).IsTrue().Because(scenario);
            }

            foreach (var bucket in (await target.Database.ListDocumentsAsync(AttemptLimiter.Collection)).Items)
            {
                await target.Database.PutDocumentAsync(AttemptLimiter.Collection, bucket.Id,
                    JsonSerializer.SerializeToElement(new { window = bucket.Document.GetProperty("window").GetInt64(), used = 0 }), TerraBasePrecondition.Matches(bucket.Version));
            }

            // In process, so storage is the fixture: both spellings of ASP.NET's automatic forwarding flag are set.
            await using var service = await AuthTestServer.StartAsync(values =>
            {
                foreach (var (key, value) in settings)
                {
                    values[key] = value;
                }
            }, services => target.Register(services), configure: builder =>
            {
                builder.Configuration["ASPNETCORE_FORWARDEDHEADERS_ENABLED"] = "true";
                builder.Configuration["ForwardedHeaders_Enabled"] = "true";
            });

            var (first, _) = await RawHttp.DuplicateForwardedAsync(service.Client.BaseAddress!, path, clients[0], trusted ? string.Empty : clients[0], 0);
            await Assert.That(first).IsEqualTo(HttpStatusCode.Unauthorized).Because(scenario);
            var (second, delay) = await RawHttp.DuplicateForwardedAsync(service.Client.BaseAddress!, path, clients[1], trusted ? " \t " : clients[1], 1);
            await Assert.That(second).IsEqualTo(HttpStatusCode.TooManyRequests).Because(scenario);
            await Assert.That(delay).IsNotNull().Because(scenario);

            if (trusted)
            {
                // Empty final wire lines spent the socket's client allowance, neither preceding fake client's allowance.
                for (var attempt = 0; attempt < forwardedClientCount; attempt++)
                {
                    var (status, _) = await RawHttp.DuplicateForwardedAsync(service.Client.BaseAddress!, path, clients[attempt], clients[attempt], attempt + emptyTailAttempts);
                    await Assert.That(status).IsEqualTo(HttpStatusCode.Unauthorized).Because(scenario);
                }

                // The client-budget refusal also spent the original proxy's aggregate budget.
                var (limited, retryAfter) = await RawHttp.DuplicateForwardedAsync(service.Client.BaseAddress!, path, clients[forwardedClientCount], clients[forwardedClientCount], proxyCapacity);
                await Assert.That(limited).IsEqualTo(HttpStatusCode.TooManyRequests).Because(scenario);
                await Assert.That(retryAfter).IsNotNull().Because(scenario);
            }

            var buckets = (await target.Database.ListDocumentsAsync(AttemptLimiter.Collection)).Items;
            var expectedClientBuckets = trusted ? 1 + forwardedClientCount : 0;
            var expectedBucketCount = 1 + expectedClientBuckets;
            var expectedPeerUsed = trusted ? proxyCapacity : clientCapacity;
            var expectedUsed = expectedPeerUsed + expectedClientBuckets * clientCapacity;
            await Assert.That(buckets.Count).IsEqualTo(expectedBucketCount).Because(scenario);
            await Assert.That(buckets.All(bucket => bucket.Document.GetProperty("window").GetInt64() == clock.GetUtcNow().ToUnixTimeSeconds() / 60)).IsTrue().Because(scenario);
            await Assert.That(buckets.Sum(bucket => bucket.Document.GetProperty("used").GetInt32())).IsEqualTo(expectedUsed).Because(scenario);
            using var discoveryRequest = new HttpRequestMessage(HttpMethod.Get, "/terrascale/.well-known/openid-configuration");
            discoveryRequest.Headers.Host = "spoofed.example.invalid";
            discoveryRequest.Headers.Add("X-Forwarded-For", clients[forwardedClientCount]);
            discoveryRequest.Headers.Add("X-Forwarded-Host", "spoofed.example.invalid");
            discoveryRequest.Headers.Add("X-Forwarded-Proto", "https");
            using var discovery = await service.Client.SendAsync(discoveryRequest);
            await Assert.That(discovery.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(scenario);
            var document = await OidcHarness.JsonAsync(discovery);
            await Assert.That(document.GetProperty("issuer").GetString()).IsEqualTo(OidcHarness.Issuer).Because(scenario);
            await Assert.That(document.GetProperty("token_endpoint").GetString()).IsEqualTo(OidcHarness.Issuer + "/oauth/token").Because(scenario);
        }
    }

    [Test]
    public async Task Production_starts_against_the_terrabase_its_url_names()
    {
        // Production uses TS_AUTH_TERRABASE_URL alone to reach TerraBase's internal listener.
        var (started, unavailable) = await TerraBaseTestServer.StartProductionAsync();
        await using var terrabase = started ?? throw new InvalidOperationException(unavailable);
        var settings = AuthTestServer.DefaultSettings();
        settings.Remove("environment");
        settings["terrabase-url"] = terrabase.InternalAddress.GetLeftPart(UriPartial.Authority);
        settings["cloudflare-account-id"] = "0123456789abcdef0123456789abcdef";
        settings["cloudflare-email-token"] = "synthetic-email-token";
        // Secrets arrive as <NAME>_FILE with a trailing newline, as OCI Vault mounts them; each file wins over a malformed
        // variable, which would stop the start if it were read.
        var secrets = Directory.CreateTempSubdirectory("ts-auth-secrets-");
        try
        {
            var masterKey = Path.Combine(secrets.FullName, "master-key");
            var peerSecret = Path.Combine(secrets.FullName, "peer-secret");
            await File.WriteAllTextAsync(masterKey, settings["master-key"] + "\n");
            await File.WriteAllTextAsync(peerSecret, TerraBaseTestServer.PeerSecret + "\n");
            settings["master-key"] = "not-a-key";
            await using var service = await RunningBinary.StartAsync(settings, new Dictionary<string, string>
            {
                ["TS_REGION"] = "gru",
                ["TS_AUTH_MASTER_KEY_FILE"] = masterKey,
                ["TERRABASE_PEER_SECRET"] = "not-a-peer-secret",
                ["TERRABASE_PEER_SECRET_FILE"] = peerSecret,
            });

            using var health = await service.Client.GetAsync("/healthz");

            // Not /readyz: a production TerraBase opens its catalog only after reading it back from a reachable backup bucket.
            await Assert.That(health.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }
        finally
        {
            secrets.Delete(recursive: true);
        }
    }

    [Test]
    public async Task A_named_terrabase_without_the_peer_credential_refuses_to_start()
    {
        var exception = await Assert.ThrowsAsync<AuthConfigurationException>(() => AuthTestServer.StartAsync(
            settings: values => values["terrabase-url"] = "http://127.0.0.1:8081",
            configure: builder => builder.Configuration["TERRABASE_PEER_SECRET"] = string.Empty));

        await Assert.That(exception!.Message).IsEqualTo("TERRABASE_PEER_SECRET is required with TS_AUTH_TERRABASE_URL");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_named_terrabase_missing_or_malformed_peer_credential_refuses_start_without_echoing_it(bool malformed)
    {
        var secret = malformed ? TerraBaseTestServer.PeerSecret[..62] + "zz" : string.Empty;
        var exception = await Assert.ThrowsAsync<AuthConfigurationException>(() => AuthTestServer.StartAsync(
            settings: values =>
            {
                values.Remove("environment");
                values["terrabase-url"] = "http://127.0.0.1:8081";
            },
            configure: builder => builder.Configuration["TERRABASE_PEER_SECRET"] = secret));

        await Assert.That(exception!.Message).Contains("TERRABASE_PEER_SECRET");
        var settings = AuthTestServer.DefaultSettings();
        settings.Remove("environment");
        settings["terrabase-url"] = "http://127.0.0.1:8081";
        var (exitCode, output) = await RunBinaryAsync("--rate-limit=1000", settings, environment: new Dictionary<string, string>
        {
            ["TERRABASE_PEER_SECRET"] = secret,
            ["TERRABASE_PEER_SECRET_FILE"] = string.Empty,
        });

        await Assert.That(exitCode).IsEqualTo(2);
        await Assert.That(output).Contains(exception.Message);
        await Assert.That(output).DoesNotContain("   at ");
        if (malformed)
        {
            await Assert.That(exception.Message.Contains(secret, StringComparison.Ordinal)).IsFalse();
            await Assert.That(output.Contains(secret, StringComparison.Ordinal)).IsFalse();
        }
    }

    [Test]
    [Arguments("appsettings.json")]
    [Arguments("appsettings.Production.json")]
    public async Task Appsettings_cannot_replace_the_missing_peer_secret(string filename)
    {
        var directory = Directory.CreateTempSubdirectory("ts-auth-peer-");
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory.FullName, filename), $$"""{"TERRABASE_PEER_SECRET":"{{secret}}"}""");
            var settings = AuthTestServer.DefaultSettings();
            settings.Remove("environment");
            settings["terrabase-url"] = "http://127.0.0.1:8081";
            var (exitCode, output) = await RunBinaryAsync("--rate-limit=1000", settings, directory.FullName, new Dictionary<string, string>
            {
                ["DOTNET_ENVIRONMENT"] = "Production",
            });

            await Assert.That(exitCode).IsEqualTo(2);
            await Assert.That(output).Contains("TERRABASE_PEER_SECRET is required with TS_AUTH_TERRABASE_URL");
            await Assert.That(output).DoesNotContain(secret);
            await Assert.That(output).DoesNotContain("   at ");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>The real service binary on a free loopback port, with a controlled environment.</summary>
    private sealed class RunningBinary : IAsyncDisposable
    {
        private readonly Process process;

        private RunningBinary(Process process)
        {
            this.process = process;
            this.Client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        }

        public HttpClient Client { get; }

        // Log records are JSON lines, after an optional journald <N> prefix; the "Now listening on"
        // record carries its address as a value.
        private static string? ListeningAddress(string line)
        {
            var start = line.IndexOf('{', StringComparison.Ordinal);
            if (start < 0)
            {
                return null;
            }

            try
            {
                using var record = JsonDocument.Parse(line[start..]);
                return record.RootElement.TryGetProperty("msg", out var msg) && msg.GetString()?.Contains("Now listening on", StringComparison.Ordinal) == true
                    && Regex.Match(line, "https?://[^\"]+") is { Success: true } address
                    ? address.Value
                    : null;
            }
            catch (JsonException)
            {
                // A partial line; the next complete record carries the address.
                return null;
            }
        }

        public static Task<RunningBinary> StartAsync(Dictionary<string, string> settings, params string[] arguments) =>
            StartAsync(settings, [], arguments);

        public static async Task<RunningBinary> StartAsync(Dictionary<string, string> settings, Dictionary<string, string> environment, params string[] arguments)
        {
            // Port 0: the child binds a free port itself and reports it on its startup line,
            // so no other process can answer in its place.
            settings.TryAdd("listen", "http://127.0.0.1:0");
            var start = new ProcessStartInfo("dotnet", [Path.Combine(AppContext.BaseDirectory, "ts-auth.dll"), .. arguments])
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            foreach (var name in start.Environment.Keys.Where(key => key.StartsWith("TS_AUTH_", StringComparison.Ordinal) || key.StartsWith("CLOUDFLARE_", StringComparison.Ordinal)
                || key.StartsWith("OTEL_", StringComparison.Ordinal) || key.StartsWith("Logging__", StringComparison.Ordinal)).ToList())
            {
                start.Environment.Remove(name);
            }

            foreach (var (key, value) in settings)
            {
                start.Environment[$"TS_AUTH_{key.Replace('-', '_').ToUpperInvariant()}"] = value;
            }

            foreach (var (key, value) in environment)
            {
                start.Environment[key] = value;
            }

            var listening = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
            var process = Process.Start(start)!;
            process.OutputDataReceived += (_, line) =>
            {
                if (line.Data is { } data && ListeningAddress(data) is { } address)
                {
                    listening.TrySetResult(new Uri(address.Replace("[::]", "127.0.0.1", StringComparison.Ordinal)));
                }
            };
            process.ErrorDataReceived += (_, _) => { };
            process.Exited += (_, _) => listening.TrySetException(new InvalidOperationException("ts-auth exited before listening."));
            process.EnableRaisingEvents = true;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            var service = new RunningBinary(process);
            var started = false;
            try
            {
                service.Client.BaseAddress = await listening.Task.WaitAsync(TimeSpan.FromSeconds(30));
                using var probe = await service.Client.GetAsync("/healthz");
                started = true;
                return service;
            }
            finally
            {
                // Every unsuccessful path (timeouts and cancellation included) stops the child.
                if (!started)
                {
                    await service.DisposeAsync();
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            this.Client.Dispose();
            if (!this.process.HasExited)
            {
                this.process.Kill(entireProcessTree: true);
                await this.process.WaitForExitAsync();
            }

            this.process.Dispose();
        }
    }

    private const string StorageUrlRequired =
        "TS_AUTH_TERRABASE_URL is required outside Development";

    // Answers every TerraBase request with 503 and records where it was sent; nothing leaves the process.
    private sealed class RecordingTransport(System.Collections.Concurrent.ConcurrentQueue<Uri> sent) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            sent.Enqueue(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }
    }

    private static async Task<(int ExitCode, string Output)> RunBinaryAsync(string argument, Dictionary<string, string>? settings = null,
        string? workingDirectory = null, Dictionary<string, string>? environment = null)
    {
        var binary = Path.Combine(AppContext.BaseDirectory, "ts-auth.dll");
        var start = new ProcessStartInfo("dotnet", [binary, argument])
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            WorkingDirectory = workingDirectory ?? string.Empty,
        };
        // Isolate application and host configuration while preserving runtime paths and bundle extraction settings.
        foreach (var name in start.Environment.Keys.Where(key =>
            key.StartsWith("TS_AUTH_", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("CLOUDFLARE_", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("ASPNETCORE_", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("OTEL_", StringComparison.OrdinalIgnoreCase) || key.StartsWith("Logging__", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("Kestrel__", StringComparison.OrdinalIgnoreCase) || key.StartsWith("Sentry__", StringComparison.OrdinalIgnoreCase)
            || key.ToUpperInvariant() is "TERRABASE_PEER_SECRET" or "TERRABASE_PEER_SECRET_FILE" or "DOTNET_TERRABASE_PEER_SECRET" or "DOTNET_ENVIRONMENT"
                or "DOTNET_CONTENTROOT" or "DOTNET_APPLICATIONNAME" or "ENVIRONMENT" or "CONTENTROOT" or "APPLICATIONNAME"
                or "URLS" or "HTTP_PORTS" or "HTTPS_PORTS" or "PREFERHOSTINGURLS" or "FORWARDEDHEADERS_ENABLED"
                or "HOSTINGSTARTUPASSEMBLIES" or "HOSTINGSTARTUPEXCLUDEASSEMBLIES" or "PREVENTHOSTINGSTARTUP").ToList())
        {
            start.Environment.Remove(name);
        }

        // Production configuration probes run on the supported auth authority unless explicitly overridden.
        start.Environment["TS_REGION"] = "gru";

        foreach (var (key, value) in settings ?? [])
        {
            start.Environment[$"TS_AUTH_{key.Replace('-', '_').ToUpperInvariant()}"] = value;
        }

        foreach (var (key, value) in environment ?? [])
        {
            start.Environment[key] = value;
        }

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            // A regression that lets the server start must fail the test, not hang it.
            await process.WaitForExitAsync(deadline.Token);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }

        return (process.ExitCode, await stdout + await stderr);
    }
}
