using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Auth.Contracts;
using TerraScale.Auth.Features.RateLimiting;
using TerraScale.Auth.Security;
using TerraScale.Auth.Store;
using TerraScale.Auth.Tests;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Auth.Fast.Security;

/// <summary>
/// The shared attempt limiter: fixed one-minute windows in TerraBase, scoped by instance, purpose
/// and subject. The test route admits an attempt with 200 or refuses it with 429.
/// </summary>
[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class RateLimitTests(TerraBaseFixture fixture)
{
    private const int Maximum = 3;

    private static readonly DateTimeOffset Start = new(2026, 10, 4, 12, 0, 5, TimeSpan.Zero);

    // Replicas share the master key, which keys the bucket ids.
    private static readonly string MasterKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    // Binds to the real TerraBase target; without one the server's storage is unreachable.
    // wrap layers fault or contention injection over the real store.
    private static Task<AuthTestServer> StartAsync(SharedStateTarget? target, TimeProvider clock, Func<IDocumentStore, IDocumentStore>? wrap = null) => AuthTestServer.StartAsync(
        settings: values =>
        {
            target?.Bind(values);
            values["master-key"] = MasterKey;
        },
        services: services =>
        {
            target?.Register(services);
            if (wrap is not null)
            {
                services.AddSingleton<IDocumentStore>(provider => wrap(new TerraBaseDocumentStore(provider.GetRequiredService<AuthStore>())));
            }

            services.AddSingleton(clock);
        },
        routes: app => app.MapPost("/__test/attempts/{instance}/{purpose}/{subject}", async (string instance, string purpose, string subject, int? maximum, AttemptLimiter limiter, CancellationToken cancellationToken) =>
            await limiter.TryAcquireAsync(instance, purpose, subject, maximum ?? Maximum, cancellationToken)
                ? Results.Ok()
                : Results.StatusCode(StatusCodes.Status429TooManyRequests)));

    [Test]
    public async Task Attempts_are_refused_after_the_maximum_until_the_next_window()
    {
        var clock = new ManualClock(Start);
        await using var server = await StartAsync(await SharedStateTarget.CreateAsync(fixture), clock);

        var admitted = await AttemptsAsync(server, "ins_a", "sign-in", "user_1", Maximum + 2);
        clock.Advance(TimeSpan.FromSeconds(54));
        var stillLimited = await AttemptAsync(server, "ins_a", "sign-in", "user_1");
        clock.Advance(TimeSpan.FromSeconds(1));
        var nextWindow = await AttemptsAsync(server, "ins_a", "sign-in", "user_1", Maximum + 1);

        await Assert.That(string.Join(",", admitted)).IsEqualTo("True,True,True,False,False");
        await Assert.That(stillLimited).IsFalse();
        await Assert.That(string.Join(",", nextWindow)).IsEqualTo("True,True,True,False");
    }

    [Test]
    public async Task Budgets_are_separate_per_instance_purpose_and_subject()
    {
        var target = await SharedStateTarget.CreateAsync(fixture);
        await using var server = await StartAsync(target, new ManualClock(Start));

        await AttemptsAsync(server, "ins_a", "sign-in", "user_1", Maximum);

        await Assert.That(await AttemptAsync(server, "ins_a", "sign-in", "user_1")).IsFalse();
        await Assert.That(await AttemptAsync(server, "ins_b", "sign-in", "user_1")).IsTrue();
        await Assert.That(await AttemptAsync(server, "ins_a", "recovery", "user_1")).IsTrue();
        await Assert.That(await AttemptAsync(server, "ins_a", "sign-in", "user_2")).IsTrue();
        // Bucket keys are keyed digests: neither the subject nor its unkeyed form addresses a bucket,
        // so stored addresses cannot be tested against guessed subjects.
        await Assert.That(await target.CountAsync(AttemptLimiter.Collection)).IsEqualTo(4);
        await Assert.That(await target.ReadAsync(AttemptLimiter.Collection, "ins_a", "user_1")).IsNull();
        await Assert.That(await target.ReadAsync(AttemptLimiter.Collection, "ins_a", "sign-in:user_1")).IsNull();
        await Assert.That(await target.ReadAsync(AttemptLimiter.Collection, "ins_a", Tokens.Sha256Hex("sign-in:user_1"))).IsNull();
    }

    [Test]
    public async Task Replicas_share_one_budget_and_a_slower_clock_never_resets_a_newer_window()
    {
        var target = await SharedStateTarget.CreateAsync(fixture);
        var ahead = new ManualClock(Start.AddMinutes(1));
        await using var first = await StartAsync(target, ahead);
        await using var behind = await StartAsync(target, new ManualClock(Start));

        await AttemptsAsync(first, "ins_a", "sign-in", "user_1", Maximum - 1);
        var lastSlot = await AttemptAsync(behind, "ins_a", "sign-in", "user_1");
        var overBudget = await AttemptAsync(behind, "ins_a", "sign-in", "user_1");

        await Assert.That(lastSlot).IsTrue();
        await Assert.That(overBudget).IsFalse();
        await Assert.That(await AttemptAsync(first, "ins_a", "sign-in", "user_1")).IsFalse();
    }

    [Test]
    [Arguments(10, HttpStatusCode.OK)]
    [Arguments(11, HttpStatusCode.TooManyRequests)]
    public async Task Losing_many_conditional_writes_still_ends_in_admission_or_refusal(int losses, HttpStatusCode expected)
    {
        var store = new ContendedStore();
        await using var server = await StartAsync(await SharedStateTarget.CreateAsync(fixture), new ManualClock(Start), inner => store.Over(inner));
        using var first = await server.Client.PostAsync("/__test/attempts/ins_a/sign-in/user_1?maximum=12", null);

        // Each write of this attempt loses to another replica admitting one, more than eight times.
        store.Losses = losses;
        using var contended = await server.Client.PostAsync("/__test/attempts/ins_a/sign-in/user_1?maximum=12", null);

        await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(contended.StatusCode).IsEqualTo(expected);
    }

    [Test]
    public async Task The_largest_maximum_still_admits_attempts()
    {
        await using var server = await StartAsync(await SharedStateTarget.CreateAsync(fixture), new ManualClock(Start));

        using var first = await server.Client.PostAsync($"/__test/attempts/ins_a/sign-in/user_1?maximum={int.MaxValue}", null);
        using var second = await server.Client.PostAsync($"/__test/attempts/ins_a/sign-in/user_1?maximum={int.MaxValue}", null);

        await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(second.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    [Arguments("get", HttpStatusCode.ServiceUnavailable)]
    [Arguments("create", HttpStatusCode.ServiceUnavailable)]
    [Arguments("replace", HttpStatusCode.OK)]
    public async Task Storage_failures_refuse_attempts_with_503(string failing, HttpStatusCode first)
    {
        await using var server = await StartAsync(await SharedStateTarget.CreateAsync(fixture), new ManualClock(Start), inner => new FailingDocumentStore(inner, failing));

        // The first attempt creates the bucket; the second replaces it.
        using var creating = await server.Client.PostAsync("/__test/attempts/ins_a/sign-in/user_1", null);
        using var replacing = await server.Client.PostAsync("/__test/attempts/ins_a/sign-in/user_1", null);
        var body = await replacing.Content.ReadFromJsonAsync<JsonElement>();

        await Assert.That(creating.StatusCode).IsEqualTo(first);
        await Assert.That(replacing.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(body.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("temporarily_unavailable");
    }

    [Test]
    public async Task A_store_error_with_another_status_still_answers_503()
    {
        await using var server = await StartAsync(
            await SharedStateTarget.CreateAsync(fixture), new ManualClock(Start), inner => new FailingDocumentStore(inner, "get", ApiException.Internal));

        using var response = await server.Client.PostAsync("/__test/attempts/ins_a/sign-in/user_1", null);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(body.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("temporarily_unavailable");
    }

    [Test]
    public async Task Writes_that_keep_losing_stop_with_503_and_charge_nothing()
    {
        var target = await SharedStateTarget.CreateAsync(fixture);
        var clock = new ManualClock(Start);
        LosingStore? store = null;
        await using (var losing = await StartAsync(target, clock, inner => store = new LosingStore(inner)))
        {
            // The first attempt creates the bucket; every replacement after it loses, and the
            // largest maximum must not stretch the retries to billions of storage round trips.
            using var created = await losing.Client.PostAsync("/__test/attempts/ins_a/sign-in/user_1", null);
            using var exhausted = await losing.Client.PostAsync($"/__test/attempts/ins_a/sign-in/user_1?maximum={int.MaxValue}", null);
            var body = await exhausted.Content.ReadFromJsonAsync<JsonElement>();

            await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(exhausted.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
            await Assert.That(body.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("temporarily_unavailable");
            await Assert.That(store!.Rejected).IsEqualTo(64);
        }

        // Only the first attempt was charged: two of the three remain.
        await using var server = await StartAsync(target, clock);
        await Assert.That(string.Join(",", await AttemptsAsync(server, "ins_a", "sign-in", "user_1", Maximum))).IsEqualTo("True,True,False");
    }

    [Test]
    [Arguments("used")]
    [Arguments("window")]
    [Arguments("negative-used")]
    [Arguments("negative-window")]
    public async Task A_malformed_stored_bucket_admits_nothing(string damage)
    {
        var target = await SharedStateTarget.CreateAsync(fixture);
        await using var server = await StartAsync(target, new ManualClock(Start));
        using var created = await server.Client.PostAsync("/__test/attempts/ins_a/sign-in/user_1", null);

        // Storage-level damage: a member removed, or a negative count/window.
        var stored = (await target.Database.ListDocumentsAsync(AttemptLimiter.Collection)).Items.Single();
        var bucket = JsonNode.Parse(stored.Document.GetRawText())!.AsObject();
        if (damage.StartsWith("negative-", StringComparison.Ordinal))
        {
            bucket[damage["negative-".Length..]] = -1;
        }
        else
        {
            bucket.Remove(damage);
        }

        await target.Database.PutDocumentAsync(AttemptLimiter.Collection, stored.Id, JsonSerializer.SerializeToElement(bucket), TerraBasePrecondition.Matches(stored.Version));
        using var attempt = await server.Client.PostAsync("/__test/attempts/ins_a/sign-in/user_1", null);
        var body = await attempt.Content.ReadFromJsonAsync<JsonElement>();

        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(attempt.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(body.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("temporarily_unavailable");
    }

    [Test]
    public async Task Unreachable_storage_refuses_attempts_with_503()
    {
        await using var server = await StartAsync(target: null, new ManualClock(Start));

        using var response = await server.Client.PostAsync("/__test/attempts/ins_a/sign-in/user_1", null);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(body.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("temporarily_unavailable");
    }

    /// <summary>A store whose conditional replacements always lose, as under endless contention.</summary>
    private sealed class LosingStore(IDocumentStore inner) : IDocumentStore
    {
        private int rejected;

        public int Rejected => Volatile.Read(ref this.rejected);

        public Task<Versioned<T>?> GetAsync<T>(string collection, string instanceId, string key, CancellationToken cancellationToken) =>
            inner.GetAsync<T>(collection, instanceId, key, cancellationToken);

        public Task<bool> TryCreateAsync<T>(string collection, string instanceId, string key, T document, CancellationToken cancellationToken) =>
            inner.TryCreateAsync(collection, instanceId, key, document, cancellationToken);

        // Stops an unbounded retry loop well past the ceiling so the test fails instead of hanging.
        public Task<bool> TryReplaceAsync<T>(string collection, string instanceId, string key, T document, string version, CancellationToken cancellationToken) =>
            Interlocked.Increment(ref this.rejected) > 1000 ? throw new InvalidOperationException("unbounded retries") : Task.FromResult(false);
    }

    /// <summary>Lets another replica admit an attempt between each caller read and its conditional write.</summary>
    private sealed class ContendedStore : IDocumentStore
    {
        private IDocumentStore inner = null!;
        private int losses;

        public int Losses
        {
            set => Volatile.Write(ref this.losses, value);
        }

        public ContendedStore Over(IDocumentStore real)
        {
            this.inner = real;
            return this;
        }

        public Task<Versioned<T>?> GetAsync<T>(string collection, string instanceId, string key, CancellationToken cancellationToken) =>
            this.inner.GetAsync<T>(collection, instanceId, key, cancellationToken);

        public Task<bool> TryCreateAsync<T>(string collection, string instanceId, string key, T document, CancellationToken cancellationToken) =>
            this.inner.TryCreateAsync(collection, instanceId, key, document, cancellationToken);

        public async Task<bool> TryReplaceAsync<T>(string collection, string instanceId, string key, T document, string version, CancellationToken cancellationToken)
        {
            if (Interlocked.Decrement(ref this.losses) >= 0 && await this.inner.GetAsync<AttemptBucket>(collection, instanceId, key, cancellationToken) is { } other)
            {
                await this.inner.TryReplaceAsync(collection, instanceId, key, other.Document with { Used = other.Document.Used + 1 }, other.Version, cancellationToken);
            }

            return await this.inner.TryReplaceAsync(collection, instanceId, key, document, version, cancellationToken);
        }
    }

    private static async Task<bool> AttemptAsync(AuthTestServer server, string instance, string purpose, string subject)
    {
        using var response = await server.Client.PostAsync($"/__test/attempts/{instance}/{purpose}/{subject}", null);
        return response.StatusCode switch
        {
            HttpStatusCode.OK => true,
            HttpStatusCode.TooManyRequests => false,
            var other => throw new InvalidOperationException($"unexpected status {other}"),
        };
    }

    private static async Task<bool[]> AttemptsAsync(AuthTestServer server, string instance, string purpose, string subject, int count)
    {
        var results = new bool[count];
        for (var index = 0; index < count; index++)
        {
            results[index] = await AttemptAsync(server, instance, purpose, subject);
        }

        return results;
    }
}
