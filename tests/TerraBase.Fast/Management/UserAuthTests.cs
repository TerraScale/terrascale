using System.Net;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
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
            StatusAsync(mallory, $"{Organizations}/{organization}/projects/{project}"));
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
