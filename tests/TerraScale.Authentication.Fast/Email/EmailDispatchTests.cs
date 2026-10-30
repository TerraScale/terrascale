using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Authentication.Features.Email;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.RateLimiting;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Authentication.Store;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Fast.Email;

/// <summary>Durable mail dispatch over hosted HTTP, real TerraBase and a controlled sender.</summary>
[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class EmailDispatchTests(TerraBaseFixture fixture)
{
    [Test]
    [Arguments(1)]
    [Arguments(SignInMail.DeliveryConcurrency)]
    public async Task Stalled_sends_use_bounded_slots_and_a_free_slot_delivers_later_mail(int blocked)
    {
        var sender = new GatedSender();
        await using var oidc = await OidcHarness.StartAsync(fixture,
            services: services => services.AddSingleton<IEmailSender>(sender));
        try
        {
            var limiter = oidc.Server.App.Services.GetRequiredService<AttemptLimiter>();
            var folds = new HashSet<string>(StringComparer.Ordinal);
            var addresses = Enumerable.Range(0, 1000).Select(index => $"dispatch-{index}@example.test")
                .Where(address => folds.Add(limiter.Fold(address))).Take(blocked + 1).ToArray();
            foreach (var address in addresses)
            {
                await oidc.CreateUserAsync(address);
            }

            for (var index = 0; index < blocked; index++)
            {
                sender.Hold(addresses[index]);
                await StartAsync(oidc, addresses[index]);
                await Assert.That(await sender.NextStartedAsync()).IsEqualTo(addresses[index]);
            }

            await StartAsync(oidc, addresses[^1]);
            if (blocked == SignInMail.DeliveryConcurrency)
            {
                var states = await oidc.Server.MailStatesAsync();
                await Assert.That(states.Count(state => state == "sending")).IsEqualTo(blocked);
                await Assert.That(states.Count(state => state == "queued")).IsEqualTo(1);
                sender.Release(addresses[0]);
                await sender.Captured.NextAsync();
            }

            // The newer send must start and finish while older calls are still stalled.
            await Assert.That(await sender.NextStartedAsync()).IsEqualTo(addresses[^1]);
            await Assert.That((await sender.Captured.NextAsync()).Recipient).IsEqualTo(addresses[^1]);
            await Assert.That(sender.MaxInFlight).IsLessThanOrEqualTo(SignInMail.DeliveryConcurrency);
        }
        finally
        {
            sender.ReleaseAll();
        }
        await oidc.Server.DrainMailAsync();
    }

    [Test]
    public async Task A_full_batch_of_leased_rows_does_not_starve_new_mail()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        var store = oidc.Server.App.Services.GetRequiredService<AuthStore>();
        var ids = new List<string>();
        try
        {
            // Sending rows are owned by another replica until their lease expires.
            // Their payload is already unavailable; touching it would also fail the test.
            var lease = JsonSerializer.Serialize(DateTimeOffset.UtcNow + SignInMail.Lease);
            for (var index = 0; index < 64; index++)
            {
                var id = AuthStore.NewId();
                using var row = JsonDocument.Parse($$"""{"id":"{{id}}","state":"sending","locale":"en","createdAt":{{lease}},"leaseUntil":{{lease}},"payload":null}""");
                await store.CreateAsync(AuthStore.MailOutboxCollection, OidcIssuer.InstanceId, id, row.RootElement);
                ids.Add(id);
            }
            const string recipient = "after-leases@example.test";
            await oidc.CreateUserAsync(recipient);
            await StartAsync(oidc, recipient);
            await Assert.That((await oidc.Email.NextAsync()).Recipient).IsEqualTo(recipient);
            // Provider completion can precede the new row's final state write. Inspect
            // the leased rows themselves rather than counting that unrelated send.
            var leased = await Task.WhenAll(ids.Select(id => store.GetAsync(AuthStore.MailOutboxCollection, OidcIssuer.InstanceId, id)));
            await Assert.That(leased.Select(row => row!.Document.GetProperty("state").GetString()).ToArray())
                .IsEquivalentTo(Enumerable.Repeat("sending", 64).ToArray());
        }
        finally
        {
            foreach (var id in ids)
            {
                var document = await store.GetAsync(AuthStore.MailOutboxCollection, OidcIssuer.InstanceId, id);
                if (document is not null)
                {
                    await store.CompareAndDeleteAsync(AuthStore.MailOutboxCollection, OidcIssuer.InstanceId, id, document.Version);
                }
            }
        }
        await oidc.Server.DrainMailAsync();
    }

    private static async Task StartAsync(OidcHarness oidc, string recipient)
    {
        using var response = await oidc.PostFormAsync($"/ui/{OidcIssuer.Slug}/sign-in", new() { ["email"] = recipient });
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    private sealed class GatedSender : IEmailSender
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource> gates = new(StringComparer.Ordinal);
        private readonly Channel<string> started = Channel.CreateUnbounded<string>();

        private readonly Lock counting = new();
        private int inFlight;
        private int maxInFlight;

        public CapturingEmailSender Captured { get; } = new();

        public int MaxInFlight
        {
            get
            {
                lock (this.counting)
                {
                    return this.maxInFlight;
                }
            }
        }

        public void Hold(string recipient) => this.gates.TryAdd(recipient, new(TaskCreationOptions.RunContinuationsAsynchronously));

        public void Release(string recipient) => this.gates[recipient].TrySetResult();

        public void ReleaseAll()
        {
            foreach (var gate in this.gates.Values)
            {
                gate.TrySetResult();
            }
        }

        public async Task<string> NextStartedAsync() =>
            await this.started.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        public async Task SendAsync(string recipient, EmailMessage message, CancellationToken cancellationToken)
        {
            lock (this.counting)
            {
                this.maxInFlight = Math.Max(this.maxInFlight, ++this.inFlight);
            }
            try
            {
                this.gates.TryGetValue(recipient, out var gate);
                this.started.Writer.TryWrite(recipient);
                if (gate is not null)
                {
                    await gate.Task.WaitAsync(cancellationToken);
                }
                await this.Captured.SendAsync(recipient, message, cancellationToken);
            }
            finally
            {
                lock (this.counting)
                {
                    this.inFlight--;
                }
            }
        }
    }
}
