using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.RateLimiting;
using TerraScale.Authentication.Store;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Fast.Security;

[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class RegionalAttemptLimiterTests(TerraBaseFixture fixture)
{
    private static readonly DateTimeOffset Start = new(2026, 10, 4, 12, 0, 5, TimeSpan.Zero);

    [Test]
    public async Task Changing_a_budgets_window_starts_fresh_instead_of_locking_until_a_far_future_window()
    {
        var (_, database) = await fixture.CreateDatabaseAsync();
        var options = AuthOptions.Load(AuthTestServer.DefaultSettings());
        var clock = new ManualClock(Start);
        var limiter = new AttemptLimiter(new TerraBaseDocumentStore(new AuthStore(database, clock)), clock, options, "gru");
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await Assert.That(await limiter.AcquireWithDeadlineAsync("ins_a", "code", "user_1", 5, TimeSpan.FromMinutes(15), CancellationToken.None)).IsNull();
        }

        await Assert.That(await limiter.AcquireWithDeadlineAsync("ins_a", "code", "user_1", 5, TimeSpan.FromMinutes(15), CancellationToken.None)).IsNotNull();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            await Assert.That(await limiter.AcquireWithDeadlineAsync("ins_a", "code", "user_1", 5, TimeSpan.FromMinutes(30), CancellationToken.None)).IsNull();
        }

        var denied = await limiter.AcquireWithDeadlineAsync("ins_a", "code", "user_1", 5, TimeSpan.FromMinutes(30), CancellationToken.None);
        await Assert.That(denied?.Until).IsEqualTo(DateTimeOffset.FromUnixTimeSeconds((Start.ToUnixTimeSeconds() / 1800 + 1) * 1800));
    }

}
