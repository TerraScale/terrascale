using System.Net;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.Tokens;
using TerraBase.Features.UserAuth;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Fast.Management;

/// <summary>ts-auth access tokens authenticate Management API users (TS-519).</summary>
public sealed class UserAuthTests
{
    private const string Organizations = "/v1/management/organizations";

    private static readonly Faker Faker = new();

    [Test]
    public async Task Tokens_signed_with_a_published_ec_or_rsa_key_act_as_their_sub()
    {
        await using var api = await ManagementApi.StartAsync(services: TerraBaseTestServer.EnableConnectionDiagnostics);
        var rsa = api.Issuer.AddKey(SecurityAlgorithms.RsaSha256);
        var user = EntityIds.New().ToString();
        using var ec = api.WithToken(api.Issuer.Mint(user));
        using var rs = api.WithToken(api.Issuer.Mint(user, token => token.SigningCredentials = rsa));

        using var created = await ec.PostAsJsonAsync(Uri(Organizations), new { name = Faker.Company.CompanyName() });
        var organization = await created.Content.ReadFromJsonAsync<JsonElement>();
        var listed = await rs.GetFromJsonAsync<JsonElement>(Uri(Organizations));

        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(organization.GetProperty("createdBy").GetString()).IsEqualTo(user);
        await Assert.That(listed.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetString()))
            .IsEquivalentTo([organization.GetProperty("id").GetString()]);
    }

    [Test]
    public async Task Concurrent_users_with_ordinary_and_multi_kib_tokens_each_act_only_as_their_own_sub_on_cached_requests()
    {
        await using var api = await ManagementApi.StartAsync();
        // An unchecked claim pads some tokens from a few hundred characters to several KiB.
        var users = Enumerable.Range(0, 6).Select(_ => EntityIds.New().ToString()).ToArray();
        var tokens = users.Select((user, index) => api.Issuer.Mint(user, token => token.Claims["note"] = Faker.Random.AlphaNumeric(index % 3 * 1500))).ToArray();
        var clients = tokens.Select(api.WithToken).ToArray();
        try
        {
            // First requests validate concurrently; the later rounds all answer from accepted tokens.
            var created = await Task.WhenAll(clients.Select(async client =>
            {
                using var response = await client.PostAsJsonAsync(Uri(Organizations), new { name = Faker.Company.CompanyName() });
                return (response.StatusCode, Body: await response.Content.ReadFromJsonAsync<JsonElement>());
            }));
            var listed = await Task.WhenAll(Enumerable.Range(0, 4).SelectMany(_ => clients.Select(async (client, index) =>
                (index, Items: (await client.GetFromJsonAsync<JsonElement>(Uri(Organizations))).GetProperty("items")
                    .EnumerateArray().Select(item => item.GetProperty("id").GetString()).ToArray()))));

            await Assert.That(tokens.Min(token => token.Length)).IsLessThan(1024);
            await Assert.That(tokens.Max(token => token.Length)).IsGreaterThan(4096);
            await Assert.That(created.Select(result => result.StatusCode)).All().Satisfy(status => status.IsEqualTo(HttpStatusCode.Created));
            await Assert.That(created.Select(result => result.Body.GetProperty("createdBy").GetString()!)).IsEquivalentTo(users, TUnit.Assertions.Enums.CollectionOrdering.Matching);
            foreach (var (index, items) in listed)
            {
                await Assert.That(items).IsEquivalentTo([created[index].Body.GetProperty("id").GetString()]);
            }
        }
        finally
        {
            foreach (var client in clients)
            {
                client.Dispose();
            }
        }
    }

    [Test]
    public async Task Tokens_outside_their_lifetime_or_for_another_issuer_or_audience_are_rejected()
    {
        await using var api = await ManagementApi.StartAsync();
        var user = EntityIds.New().ToString();
        var now = DateTime.UtcNow;
        Action<SecurityTokenDescriptor>[] invalid =
        [
            token => (token.IssuedAt, token.NotBefore, token.Expires) = (now.AddMinutes(-10), now.AddMinutes(-10), now.AddMinutes(-2)),
            token => token.Expires = now.AddSeconds(-10),
            token => (token.NotBefore, token.Expires) = (now.AddMinutes(2), now.AddMinutes(10)),
            token => token.Expires = null,
            token => token.Issuer = "https://auth.test/other-instance",
            token => token.Issuer = TestIssuer.Issuer + "/",
            token => token.Audience = "https://other.test",
            token => token.Audience = TestIssuer.Audience + "/",
            token => token.Audience = null,
        ];

        var statuses = new List<HttpStatusCode>();
        foreach (var edit in invalid)
        {
            statuses.Add(await SendOnceAsync(api.WithToken(api.Issuer.Mint(user, edit))));
        }

        // Clock tolerance applies only to nbf, never to exp.
        var skewed = await SendOnceAsync(api.WithToken(api.Issuer.Mint(user, token => token.NotBefore = DateTime.UtcNow.AddSeconds(10))));

        await Assert.That(statuses).All().Satisfy(status => status.IsEqualTo(HttpStatusCode.Unauthorized));
        await Assert.That(skewed).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task Cached_and_uncached_tokens_are_rejected_at_exact_expiration_without_contacting_the_issuer()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var clock = new FakeTimeProvider(now);
        await using var api = await ManagementApi.StartAsync(services: services =>
            services.AddKeyedSingleton<TimeProvider>(UserAuthenticationHandler.SchemeName, clock));
        var expires = now.AddMinutes(5);
        string Mint()
        {
            return api.Issuer.Mint(EntityIds.New().ToString(), token =>
                (token.IssuedAt, token.NotBefore, token.Expires) = (now.UtcDateTime, now.UtcDateTime, expires.UtcDateTime));
        }
        using var warm = api.WithToken(Mint());
        using var cold = api.WithToken(Mint());
        var discoveryFetches = api.Issuer.DiscoveryFetches;
        var jwksFetches = api.Issuer.JwksFetches;
        api.Issuer.Available = false;

        await Assert.That(await StatusAsync(warm)).IsEqualTo(HttpStatusCode.OK);
        clock.SetUtcNow(expires.AddTicks(-1));
        await Assert.That(await StatusAsync(warm)).IsEqualTo(HttpStatusCode.OK);
        clock.Advance(TimeSpan.FromTicks(1));

        await Assert.That(await StatusAsync(warm)).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(await StatusAsync(cold)).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(api.Issuer.DiscoveryFetches).IsEqualTo(discoveryFetches);
        await Assert.That(api.Issuer.JwksFetches).IsEqualTo(jwksFetches);
        await Assert.That(api.Issuer.ProjectFetches).IsEqualTo(0);
    }

    [Test]
    public async Task Unsigned_symmetric_tampered_and_non_access_tokens_are_rejected()
    {
        await using var api = await ManagementApi.StartAsync();
        var user = EntityIds.New().ToString();
        // An HMAC key published in the JWKS: anyone holding it could sign, so it must never be accepted. Two layers
        // refuse it (the key filter and the algorithm list); this checks the outcome, not each layer on its own.
        var hmac = api.Issuer.AddKey(SecurityAlgorithms.HmacSha256);
        var valid = api.Issuer.Mint(user);
        string[] tokens =
        [
            TestIssuer.MintUnsigned(user),
            api.Issuer.Mint(user, token => token.SigningCredentials = hmac),
            api.Issuer.Mint(user, token => token.TokenType = "JWT"),
            valid[..^2] + (valid[^2] == 'A' ? "B" : "A") + valid[^1],
            "not-a-jwt",
        ];

        var statuses = new List<HttpStatusCode>();
        foreach (var token in tokens)
        {
            statuses.Add(await SendOnceAsync(api.WithToken(token)));
        }

        await Assert.That(statuses).All().Satisfy(status => status.IsEqualTo(HttpStatusCode.Unauthorized));
        await Assert.That(await SendOnceAsync(api.WithToken(valid))).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task Tokens_without_a_usable_sub_or_of_a_guest_are_rejected()
    {
        await using var api = await ManagementApi.StartAsync(services: TerraBaseTestServer.EnableConnectionDiagnostics);
        string?[] subjects =
        [
            null, "", new string('u', 257), "bad\u0001user",
            "00112233-4455-6677-8899-aabbccddeeff",
            "01arz3ndektsv4rrffq69g5fav",
            "01ARZ3NDEKTSV4RRFFQ69G5FAI",
            "81ARZ3NDEKTSV4RRFFQ69G5FAV",
        ];

        var statuses = new List<HttpStatusCode>();
        foreach (var subject in subjects)
        {
            statuses.Add(await SendOnceAsync(api.WithToken(api.Issuer.Mint(subject))));
        }

        var user = EntityIds.New().ToString();
        statuses.Add(await SendOnceAsync(api.WithToken(api.Issuer.Mint(user, token => token.Claims["is_anonymous"] = true))));
        var member = await SendOnceAsync(api.WithToken(api.Issuer.Mint(user, token => token.Claims["is_anonymous"] = false)));

        await Assert.That(statuses).All().Satisfy(status => status.IsEqualTo(HttpStatusCode.Unauthorized));
        await Assert.That(member).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task A_rotated_key_is_fetched_once_and_unknown_keys_refresh_at_most_every_30_seconds()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var api = await ManagementApi.StartAsync(services: TerraBaseTestServer.EnableConnectionDiagnostics, time: clock);
        var fetchedAtStartup = api.Issuer.JwksFetches;

        // ts-auth publishes a new key and signs with it: concurrent requests share one refresh and all pass.
        api.Issuer.Rotate();
        var rotated = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => SendOnceAsync(api.As(EntityIds.New().ToString()))));
        var fetchedAfterRotation = api.Issuer.JwksFetches;
        // Within 30 s of that refresh, another unknown key (unpublished, then published) costs no fetch and fails.
        clock.Advance(TsAuthKeys.UnknownKeyInterval - TimeSpan.FromTicks(1));
        var unpublished = api.Issuer.AddKey(SecurityAlgorithms.EcdsaSha256, publish: false);
        var forged = await SendOnceAsync(api.WithToken(api.Issuer.Mint(EntityIds.New().ToString(), token => token.SigningCredentials = unpublished)));
        api.Issuer.Rotate();
        var tooSoon = await SendOnceAsync(api.As(EntityIds.New().ToString()));
        var fetchedWithinInterval = api.Issuer.JwksFetches;
        clock.Advance(TimeSpan.FromTicks(1));
        var later = await SendOnceAsync(api.As(EntityIds.New().ToString()));

        await Assert.That(fetchedAtStartup).IsEqualTo(1);
        await Assert.That(rotated).All().Satisfy(status => status.IsEqualTo(HttpStatusCode.OK));
        await Assert.That(fetchedAfterRotation).IsEqualTo(2);
        await Assert.That(forged).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(tooSoon).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(fetchedWithinInterval).IsEqualTo(2);
        await Assert.That(later).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(api.Issuer.JwksFetches).IsEqualTo(3);
    }

    [Test]
    public async Task The_scheduled_refresh_adds_new_keys_and_drops_withdrawn_ones()
    {
        var timers = new ManualTimers();
        await using var api = await ManagementApi.StartAsync(services: services => services.AddSingleton<TimeProvider>(timers));
        var user = EntityIds.New().ToString();
        var withdrawn = api.Issuer.Signing;
        var oldToken = api.Issuer.Mint(user);
        var before = await SendOnceAsync(api.WithToken(oldToken));
        var fetched = api.Issuer.JwksFetches;

        // ts-auth rotates and retires the old key; the next scheduled refresh fetches without any request asking.
        var current = api.Issuer.Rotate();
        api.Issuer.Withdraw(withdrawn);
        await timers.FireAsync(TsAuthKeys.RefreshInterval);
        // The loop schedules its next refresh only after that fetch replaced the cache.
        await timers.ScheduledAsync(TsAuthKeys.RefreshInterval);
        var cached = api.Server.Services.GetRequiredService<IIssuerKeys>().Keys!.Select(key => key.KeyId).ToList();
        var fetchedByTheSchedule = api.Issuer.JwksFetches - fetched;
        var old = await SendOnceAsync(api.WithToken(oldToken));
        var renewed = await SendOnceAsync(api.As(user));

        await Assert.That(before).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(fetchedByTheSchedule).IsEqualTo(1);
        await Assert.That(cached).Contains(current.Key.KeyId);
        await Assert.That(cached).DoesNotContain(withdrawn.Key.KeyId);
        await Assert.That(old).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(renewed).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task A_project_token_whose_signing_key_is_withdrawn_reads_shapes_until_the_next_scheduled_refresh_and_never_after()
    {
        // V4 row 10: TerraBase has no revocation list, so withdrawing the signing key is ts-auth's only early cut-off.
        var timers = new ManualTimers();
        await using var api = await ManagementApi.StartAsync(services: services => services.AddSingleton<TimeProvider>(timers));
        var project = EntityIds.New().ToString();
        var withdrawn = api.Issuer.Signing;
        using var client = api.WithToken(api.Issuer.Mint(EntityIds.New().ToString(), token =>
        {
            token.Claims["project_id"] = project;
            token.Claims["scope"] = "databases:write documents:read documents:write";
        }));
        var database = $"/v1/projects/{project}/databases/main";
        using (var created = await client.PostAsJsonAsync(Uri($"/v1/projects/{project}/databases"), new { name = "main" }))
        {
            created.EnsureSuccessStatusCode();
        }

        using (var collection = await client.PostAsJsonAsync(Uri(database + "/collections"), new { name = "items" }))
        {
            collection.EnsureSuccessStatusCode();
        }

        var shape = database + "/collections/items/shape?offset=-1";
        var before = await ShapeStatusAsync(client, shape);

        // ts-auth retires the key; until TerraBase's next scheduled refresh the cached key still verifies the token.
        api.Issuer.Rotate();
        api.Issuer.Withdraw(withdrawn);
        var beforeRefresh = await ShapeStatusAsync(client, shape);
        await timers.FireAsync(TsAuthKeys.RefreshInterval);
        await timers.ScheduledAsync(TsAuthKeys.RefreshInterval);
        var afterRefresh = await ShapeStatusAsync(client, shape);

        await Assert.That(before).IsEqualTo(HttpStatusCode.TemporaryRedirect);
        await Assert.That(beforeRefresh).IsEqualTo(HttpStatusCode.TemporaryRedirect).Because("the withdrawn key stays cached until the refresh");
        await Assert.That(afterRefresh).IsEqualTo(HttpStatusCode.Unauthorized).Because("one scheduled refresh retires it");
        // Measured bound for key withdrawal: one refresh interval (10 min) while ts-auth answers.
        await Assert.That(TsAuthKeys.RefreshInterval).IsEqualTo(TimeSpan.FromMinutes(10));
    }

    [Test]
    public async Task One_deadline_bounds_a_fetch_that_ts_auth_never_finishes()
    {
        var timers = new ManualTimers();
        using var tls = new ClientTls();
        using var issuer = new TestIssuer { HangJwks = true };
        var starting = TerraBaseTestServer.StartAsync(
            settings =>
            {
                tls.Apply(settings);
                TestIssuer.Configure(settings);
            },
            services =>
            {
                issuer.Route(services);
                services.AddSingleton<TimeProvider>(timers);
                // Only the fetch deadline may end the hang, not the HTTP client's own timeout.
                services.AddHttpClient(TsAuthKeys.HttpClientName, client => client.Timeout = Timeout.InfiniteTimeSpan);
            });

        // Startup waits for the first fetch, which answers discovery then hangs on the JWKS until its deadline passes.
        await issuer.JwksHanging.WaitAsync(TimeSpan.FromSeconds(30));
        await timers.FireAsync(TsAuthKeys.FetchTimeout);
        await using var server = await starting.WaitAsync(TimeSpan.FromSeconds(30));
        using var ready = await server.Client.GetAsync(Uri("/readyz"));

        // Serving does not wait for ts-auth's keys: only user tokens do.
        await Assert.That(issuer.DiscoveryFetches).IsEqualTo(1);
        await Assert.That(ready.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task Ts_auth_going_down_after_startup_keeps_the_cached_keys()
    {
        await using var api = await ManagementApi.StartAsync();
        api.Issuer.Available = false;
        var unknown = api.Issuer.AddKey(SecurityAlgorithms.EcdsaSha256, publish: false);

        // The unknown key's refresh fails; the cached keys keep validating and the server stays ready.
        var refreshFailed = await SendOnceAsync(api.WithToken(api.Issuer.Mint(EntityIds.New().ToString(), token => token.SigningCredentials = unknown)));
        var cached = await SendOnceAsync(api.As(EntityIds.New().ToString()));
        using var ready = await api.Server.Client.GetAsync(Uri("/readyz"));

        await Assert.That(refreshFailed).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(cached).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(ready.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task Ts_auth_unreachable_at_startup_leaves_the_server_ready_and_tokens_rejected_until_its_keys_load()
    {
        var timers = new ManualTimers();
        using var tls = new ClientTls();
        using var issuer = new TestIssuer { Available = false };
        // Timers fire only when the test fires them, so the background retry cannot add a fetch while requests arrive.
        await using var server = await TerraBaseTestServer.StartAsync(
            settings =>
            {
                tls.Apply(settings);
                TestIssuer.Configure(settings);
            },
            services =>
            {
                issuer.Route(services);
                services.AddSingleton<TimeProvider>(timers);
            });
        issuer.Rotate();
        using var client = tls.CreateClient(server.ClientAddress!, issuer.Mint(EntityIds.New().ToString()));

        // The startup fetch runs in the background; it is started before the server answers, and every request joins it
        // instead of starting another, so once the requests are answered it has finished and is the only fetch.
        var statuses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => StatusAsync(client)));
        var fetchesWhileRejecting = issuer.DiscoveryFetches;
        using var adminReady = await server.Client.GetAsync(Uri("/readyz"));
        using var clientReady = await client.GetAsync(Uri("/readyz"));

        // ts-auth answers; the background retry loads its keys and then schedules the regular refresh.
        issuer.Available = true;
        await timers.FireAsync(TsAuthKeys.StartupRetry);
        await timers.ScheduledAsync(TsAuthKeys.RefreshInterval);
        var accepted = await StatusAsync(client);

        await Assert.That(statuses).All().Satisfy(status => status.IsEqualTo(HttpStatusCode.Unauthorized));
        await Assert.That(fetchesWhileRejecting).IsEqualTo(1);
        await Assert.That(adminReady.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(clientReady.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(accepted).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task A_discovery_document_naming_another_issuer_is_not_trusted()
    {
        using var tls = new ClientTls();
        using var issuer = new TestIssuer { DiscoveryIssuer = "https://auth.evil.test/terrascale" };
        // Timers never fire, so the background retry cannot fetch again while the test asserts.
        await using var server = await TerraBaseTestServer.StartAsync(
            settings =>
            {
                tls.Apply(settings);
                TestIssuer.Configure(settings);
            },
            services =>
            {
                issuer.Route(services);
                services.AddSingleton<TimeProvider>(new StoppedTimers());
            });
        using var client = tls.CreateClient(server.ClientAddress!, issuer.Mint(EntityIds.New().ToString()));

        var status = await StatusAsync(client);
        using var ready = await server.Client.GetAsync(Uri("/readyz"));

        // Its jwks_uri is never requested: the document is refused before anything it names is fetched.
        await Assert.That(issuer.DiscoveryFetches).IsEqualTo(1);
        await Assert.That(issuer.JwksFetches).IsEqualTo(0);
        await Assert.That(status).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(ready.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task Keys_are_fetched_over_the_servers_own_ts_auth_transport()
    {
        using var tls = new ClientTls();
        using var issuer = new TestIssuer();
        await using var transport = await issuer.ListenAsync();
        await using var server = await TerraBaseTestServer.StartAsync(
            settings =>
            {
                tls.Apply(settings);
                TestIssuer.Configure(settings);
            },
            transport.Route);
        using var client = tls.CreateClient(server.ClientAddress!, issuer.Mint(EntityIds.New().ToString()));

        var status = await StatusAsync(client);
        using var ready = await server.Client.GetAsync(Uri("/readyz"));

        await Assert.That(status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(ready.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    [Arguments("redirect")]
    [Arguments("oversized")]
    public async Task The_servers_own_transport_follows_no_redirect_and_reads_no_oversized_key_set(string answer)
    {
        using var tls = new ClientTls();
        // A redirect points at a valid key set, so only refusing to follow it keeps its keys out.
        using var issuer = new TestIssuer
        {
            RedirectJwks = answer == "redirect",
            JwksPaddingBytes = answer == "oversized" ? TsAuthKeys.MaxDocumentBytes : 0,
        };
        await using var transport = await issuer.ListenAsync();
        // Timers never fire, so the background retry cannot fetch again while the test asserts.
        await using var server = await TerraBaseTestServer.StartAsync(
            settings =>
            {
                tls.Apply(settings);
                TestIssuer.Configure(settings);
            },
            services =>
            {
                transport.Route(services);
                services.AddSingleton<TimeProvider>(new StoppedTimers());
            });
        using var client = tls.CreateClient(server.ClientAddress!, issuer.Mint(EntityIds.New().ToString()));

        var status = await StatusAsync(client);
        using var ready = await server.Client.GetAsync(Uri("/readyz"));

        await Assert.That(issuer.JwksFetches).IsEqualTo(1);
        await Assert.That(issuer.MovedJwksFetches).IsEqualTo(0);
        await Assert.That(status).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(ready.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task Users_reach_only_their_own_organizations_whatever_headers_they_send()
    {
        await using var api = await ManagementApi.StartAsync();
        var organization = await api.CreateOrganizationAsync(Faker.Company.CompanyName());
        var project = await api.CreateProjectAsync(organization, "Web");
        using var mallory = api.As(EntityIds.New().ToString());
        // Headers that once named the acting user and organization: ignored, never trusted.
        mallory.DefaultRequestHeaders.Add("X-TerraBase-User-Id", api.User);
        mallory.DefaultRequestHeaders.Add("X-TerraBase-Organization-Id", organization);

        var reads = await Task.WhenAll(
            StatusAsync(mallory, $"{Organizations}/{organization}"),
            StatusAsync(mallory, $"{Organizations}/{organization}/projects"),
            StatusAsync(mallory, $"{Organizations}/{organization}/projects/{project}"),
            StatusAsync(mallory, $"{Organizations}/{organization}/members"));
        using var createProject = await mallory.PostAsJsonAsync(Uri($"{Organizations}/{organization}/projects"), new { name = "Stolen" });
        using var delete = await mallory.DeleteAsync(Uri($"{Organizations}/{organization}"));
        var listed = await mallory.GetFromJsonAsync<JsonElement>(Uri(Organizations));
        var stillThere = await StatusAsync(api.Client, $"{Organizations}/{organization}");

        await Assert.That(reads).All().Satisfy(status => status.IsEqualTo(HttpStatusCode.NotFound));
        await Assert.That(createProject.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(delete.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(listed.GetProperty("items").GetArrayLength()).IsEqualTo(0);
        await Assert.That(stillThere).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task Invitations_are_accepted_only_with_the_verified_email_of_the_token()
    {
        await using var api = await ManagementApi.StartAsync();
        var organization = await api.CreateOrganizationAsync(Faker.Company.CompanyName());
        var email = Faker.Internet.Email();
        using var invited = await api.Client.PostAsJsonAsync(Uri($"{Organizations}/{organization}/invitations"), new { email, role = "member" });
        var token = (await invited.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        var user = EntityIds.New().ToString();
        string[] refused =
        [
            api.Issuer.Mint(user),
            api.Issuer.MintWithEmail(user, email, verified: false),
            api.Issuer.MintWithEmail(user, Faker.Internet.Email()),
        ];

        var statuses = new List<HttpStatusCode>();
        foreach (var accessToken in refused)
        {
            using var client = api.WithToken(accessToken);
            // The body's email is ignored: only the token's verified email counts.
            using var response = await client.PostAsJsonAsync(Uri("/v1/management/invitations/accept"), new { token, email });
            statuses.Add(response.StatusCode);
        }

        using var member = api.As(user, email);
        using var accepted = await member.PostAsJsonAsync(Uri("/v1/management/invitations/accept"), new { token });

        await Assert.That(statuses).All().Satisfy(status => status.IsEqualTo(HttpStatusCode.Forbidden));
        await Assert.That(accepted.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await accepted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("userId").GetString()).IsEqualTo(user);
    }

    private static Uri Uri(string path) => new(path, UriKind.Relative);

    private static async Task<HttpStatusCode> StatusAsync(HttpClient client, string path = Organizations)
    {
        using var response = await client.GetAsync(Uri(path));
        return response.StatusCode;
    }

    private static async Task<HttpStatusCode> ShapeStatusAsync(HttpClient client, string target)
    {
        using var response = await client.GetAsync(Uri(target));
        return response.StatusCode;
    }

    // Sends one request with a client made for it, then disposes the client.
    private static async Task<HttpStatusCode> SendOnceAsync(HttpClient client)
    {
        using (client)
        {
            return await StatusAsync(client);
        }
    }

    /// <summary>The system clock with timers that fire only through <see cref="FireAsync"/>.</summary>
    private sealed class ManualTimers : TimeProvider
    {
        private readonly Lock _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private readonly List<(TimeSpan Due, TaskCompletionSource Created)> _waiters = [];

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state, dueTime);
            lock (_gate)
            {
                _timers.Add(timer);
                foreach (var (_, created) in _waiters.Where(waiter => waiter.Due == dueTime))
                {
                    created.TrySetResult();
                }

                _waiters.RemoveAll(waiter => waiter.Due == dueTime);
            }

            return timer;
        }

        /// <summary>Completes once a timer due after <paramref name="dueTime"/> exists (within 30 seconds).</summary>
        public Task ScheduledAsync(TimeSpan dueTime)
        {
            lock (_gate)
            {
                if (_timers.Any(timer => timer.Due == dueTime))
                {
                    return Task.CompletedTask;
                }

                var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add((dueTime, waiter));
                return waiter.Task.WaitAsync(TimeSpan.FromSeconds(30));
            }
        }

        /// <summary>Waits until a timer due after <paramref name="dueTime"/> exists, then fires each such timer once.</summary>
        public async Task FireAsync(TimeSpan dueTime)
        {
            await ScheduledAsync(dueTime);
            ManualTimer[] due;
            lock (_gate)
            {
                due = [.. _timers.Where(timer => timer.Due == dueTime)];
                _timers.RemoveAll(timer => timer.Due == dueTime);
            }

            foreach (var timer in due)
            {
                timer.Fire();
            }
        }

        private void Remove(ManualTimer timer)
        {
            lock (_gate)
            {
                _timers.Remove(timer);
            }
        }

        private sealed class ManualTimer(ManualTimers owner, TimerCallback callback, object? state, TimeSpan dueTime) : ITimer
        {
            public TimeSpan Due { get; private set; } = dueTime;

            public void Fire() => callback(state);

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                Due = dueTime;
                return true;
            }

            public void Dispose() => owner.Remove(this);

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    /// <summary>The system clock with timers that never fire.</summary>
    private sealed class StoppedTimers : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            base.CreateTimer(callback, state, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }
}
