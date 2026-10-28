using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.RateLimiting;
using TerraScale.Authentication.Security;
using TerraScale.Authentication.Store;
using TerraScale.Authentication.Tests;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Authentication.Fast.Security;

/// <summary>
/// The shared attempt limiter: fixed windows and elapsed cooldowns in TerraBase, scoped by instance, purpose
/// and subject. Test routes admit an attempt with 200 or refuse it with 429; the decision route
/// also reports the persisted expiry's remaining delay as Retry-After.
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
        routes: app =>
        {
            app.MapPost("/__test/attempts/{instance}/{purpose}/{subject}", async (string instance, string purpose, string subject, int? maximum, bool? cooldown, AttemptLimiter limiter, CancellationToken cancellationToken) =>
                await (cooldown == true
                    ? limiter.TryAcquireCooldownAsync(instance, purpose, subject, TimeSpan.FromMinutes(1), cancellationToken)
                    : limiter.TryAcquireAsync(instance, purpose, subject, maximum ?? Maximum, cancellationToken))
                    ? Results.Ok()
                    : Results.StatusCode(StatusCodes.Status429TooManyRequests));
            app.MapPost("/__test/decisions/{instance}/{purpose}/{subject}", async (string instance, string purpose, string subject, int? windowSeconds, bool? cooldown, HttpContext context, AttemptLimiter limiter, CancellationToken cancellationToken) =>
            {
                var decision = cooldown == true
                    ? await limiter.AcquireCooldownAsync(instance, purpose, subject, TimeSpan.FromSeconds(windowSeconds ?? 60), cancellationToken)
                    : await limiter.AcquireAsync(instance, purpose, subject, Maximum, TimeSpan.FromSeconds(windowSeconds ?? 60), cancellationToken);
                if (decision is not { } retryAfter)
                {
                    return Results.Ok();
                }

                context.Response.Headers.RetryAfter = ((long)retryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);
                return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            });
            app.MapPost("/__test/notices/{instance}/{purpose}/{userId}", async (string instance, string purpose, string userId, long until, AttemptLimiter limiter, CancellationToken cancellationToken) =>
                await limiter.ClaimLockoutNoticeAsync(instance, purpose, userId, DateTimeOffset.FromUnixTimeSeconds(until), cancellationToken)
                    ? Results.Ok()
                    : Results.Conflict());
        });

    [Test]
    public async Task Notice_claims_are_atomic_scoped_and_never_reclaim_an_older_deadline()
    {
        var target = await SharedStateTarget.CreateAsync(fixture);
        var clock = new ManualClock(Start);
        await using var first = await StartAsync(target, clock);
        await using var second = await StartAsync(target, new ManualClock(Start.AddSeconds(-7)));
        var until = Start.AddMinutes(1).ToUnixTimeSeconds();
        var claims = await Task.WhenAll(Enumerable.Range(0, 12).Select(index => ClaimNoticeAsync(index % 2 == 0 ? first : second, "ins_a", "password", "user_1", until)));
        await Assert.That(claims.Count(claimed => claimed)).IsEqualTo(1);

        var original = (await target.Database.ListDocumentsAsync(AttemptLimiter.Collection)).Items.Single();
        await Assert.That(original.Document.GetProperty("window").GetInt64()).IsEqualTo(until);
        await Assert.That(await ClaimNoticeAsync(second, "ins_a", "password", "user_1", until - 1)).IsFalse();
        await Assert.That((await target.Database.ListDocumentsAsync(AttemptLimiter.Collection)).Items.Single().Version).IsEqualTo(original.Version);

        await Assert.That(await ClaimNoticeAsync(first, "ins_a", "password", "user_1", until + 60)).IsTrue();
        await Assert.That(await ClaimNoticeAsync(second, "ins_a", "password", "user_1", until)).IsFalse();
        clock.Advance(TimeSpan.FromMinutes(2));
        await Assert.That(await ClaimNoticeAsync(first, "ins_a", "password", "user_1", until + 120)).IsTrue();
        clock.Advance(TimeSpan.FromMinutes(1));
        await Assert.That(await ClaimNoticeAsync(first, "ins_a", "password", "user_2", until + 120)).IsFalse();

        var future = until + 240;
        await Assert.That(await ClaimNoticeAsync(first, "ins_b", "password", "user_1", future)).IsTrue();
        await Assert.That(await ClaimNoticeAsync(first, "ins_a", "reset", "user_1", future)).IsTrue();
        await Assert.That(await ClaimNoticeAsync(first, "ins_a", "password", "user_2", future)).IsTrue();
        await Assert.That(await target.CountAsync(AttemptLimiter.Collection)).IsEqualTo(4);
    }

    [Test]
    [Arguments("get", false)]
    [Arguments("get", true)]
    [Arguments("create", false)]
    [Arguments("replace", true)]
    [Arguments("contended-replace", true)]
    public async Task Notice_claims_that_expire_during_storage_never_authorize_a_send(string operation, bool existing)
    {
        var target = await SharedStateTarget.CreateAsync(fixture);
        var clock = new ManualClock(Start);
        await using var server = await StartAsync(target, clock, inner => new ClockAdvancingStore(inner, clock));
        var until = Start.AddMinutes(1).ToUnixTimeSeconds();
        if (existing)
        {
            await Assert.That(await ClaimNoticeAsync(server, "ins_a", "password", "user_1", until - 2)).IsTrue();
        }

        var previousVersion = existing ? (await target.Database.ListDocumentsAsync(AttemptLimiter.Collection)).Items.Single().Version : null;
        var store = (ClockAdvancingStore)server.App.Services.GetRequiredService<IDocumentStore>();
        store.AdvanceAfter = operation;
        using var response = await server.Client.PostAsync($"/__test/notices/ins_a/password/user_1?until={until}", null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(clock.GetUtcNow()).IsEqualTo(DateTimeOffset.FromUnixTimeSeconds(until));
        // A lost write that crosses expiry must stop before another storage round trip.
        await Assert.That(store.Reads).IsEqualTo(existing ? 2 : 1);
        var stored = (await target.Database.ListDocumentsAsync(AttemptLimiter.Collection)).Items;
        await Assert.That(stored.Count).IsEqualTo(existing || operation == "create" ? 1 : 0);
        if (existing || operation == "create")
        {
            var expectedDeadline = operation switch
            {
                "get" => until - 2,
                "contended-replace" => until - 1,
                _ => until,
            };
            await Assert.That(stored.Single().Document.GetProperty("window").GetInt64()).IsEqualTo(expectedDeadline);
            await Assert.That(stored.Single().Document.GetProperty("used").GetInt32()).IsEqualTo(1);
            if (operation == "get")
            {
                await Assert.That(stored.Single().Version).IsEqualTo(previousVersion);
            }
        }

        // A claim persisted during expiry still permits a subsequent, live lockout episode.
        await Assert.That(await ClaimNoticeAsync(server, "ins_a", "password", "user_1", until + 60)).IsTrue();
    }

    [Test]
    [Arguments("get", HttpStatusCode.ServiceUnavailable)]
    [Arguments("create", HttpStatusCode.ServiceUnavailable)]
    [Arguments("replace", HttpStatusCode.OK)]
    public async Task A_failed_notice_claim_never_authorizes_a_send(string failing, HttpStatusCode first)
    {
        var target = await SharedStateTarget.CreateAsync(fixture);
        await using var server = await StartAsync(target, new ManualClock(Start), inner => new FailingDocumentStore(inner, failing));
        var until = Start.AddMinutes(1).ToUnixTimeSeconds();
        using var creating = await server.Client.PostAsync($"/__test/notices/ins_a/password/user_1?until={until}", null);
        using var replacing = await server.Client.PostAsync($"/__test/notices/ins_a/password/user_1?until={until + 60}", null);
        await Assert.That(creating.StatusCode).IsEqualTo(first);
        await Assert.That(replacing.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That((await replacing.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetProperty("code").GetString()).IsEqualTo("temporarily_unavailable");
    }

    [Test]
    [Arguments("window")]
    [Arguments("used")]
    [Arguments("overflow-window")]
    public async Task A_damaged_notice_claim_never_authorizes_a_send(string damage)
    {
        var target = await SharedStateTarget.CreateAsync(fixture);
        await using var server = await StartAsync(target, new ManualClock(Start));
        var until = Start.AddMinutes(1).ToUnixTimeSeconds();
        await Assert.That(await ClaimNoticeAsync(server, "ins_a", "password", "user_1", until)).IsTrue();
        var stored = (await target.Database.ListDocumentsAsync(AttemptLimiter.Collection)).Items.Single();
        var bucket = JsonNode.Parse(stored.Document.GetRawText())!.AsObject();
        bucket[damage == "used" ? "used" : "window"] = damage == "overflow-window" ? long.MaxValue : 0;
        await target.Database.PutDocumentAsync(AttemptLimiter.Collection, stored.Id, JsonSerializer.SerializeToElement(bucket), TerraBasePrecondition.Matches(stored.Version));
        using var response = await server.Client.PostAsync($"/__test/notices/ins_a/password/user_1?until={until + 60}", null);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
    }

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
    [Arguments(60)]
    [Arguments(900)]
    public async Task Decisions_report_the_persisted_window_expiry_without_extending_it(int windowSeconds)
    {
        var target = await SharedStateTarget.CreateAsync(fixture);
        var clock = new ManualClock(Start);
        await using var server = await StartAsync(target, clock);

        for (var attempt = 0; attempt < Maximum; attempt++)
        {
            await Assert.That(await DecisionAsync(server, "ins_a", "sign-in", "user_1", windowSeconds)).IsNull();
        }

        var full = (await target.Database.ListDocumentsAsync(AttemptLimiter.Collection)).Items.Single();
        await Assert.That(full.Document.GetProperty("window").GetInt64()).IsEqualTo(Start.ToUnixTimeSeconds() / windowSeconds);
        await Assert.That(full.Document.GetProperty("used").GetInt32()).IsEqualTo(Maximum);
        await Assert.That(await DecisionAsync(server, "ins_a", "sign-in", "user_1", windowSeconds)).IsEqualTo(TimeSpan.FromSeconds(windowSeconds - 5));

        // Refusals leave the stored window unchanged; the final half-second rounds up to one.
        clock.Advance(TimeSpan.FromSeconds(windowSeconds - 5) - TimeSpan.FromMilliseconds(500));
        await Assert.That(await DecisionAsync(server, "ins_a", "sign-in", "user_1", windowSeconds)).IsEqualTo(TimeSpan.FromSeconds(1));
        var unchanged = await target.Database.GetDocumentAsync(AttemptLimiter.Collection, full.Id);
        await Assert.That(unchanged!.Version).IsEqualTo(full.Version);

        clock.Advance(TimeSpan.FromMilliseconds(500));
        await Assert.That(await DecisionAsync(server, "ins_a", "sign-in", "user_1", windowSeconds)).IsNull();
        var renewed = (await target.Database.GetDocumentAsync(AttemptLimiter.Collection, full.Id))!;
        await Assert.That(renewed.Document.GetProperty("window").GetInt64()).IsEqualTo(full.Document.GetProperty("window").GetInt64() + 1);
        await Assert.That(renewed.Document.GetProperty("used").GetInt32()).IsEqualTo(1);
    }

    [Test]
    public async Task Decisions_wait_for_an_ahead_of_clock_bucket_to_expire()
    {
        var target = await SharedStateTarget.CreateAsync(fixture);
        var behindClock = new ManualClock(Start);
        await using var ahead = await StartAsync(target, new ManualClock(Start.AddMinutes(1)));
        await using var behind = await StartAsync(target, behindClock);

        await AttemptsAsync(ahead, "ins_a", "sign-in", "user_1", Maximum - 1);
        await Assert.That(await DecisionAsync(behind, "ins_a", "sign-in", "user_1")).IsNull();
        await Assert.That(await DecisionAsync(behind, "ins_a", "sign-in", "user_1")).IsEqualTo(TimeSpan.FromSeconds(115));
        await Assert.That(await DecisionAsync(ahead, "ins_a", "sign-in", "user_1")).IsEqualTo(TimeSpan.FromSeconds(55));

        // Catching up to the persisted window's start still does not renew its budget.
        behindClock.Advance(TimeSpan.FromMinutes(1));
        await Assert.That(await DecisionAsync(behind, "ins_a", "sign-in", "user_1")).IsEqualTo(TimeSpan.FromSeconds(55));
        behindClock.Advance(TimeSpan.FromSeconds(55));
        await Assert.That(await DecisionAsync(behind, "ins_a", "sign-in", "user_1")).IsNull();
    }

    [Test]
    public async Task Cooldown_decisions_report_elapsed_remaining_time_without_extending_it()
    {
        var target = await SharedStateTarget.CreateAsync(fixture);
        var start = Start.AddSeconds(55).AddMilliseconds(-1);
        var clock = new ManualClock(start);
        await using var server = await StartAsync(target, clock);

        await Assert.That(await AttemptAsync(server, "ins_a", "mail-cooldown", "user_1", cooldown: true)).IsTrue();
        var full = (await target.Database.ListDocumentsAsync(AttemptLimiter.Collection)).Items.Single();
        await Assert.That(full.Document.GetProperty("window").GetInt64()).IsEqualTo(start.ToUnixTimeMilliseconds());
        await Assert.That(await DecisionAsync(server, "ins_a", "mail-cooldown", "user_1", cooldown: true)).IsEqualTo(TimeSpan.FromSeconds(60));

        // Crossing a fixed minute boundary does not reset an elapsed cooldown.
        clock.Advance(TimeSpan.FromMilliseconds(1));
        await Assert.That(await DecisionAsync(server, "ins_a", "mail-cooldown", "user_1", cooldown: true)).IsEqualTo(TimeSpan.FromSeconds(60));
        clock.Advance(TimeSpan.FromMilliseconds(59499));
        await Assert.That(await DecisionAsync(server, "ins_a", "mail-cooldown", "user_1", cooldown: true)).IsEqualTo(TimeSpan.FromSeconds(1));
        var unchanged = await target.Database.GetDocumentAsync(AttemptLimiter.Collection, full.Id);
        await Assert.That(unchanged!.Version).IsEqualTo(full.Version);

        clock.Advance(TimeSpan.FromMilliseconds(500));
        await Assert.That(await DecisionAsync(server, "ins_a", "mail-cooldown", "user_1", cooldown: true)).IsNull();
        var renewed = (await target.Database.GetDocumentAsync(AttemptLimiter.Collection, full.Id))!;
        await Assert.That(renewed.Document.GetProperty("window").GetInt64()).IsEqualTo(start.AddMinutes(1).ToUnixTimeMilliseconds());
        await Assert.That(renewed.Document.GetProperty("used").GetInt32()).IsEqualTo(1);
        await Assert.That(await AttemptAsync(server, "ins_a", "mail-cooldown", "user_1", cooldown: true)).IsFalse();
    }

    [Test]
    public async Task Cooldown_decisions_wait_for_an_ahead_of_clock_admission_to_expire()
    {
        var target = await SharedStateTarget.CreateAsync(fixture);
        var behindClock = new ManualClock(Start);
        await using var ahead = await StartAsync(target, new ManualClock(Start.AddMinutes(1)));
        await using var behind = await StartAsync(target, behindClock);

        await Assert.That(await DecisionAsync(ahead, "ins_a", "mail-cooldown", "user_1", cooldown: true)).IsNull();
        await Assert.That(await DecisionAsync(behind, "ins_a", "mail-cooldown", "user_1", cooldown: true)).IsEqualTo(TimeSpan.FromSeconds(120));
        await Assert.That(await DecisionAsync(ahead, "ins_a", "mail-cooldown", "user_1", cooldown: true)).IsEqualTo(TimeSpan.FromSeconds(60));

        // Catching up to the last admission still leaves the entire elapsed interval.
        behindClock.Advance(TimeSpan.FromMinutes(1));
        await Assert.That(await DecisionAsync(behind, "ins_a", "mail-cooldown", "user_1", cooldown: true)).IsEqualTo(TimeSpan.FromSeconds(60));
        behindClock.Advance(TimeSpan.FromMilliseconds(59500));
        await Assert.That(await DecisionAsync(behind, "ins_a", "mail-cooldown", "user_1", cooldown: true)).IsEqualTo(TimeSpan.FromSeconds(1));
        behindClock.Advance(TimeSpan.FromMilliseconds(500));
        await Assert.That(await DecisionAsync(behind, "ins_a", "mail-cooldown", "user_1", cooldown: true)).IsNull();
        await Assert.That(await DecisionAsync(ahead, "ins_a", "mail-cooldown", "user_1", cooldown: true)).IsEqualTo(TimeSpan.FromSeconds(120));
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
    [Arguments("get", "True,True,True,False,False")]
    [Arguments("create", "True,True,True,False,False")]
    [Arguments("replace", "True,True,True,True,False")]
    public async Task Storage_outages_fall_back_to_this_replicas_memory_and_keep_the_limit(string failing, string expected)
    {
        await using var server = await StartAsync(await SharedStateTarget.CreateAsync(fixture), new ManualClock(Start), inner => new FailingDocumentStore(inner, failing));

        // A timed-out storage call runs the same budget on this replica's memory: sign-in stays up, brute force stays
        // limited. With only replacements failing, the first attempt is stored and memory then counts afresh.
        await Assert.That(string.Join(",", await AttemptsAsync(server, "ins_a", "sign-in", "user_1", Maximum + 2))).IsEqualTo(expected);
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
    [Arguments("used", false)]
    [Arguments("window", false)]
    [Arguments("negative-used", false)]
    [Arguments("negative-window", false)]
    [Arguments("overflow-window", false)]
    [Arguments("out-of-range-window", false)]
    [Arguments("used", true)]
    [Arguments("window", true)]
    [Arguments("negative-used", true)]
    [Arguments("negative-window", true)]
    [Arguments("overflow-window", true)]
    [Arguments("out-of-range-window", true)]
    public async Task A_malformed_stored_bucket_admits_nothing(string damage, bool cooldown)
    {
        var target = await SharedStateTarget.CreateAsync(fixture);
        await using var server = await StartAsync(target, new ManualClock(Start));
        using var created = await server.Client.PostAsync($"/__test/attempts/ins_a/sign-in/user_1?cooldown={cooldown}", null);

        // Storage-level damage: missing members, negative values, or an unrepresentable end.
        var stored = (await target.Database.ListDocumentsAsync(AttemptLimiter.Collection)).Items.Single();
        var bucket = JsonNode.Parse(stored.Document.GetRawText())!.AsObject();
        if (damage is "overflow-window" or "out-of-range-window")
        {
            bucket["window"] = damage == "overflow-window" ? long.MaxValue : cooldown
                ? DateTimeOffset.MaxValue.AddMinutes(-1).ToUnixTimeMilliseconds() + 1
                : DateTimeOffset.MaxValue.ToUnixTimeSeconds() / 60;
        }
        else if (damage.StartsWith("negative-", StringComparison.Ordinal))
        {
            bucket[damage["negative-".Length..]] = -1;
        }
        else
        {
            bucket.Remove(damage);
        }

        await target.Database.PutDocumentAsync(AttemptLimiter.Collection, stored.Id, JsonSerializer.SerializeToElement(bucket), TerraBasePrecondition.Matches(stored.Version));
        var damaged = await target.Database.GetDocumentAsync(AttemptLimiter.Collection, stored.Id);
        using var attempt = await server.Client.PostAsync($"/__test/attempts/ins_a/sign-in/user_1?cooldown={cooldown}", null);
        using var decision = await server.Client.PostAsync($"/__test/decisions/ins_a/sign-in/user_1?cooldown={cooldown}", null);
        var body = await attempt.Content.ReadFromJsonAsync<JsonElement>();

        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(attempt.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(decision.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(body.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("temporarily_unavailable");
        var unchanged = await target.Database.GetDocumentAsync(AttemptLimiter.Collection, stored.Id);
        await Assert.That(unchanged!.Version).IsEqualTo(damaged!.Version);
    }

    [Test]
    public async Task Unreachable_storage_falls_back_to_this_replicas_memory_and_keeps_the_limit()
    {
        await using var server = await StartAsync(target: null, new ManualClock(Start));

        await Assert.That(string.Join(",", await AttemptsAsync(server, "ins_a", "sign-in", "user_1", Maximum + 1))).IsEqualTo("True,True,True,False");
    }

    /// <summary>
    /// Advances the clock after a real limiter storage operation, including a version race with another notice claim.
    /// Other collections (the background signing-ring bootstrap) pass through uncounted.
    /// </summary>
    private sealed class ClockAdvancingStore(IDocumentStore inner, ManualClock clock) : IDocumentStore
    {
        private int reads;

        public int Reads => Volatile.Read(ref this.reads);

        public string? AdvanceAfter { get; set; }

        public Task<Versioned<T>?> GetAsync<T>(string collection, string instanceId, string key, CancellationToken cancellationToken)
        {
            if (collection != AttemptLimiter.Collection)
            {
                return inner.GetAsync<T>(collection, instanceId, key, cancellationToken);
            }

            Interlocked.Increment(ref this.reads);
            return this.CompleteAsync("get", inner.GetAsync<T>(collection, instanceId, key, cancellationToken));
        }

        public Task<bool> TryCreateAsync<T>(string collection, string instanceId, string key, T document, CancellationToken cancellationToken) =>
            collection != AttemptLimiter.Collection
                ? inner.TryCreateAsync(collection, instanceId, key, document, cancellationToken)
                : this.CompleteAsync("create", inner.TryCreateAsync(collection, instanceId, key, document, cancellationToken));

        public async Task<bool> TryReplaceAsync<T>(string collection, string instanceId, string key, T document, string version, CancellationToken cancellationToken)
        {
            if (collection != AttemptLimiter.Collection)
            {
                return await inner.TryReplaceAsync(collection, instanceId, key, document, version, cancellationToken);
            }

            if (this.AdvanceAfter == "contended-replace")
            {
                // Another replica claims an intermediate deadline, so the original version loses.
                if (document is not AttemptBucket notice
                    || !await inner.TryReplaceAsync(collection, instanceId, key, notice with { Window = notice.Window - 1 }, version, cancellationToken))
                {
                    throw new InvalidOperationException("The competing notice claim must succeed.");
                }

                return await this.CompleteAsync("contended-replace", inner.TryReplaceAsync(collection, instanceId, key, document, version, cancellationToken));
            }

            return await this.CompleteAsync("replace", inner.TryReplaceAsync(collection, instanceId, key, document, version, cancellationToken));
        }

        private async Task<T> CompleteAsync<T>(string operation, Task<T> pending)
        {
            var result = await pending;
            if (this.AdvanceAfter == operation)
            {
                this.AdvanceAfter = null;
                clock.Advance(TimeSpan.FromMinutes(1));
            }

            return result;
        }
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

    private static async Task<bool> ClaimNoticeAsync(AuthTestServer server, string instance, string purpose, string userId, long until)
    {
        using var response = await server.Client.PostAsync($"/__test/notices/{instance}/{purpose}/{userId}?until={until}", null);
        return response.StatusCode switch
        {
            HttpStatusCode.OK => true,
            HttpStatusCode.Conflict => false,
            var other => throw new InvalidOperationException($"unexpected status {other}"),
        };
    }

    private static async Task<TimeSpan?> DecisionAsync(AuthTestServer server, string instance, string purpose, string subject, int windowSeconds = 60, bool cooldown = false)
    {
        using var response = await server.Client.PostAsync($"/__test/decisions/{instance}/{purpose}/{subject}?windowSeconds={windowSeconds}&cooldown={cooldown}", null);
        return response.StatusCode switch
        {
            HttpStatusCode.OK => null,
            HttpStatusCode.TooManyRequests => response.Headers.RetryAfter?.Delta ?? throw new InvalidOperationException("A refusal must include Retry-After."),
            var other => throw new InvalidOperationException($"unexpected status {other}"),
        };
    }

    private static async Task<bool> AttemptAsync(AuthTestServer server, string instance, string purpose, string subject, bool cooldown = false)
    {
        using var response = await server.Client.PostAsync($"/__test/attempts/{instance}/{purpose}/{subject}?cooldown={cooldown}", null);
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
