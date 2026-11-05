using System.Net;
using System.Text.Json;
using Bogus;
using Microsoft.IdentityModel.Tokens;
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
        await using var api = await ManagementApi.StartSharedAsync();
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
    public async Task Users_reach_only_their_own_organizations_whatever_headers_they_send()
    {
        await using var api = await ManagementApi.StartSharedAsync();
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
}
