using System.Text.Json;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.RateLimiting;
using TerraScale.Authentication.Store;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Fast.Security;

public sealed class RegionalAttemptLimiterTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 4, 12, 0, 5, TimeSpan.Zero);

    [Test]
    public async Task Split_failures_survive_first_window_creation_and_rollover_and_lock_every_region()
    {
        await using var regions = await RegionalTerraBaseFixture.StartAsync();
        var options = AuthOptions.Load(AuthTestServer.DefaultSettings());
        var clocks = new[] { new ManualClock(Start), new ManualClock(Start) };
        var replicas = RegionalTerraBaseFixture.Regions.Select((region, index) =>
            Enumerable.Range(0, 2).Select(_ => Limiter(regions, region, clocks[index], options)).ToArray()).ToArray();

        for (var episode = 0; episode < 2; episode++)
        {
            regions.HoldReplication();
            // Two auth replicas in each region race both the initial create and the rollover CAS.
            var attempting = replicas.SelectMany(regional => Enumerable.Range(0, 5)
                .Select(index => regional[index % regional.Length].TryAcquireAsync("ins_a", "password", "user_1", 10, CancellationToken.None))).ToArray();
            try
            {
                foreach (var region in RegionalTerraBaseFixture.Regions)
                {
                    // Writes are visible locally before their remote-durability acknowledgement.
                    await Assert.That(await ReadinessWait.UntilAsync(async () =>
                    {
                        var local = (await regions.Database(region).ListDocumentsAsync(AttemptLimiter.Collection)).Items;
                        var window = clocks[0].GetUtcNow().ToUnixTimeSeconds() / 60;
                        return local.Where(item => item.Document.GetProperty("window").GetInt64() == window)
                            .Sum(item => item.Document.GetProperty("used").GetInt32()) == 5;
                    }, TimeSpan.FromSeconds(5))).IsTrue();
                }
            }
            finally
            {
                regions.ReleaseReplication();
            }

            var attempts = await Task.WhenAll(attempting);
            await Assert.That(attempts.All(admitted => admitted)).IsTrue();
            var effectiveWindow = clocks[0].GetUtcNow().ToUnixTimeSeconds() / 60;
            await Assert.That(await ReadinessWait.UntilAsync(async () =>
            {
                foreach (var region in RegionalTerraBaseFixture.Regions)
                {
                    var copies = (await regions.Database(region).ListDocumentsAsync(AttemptLimiter.Collection)).Items;
                    if (copies.Count != 2 || copies.Any(item => item.Document.GetProperty("window").GetInt64() != effectiveWindow)
                        || copies.Sum(item => item.Document.GetProperty("used").GetInt32()) != 10)
                    {
                        return false;
                    }
                }

                return true;
            }, TimeSpan.FromSeconds(10))).IsTrue();

            var until = DateTimeOffset.FromUnixTimeSeconds((effectiveWindow + 1) * 60);
            foreach (var replica in replicas.SelectMany(regional => regional))
            {
                var denied = await replica.AcquireWithDeadlineAsync("ins_a", "password", "user_1", 10, TimeSpan.FromMinutes(1), CancellationToken.None);
                await Assert.That(denied?.Until).IsEqualTo(until);
                await Assert.That(denied?.RetryAfter).IsEqualTo(TimeSpan.FromSeconds(55));
            }

            // A slower replica must retain the future window and the same persisted notice deadline.
            var slow = Limiter(regions, "sjc", new ManualClock(clocks[1].GetUtcNow().AddMinutes(-1)), options);
            var delayed = await slow.AcquireWithDeadlineAsync("ins_a", "password", "user_1", 10, TimeSpan.FromMinutes(1), CancellationToken.None);
            await Assert.That(delayed?.Until).IsEqualTo(until);
            await Assert.That(delayed?.RetryAfter).IsEqualTo(TimeSpan.FromSeconds(115));
            foreach (var clock in clocks)
            {
                clock.Advance(TimeSpan.FromMinutes(1));
            }
        }

        // A tenant with the same purpose and subject has its own budget.
        var isolated = Limiter(regions, "gru", new ManualClock(Start.AddMinutes(1)), options);
        await Assert.That(await isolated.TryAcquireAsync("ins_a", "password", "user_1", 10, CancellationToken.None)).IsFalse();
        await Assert.That(await isolated.TryAcquireAsync("ins_b", "password", "user_1", 10, CancellationToken.None)).IsTrue();
    }

    [Test]
    public async Task Concurrent_cooldowns_keep_the_latest_deadline_and_a_slow_replica_cannot_restart_it()
    {
        await using var regions = await RegionalTerraBaseFixture.StartAsync();
        var options = AuthOptions.Load(AuthTestServer.DefaultSettings());
        var gruClock = new ManualClock(Start);
        var sjcClock = new ManualClock(Start.AddSeconds(5));
        var gru = Limiter(regions, "gru", gruClock, options);
        var sjc = Limiter(regions, "sjc", sjcClock, options);
        var interval = TimeSpan.FromMinutes(1);
        regions.HoldReplication();
        var admitting = new[] { gru.TryAcquireCooldownAsync("ins_a", "mail", "user_1", interval, CancellationToken.None),
            sjc.TryAcquireCooldownAsync("ins_a", "mail", "user_1", interval, CancellationToken.None) };
        try
        {
            foreach (var region in RegionalTerraBaseFixture.Regions)
            {
                await Assert.That(await ReadinessWait.UntilAsync(async () =>
                    (await regions.Database(region).ListDocumentsAsync(AttemptLimiter.Collection)).Items.Count == 1,
                    TimeSpan.FromSeconds(5))).IsTrue();
            }
        }
        finally
        {
            regions.ReleaseReplication();
        }

        var admissions = await Task.WhenAll(admitting);
        await Assert.That(admissions.All(admitted => admitted)).IsTrue();
        await Assert.That(await ReadinessWait.UntilAsync(async () =>
            (await regions.Database("gru").ListDocumentsAsync(AttemptLimiter.Collection)).Items.Count == 2
            && (await regions.Database("sjc").ListDocumentsAsync(AttemptLimiter.Collection)).Items.Count == 2,
            TimeSpan.FromSeconds(10))).IsTrue();
        await Assert.That(await gru.AcquireCooldownAsync("ins_a", "mail", "user_1", interval, CancellationToken.None)).IsEqualTo(TimeSpan.FromSeconds(65));
        await Assert.That(await sjc.AcquireCooldownAsync("ins_a", "mail", "user_1", interval, CancellationToken.None)).IsEqualTo(interval);
        gruClock.Advance(TimeSpan.FromSeconds(65));
        await Assert.That(await gru.TryAcquireCooldownAsync("ins_a", "mail", "user_1", interval, CancellationToken.None)).IsTrue();
        await Assert.That(await ReadinessWait.UntilAsync(async () =>
            (await regions.Database("sjc").ListDocumentsAsync(AttemptLimiter.Collection)).Items
                .Any(item => item.Document.Deserialize<AttemptBucket>()?.Window == gruClock.GetUtcNow().ToUnixTimeMilliseconds()),
            TimeSpan.FromSeconds(10))).IsTrue();
        await Assert.That(await sjc.AcquireCooldownAsync("ins_a", "mail", "user_1", interval, CancellationToken.None)).IsEqualTo(TimeSpan.FromSeconds(120));
    }

    [Test]
    public async Task Changing_a_budgets_window_starts_fresh_instead_of_locking_until_a_far_future_window()
    {
        await using var regions = await RegionalTerraBaseFixture.StartAsync();
        var options = AuthOptions.Load(AuthTestServer.DefaultSettings());
        var clock = new ManualClock(Start);
        var limiter = Limiter(regions, "gru", clock, options);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await Assert.That(await limiter.AcquireWithDeadlineAsync("ins_a", "code", "user_1", 5, TimeSpan.FromMinutes(15), CancellationToken.None)).IsNull();
        }

        await Assert.That(await limiter.AcquireWithDeadlineAsync("ins_a", "code", "user_1", 5, TimeSpan.FromMinutes(15), CancellationToken.None)).IsNotNull();

        // A release that widens the window must not read the 15-minute window number as a 30-minute one (decades ahead).
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await Assert.That(await limiter.AcquireWithDeadlineAsync("ins_a", "code", "user_1", 5, TimeSpan.FromMinutes(30), CancellationToken.None)).IsNull();
        }

        var denied = await limiter.AcquireWithDeadlineAsync("ins_a", "code", "user_1", 5, TimeSpan.FromMinutes(30), CancellationToken.None);
        await Assert.That(denied?.Until).IsEqualTo(DateTimeOffset.FromUnixTimeSeconds((Start.ToUnixTimeSeconds() / 1800 + 1) * 1800));
    }

    private static AttemptLimiter Limiter(RegionalTerraBaseFixture regions, string region, TimeProvider clock, AuthOptions options) =>
        new(new TerraBaseDocumentStore(new AuthStore(regions.Database(region), clock)), clock, options, region);
}
