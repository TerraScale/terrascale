using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Bogus;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.TerraBase.Testing;

namespace TerraScale.TerraBase.Slow.Management;

public sealed class AccessExpiryTests
{
    private const string Organizations = "/v1/management/organizations";
    private const string Accept = "/v1/management/invitations/accept";
    private static readonly Faker Faker = new();

    // Past the seven-day lifetime of invitations and ownership transfers.
    private static readonly TimeSpan PastExpiry = TimeSpan.FromDays(8);

    [Test]
    public async Task Expired_invitations_cannot_be_accepted_and_are_replaced()
    {
        await using var api = await ShiftedApi.StartAsync();
        var ownerId = User();
        var org = Org(await api.CreateOrganizationAsync(ownerId));
        using var owner = api.As(ownerId);
        using var invitee = api.As(User());
        var invitations = Uri($"{org}/invitations");
        var accept = Uri(Accept);
        var email = Email();
        var expiredToken = await InviteAsync(owner, org, email);

        using var stillPending = await owner.PostAsJsonAsync(invitations, new { email });
        api.Clock.Offset += PastExpiry;
        using var expired = await invitee.PostAsJsonAsync(accept, new { token = expiredToken, email });
        using var reinvited = await owner.PostAsJsonAsync(invitations, new { email });
        var fresh = await reinvited.Content.ReadFromJsonAsync<JsonElement>();
        var pending = await owner.GetFromJsonAsync<JsonElement>(invitations);
        using var replaced = await invitee.PostAsJsonAsync(accept, new { token = expiredToken, email });
        using var accepted = await invitee.PostAsJsonAsync(accept, new { token = fresh.GetProperty("token").GetString(), email });

        await Assert.That(stillPending.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(expired.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(reinvited.StatusCode).IsEqualTo(HttpStatusCode.Created);
        // The expired invitation was replaced rather than kept beside the new one.
        await Assert.That(Strings(pending, "id")).IsEquivalentTo([fresh.GetProperty("invitation").GetProperty("id").GetString()!]);
        await Assert.That(replaced.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(accepted.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task Expired_ownership_transfers_can_no_longer_be_confirmed()
    {
        await using var api = await ShiftedApi.StartAsync();
        var ownerId = User();
        var org = Org(await api.CreateOrganizationAsync(ownerId));
        using var owner = api.As(ownerId);
        var nomineeId = await api.AddMemberAsync(owner, org);
        using var nominee = api.As(nomineeId);
        var transfer = Uri($"{org}/ownership-transfer");

        using var started = await owner.PostAsJsonAsync(transfer, new { userId = nomineeId });
        using var pending = await nominee.GetAsync(transfer);
        api.Clock.Offset += PastExpiry;
        using var expired = await nominee.GetAsync(transfer);
        using var confirmed = await nominee.PostAsync(Uri($"{org}/ownership-transfer/confirm"), null);
        var members = await owner.GetFromJsonAsync<JsonElement>(Uri($"{org}/members"));

        await Assert.That(started.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(pending.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(expired.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(confirmed.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(Roles(members)).IsEquivalentTo([$"{ownerId}=owner", $"{nomineeId}=member"]);
    }

    [Test]
    public async Task Audit_events_past_the_retention_are_no_longer_listed()
    {
        await using var api = await ShiftedApi.StartAsync();
        var ownerId = User();
        var org = Org(await api.CreateOrganizationAsync(ownerId));
        using var owner = api.As(ownerId);
        var events = Uri($"{org}/audit-events");
        var oldEmail = Email();
        var newEmail = Email();

        (await owner.PutAsJsonAsync(Uri($"{org}/audit-retention"), new { days = 1 })).EnsureSuccessStatusCode();
        await InviteAsync(owner, org, oldEmail);
        var before = await owner.GetFromJsonAsync<JsonElement>(events);
        api.Clock.Offset += TimeSpan.FromDays(2);
        await InviteAsync(owner, org, newEmail);
        var after = await owner.GetFromJsonAsync<JsonElement>(events);

        await Assert.That(Events(before)).IsEquivalentTo([$"invitation.created|{oldEmail}", "settings.audit_retention_updated|"]);
        await Assert.That(Events(after)).IsEquivalentTo([$"invitation.created|{newEmail}"]);
    }

    [Test]
    public async Task Order_follows_insertion_across_a_clock_step_back()
    {
        await using var api = await ShiftedApi.StartAsync();
        var ownerId = User();
        var org = Org(await api.CreateOrganizationAsync(ownerId));
        using var owner = api.As(ownerId);
        var events = Uri($"{org}/audit-events");
        var firstEmail = Email();
        var secondEmail = Email();

        (await owner.PutAsJsonAsync(Uri($"{org}/audit-retention"), new { days = 1 })).EnsureSuccessStatusCode();
        await InviteAsync(owner, org, firstEmail);
        api.Clock.Offset -= TimeSpan.FromDays(2);
        await InviteAsync(owner, org, secondEmail);
        var invitations = string.Join(',', Strings(await owner.GetFromJsonAsync<JsonElement>(Uri($"{org}/invitations")), "email"));
        var steppedBack = string.Join(',', Events(await owner.GetFromJsonAsync<JsonElement>(events)));
        api.Clock.Offset = TimeSpan.Zero;
        var restored = string.Join(',', Events(await owner.GetFromJsonAsync<JsonElement>(events)));

        // Inserted second, listed after the first invitation and as the newest event, whatever the clock said.
        await Assert.That(invitations).IsEqualTo(string.Join(',', firstEmail, secondEmail));
        await Assert.That(steppedBack)
            .IsEqualTo(string.Join(',', $"invitation.created|{secondEmail}", $"invitation.created|{firstEmail}", "settings.audit_retention_updated|"));
        // Recorded two days back, it is past the one-day retention once the clock is right again.
        await Assert.That(restored).IsEqualTo(string.Join(',', $"invitation.created|{firstEmail}", "settings.audit_retention_updated|"));
    }

    [Test]
    public async Task Order_holds_for_a_burst_within_one_millisecond()
    {
        await using var api = await ShiftedApi.StartAsync();
        api.Clock.FrozenAt = DateTimeOffset.UtcNow;
        var ownerId = User();
        var org = Org(await api.CreateOrganizationAsync(ownerId));
        using var owner = api.As(ownerId);
        var emails = Enumerable.Range(0, 5).Select(_ => Email()).ToList();
        foreach (var email in emails)
        {
            await InviteAsync(owner, org, email);
        }

        var created = emails.Select(email => $"invitation.created|{email}").Reverse().ToList();
        var invitations = Strings(await owner.GetFromJsonAsync<JsonElement>(Uri($"{org}/invitations")), "email");
        var events = Events(await owner.GetFromJsonAsync<JsonElement>(Uri($"{org}/audit-events")));
        var pagedInvitations = await PageAsync(owner, $"{org}/invitations", page => Strings(page, "email"));
        var pagedEvents = await PageAsync(owner, $"{org}/audit-events", Events);

        await Assert.That(string.Join(',', invitations)).IsEqualTo(string.Join(',', emails));
        await Assert.That(string.Join(',', events)).IsEqualTo(string.Join(',', created));
        // Every item exactly once, in list order.
        await Assert.That(string.Join(',', pagedInvitations)).IsEqualTo(string.Join(',', emails));
        await Assert.That(string.Join(',', pagedEvents)).IsEqualTo(string.Join(',', created));
    }

    [Test]
    public async Task Order_survives_a_restart_with_the_clock_behind()
    {
        var dataDirectory = Directory.CreateTempSubdirectory("terrabase-restart-").FullName;
        try
        {
            var ownerId = User();
            var frozenAt = DateTimeOffset.UtcNow;
            var emails = Enumerable.Range(0, 4).Select(_ => Email()).ToList();
            string org, next;
            await using (var api = await ShiftedApi.StartAsync(dataDirectory, new ShiftedTime { FrozenAt = frozenAt }))
            {
                org = Org(await api.CreateOrganizationAsync(ownerId));
                using var owner = api.As(ownerId);
                foreach (var email in emails.Take(3))
                {
                    await InviteAsync(owner, org, email);
                }

                next = (await owner.GetFromJsonAsync<JsonElement>(Uri($"{org}/invitations?limit=2"))).GetProperty("next").GetString()!;
            }

            string after, all, events;
            await using (var api = await ShiftedApi.StartAsync(dataDirectory, new ShiftedTime { FrozenAt = frozenAt.AddDays(-1) }))
            {
                using var owner = api.As(ownerId);
                await InviteAsync(owner, org, emails[3]);
                after = string.Join(',', Strings(await owner.GetFromJsonAsync<JsonElement>(Uri($"{org}/invitations?after={Escape(next)}")), "email"));
                all = string.Join(',', Strings(await owner.GetFromJsonAsync<JsonElement>(Uri($"{org}/invitations")), "email"));
                events = string.Join(',', Events(await owner.GetFromJsonAsync<JsonElement>(Uri($"{org}/audit-events"))));
            }

            await Assert.That(after).IsEqualTo(string.Join(',', emails[2], emails[3]));
            await Assert.That(all).IsEqualTo(string.Join(',', emails));
            await Assert.That(events).IsEqualTo(string.Join(',', emails.AsEnumerable().Reverse().Select(email => $"invitation.created|{email}")));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    [Test]
    public async Task Retention_keeps_events_at_the_exact_cutoff_and_later_in_its_millisecond()
    {
        await using var api = await ShiftedApi.StartAsync();
        // 0.9 ms into a millisecond, so that a cutoff 0.4 ms earlier falls in the same millisecond.
        var recordedAt = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).AddTicks(9000);
        api.Clock.FrozenAt = recordedAt;
        var ownerId = User();
        var org = Org(await api.CreateOrganizationAsync(ownerId));
        using var owner = api.As(ownerId);
        var events = Uri($"{org}/audit-events");
        var keptEmail = Email();
        var laterEmail = Email();

        (await owner.PutAsJsonAsync(Uri($"{org}/audit-retention"), new { days = 1 })).EnsureSuccessStatusCode();
        await InviteAsync(owner, org, keptEmail);
        // Cutoff 0.4 ms before the first events, in their millisecond: this write purges, but must keep them.
        api.Clock.Offset = TimeSpan.FromDays(1) - TimeSpan.FromTicks(4000);
        await InviteAsync(owner, org, laterEmail);
        var beforeCutoff = string.Join(',', Events(await owner.GetFromJsonAsync<JsonElement>(events)));
        api.Clock.Offset = TimeSpan.FromDays(1);
        var atCutoff = string.Join(',', Events(await owner.GetFromJsonAsync<JsonElement>(events)));
        api.Clock.Offset = TimeSpan.FromDays(1) + TimeSpan.FromTicks(1);
        var pastCutoff = string.Join(',', Events(await owner.GetFromJsonAsync<JsonElement>(events)));

        var all = string.Join(',', $"invitation.created|{laterEmail}", $"invitation.created|{keptEmail}", "settings.audit_retention_updated|");
        await Assert.That(beforeCutoff).IsEqualTo(all);
        // An event recorded exactly at the cutoff is still within the retention.
        await Assert.That(atCutoff).IsEqualTo(all);
        await Assert.That(pastCutoff).IsEqualTo($"invitation.created|{laterEmail}");
    }

    private static Uri Uri(string path) => new(path, UriKind.Relative);

    // The path of one organization; access routes hang below it.
    private static string Org(string organization) => $"{Organizations}/{organization}";

    private static string User() => Faker.Random.Guid().ToString();

    private static string Escape(string cursor) => System.Uri.EscapeDataString(cursor);

    // The suffix keeps two addresses of one test from colliding by chance.
    private static string Email() => Faker.Internet.Email(uniqueSuffix: Faker.Random.AlphaNumeric(6));

    /// <summary>Invites <paramref name="email"/> to <paramref name="org"/> as a member and returns the acceptance token.</summary>
    private static async Task<string> InviteAsync(HttpClient inviter, string org, string email)
    {
        using var response = await inviter.PostAsJsonAsync(Uri($"{org}/invitations"), new { email });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
    }

    // Every item of the list at path, read through limit=2 pages that follow the opaque next cursor.
    private static async Task<List<string>> PageAsync(HttpClient client, string path, Func<JsonElement, IEnumerable<string>> items)
    {
        var all = new List<string>();
        string? next = null;
        do
        {
            var page = await client.GetFromJsonAsync<JsonElement>(Uri(next is null ? $"{path}?limit=2" : $"{path}?limit=2&after={Escape(next)}"));
            all.AddRange(items(page));
            next = page.GetProperty("next").GetString();
        }
        while (next is not null);

        return all;
    }

    private static IEnumerable<string> Strings(JsonElement page, string property) =>
        page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty(property).GetString()!);

    // "userId=role" of every member on a page.
    private static IEnumerable<string> Roles(JsonElement page) =>
        page.GetProperty("items").EnumerateArray().Select(member => $"{member.GetProperty("userId").GetString()}={member.GetProperty("role").GetString()}");

    // "action|target" of every audit event on a page; a missing or null target is empty.
    private static IEnumerable<string> Events(JsonElement page) =>
        page.GetProperty("items").EnumerateArray().Select(item =>
            $"{item.GetProperty("action").GetString()}|{(item.TryGetProperty("target", out var target) ? target.GetString() : null)}");

    /// <summary>The system clock, or <see cref="FrozenAt"/> when set, moved by <see cref="Offset"/>.</summary>
    private sealed class ShiftedTime : TimeProvider
    {
        public TimeSpan Offset { get; set; }

        public DateTimeOffset? FrozenAt { get; set; }

        public override DateTimeOffset GetUtcNow() => (FrozenAt ?? base.GetUtcNow()) + Offset;
    }

    /// <summary>A server with the client listener, the dashboard credential and a clock that tests move or freeze.</summary>
    private sealed class ShiftedApi : IAsyncDisposable
    {
        private const string Credential = "test-dashboard-credential-0123456789abcdef";

        private readonly ClientTls _tls;
        private readonly string _directory;

        private ShiftedApi(ClientTls tls, string directory, TerraBaseTestServer server, ShiftedTime clock)
        {
            _tls = tls;
            _directory = directory;
            Server = server;
            Clock = clock;
        }

        public TerraBaseTestServer Server { get; }

        public ShiftedTime Clock { get; }

        /// <summary>Starts a server, on <paramref name="dataDirectory"/> when set and with <paramref name="clock"/> when set.</summary>
        public static async Task<ShiftedApi> StartAsync(string? dataDirectory = null, ShiftedTime? clock = null)
        {
            var directory = Directory.CreateTempSubdirectory("terrabase-dashboard-").FullName;
            var credentialFile = Path.Combine(directory, "credential");
            await File.WriteAllTextAsync(credentialFile, Credential + "\n");
            var tls = new ClientTls();
            clock ??= new ShiftedTime();
            var server = await TerraBaseTestServer.StartAsync(
                settings =>
                {
                    tls.Apply(settings);
                    settings["dashboard-credential-file"] = credentialFile;
                    if (dataDirectory is not null)
                    {
                        settings["data-directory"] = dataDirectory;
                    }
                },
                services => services.AddSingleton<TimeProvider>(clock));
            return new ShiftedApi(tls, directory, server, clock);
        }

        /// <summary>Creates an organization whose first owner is <paramref name="owner"/>; returns its id.</summary>
        public async Task<string> CreateOrganizationAsync(string owner)
        {
            using var client = As(owner);
            using var response = await client.PostAsJsonAsync(Uri(Organizations), new { name = Faker.Company.CompanyName() });
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        }

        /// <summary>
        /// A dashboard client acting as <paramref name="user"/> that, like ts-dashboard, also sends the organization
        /// from the path on organization routes.
        /// </summary>
        public HttpClient As(string user)
        {
            var client = new HttpClient(new DashboardHeaders(user) { InnerHandler = _tls.CreateHandler() })
            {
                BaseAddress = Server.ClientAddress,
                Timeout = TimeSpan.FromSeconds(30),
            };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Credential);
            return client;
        }

        /// <summary>Has a new user join <paramref name="org"/> as a member through an invitation from <paramref name="inviter"/>; returns its id.</summary>
        public async Task<string> AddMemberAsync(HttpClient inviter, string org)
        {
            var user = User();
            var email = Email();
            var token = await InviteAsync(inviter, org, email);
            using var client = As(user);
            using var response = await client.PostAsJsonAsync(Uri(Accept), new { token, email });
            response.EnsureSuccessStatusCode();
            return user;
        }

        public async ValueTask DisposeAsync()
        {
            await Server.DisposeAsync();
            _tls.Dispose();
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class DashboardHeaders(string user) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Add("X-TerraBase-User-Id", user);
            var segments = request.RequestUri!.AbsolutePath.Split('/');
            if (segments.Length > 4 && segments[3] == "organizations")
            {
                request.Headers.Add("X-TerraBase-Organization-Id", segments[4]);
            }

            return base.SendAsync(request, cancellationToken);
        }
    }
}
