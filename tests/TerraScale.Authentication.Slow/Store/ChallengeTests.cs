using System.Security.Cryptography;
using System.Text;
using TerraScale.Authentication.Store;
using TerraScale.Authentication.Tests;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Authentication.Slow.Store;

[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class ChallengeTests(TerraBaseFixture fixture)
{
    [Test]
    public async Task Expiry_after_a_committed_consume_withholds_the_proof_and_retains_consumption()
    {
        var (_, database) = await fixture.CreateDatabaseAsync();
        var now = new DateTimeOffset(2035, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var expires = now.AddMinutes(5);
        var store = new AuthStore(database, new ExpiringClock(now, expires));
        var hash = new string('d', 64);
        await store.CreateChallengeAsync("tenant", "recovery", hash, "subject", expires);

        await Assert.That(await store.ConsumeChallengeAsync("tenant", "recovery", hash, "subject")).IsNull();
        var retained = (await database.ListDocumentsAsync("verification_tokens")).Items.Single();
        await Assert.That(retained.Document.GetProperty("consumed").GetBoolean()).IsTrue();
    }

    private sealed class ExpiringClock(DateTimeOffset initial, DateTimeOffset expires) : TimeProvider
    {
        private int observations;

        // Creation and eligibility see the initial instant; the post-write observation sees expiry.
        public override DateTimeOffset GetUtcNow() => Interlocked.Increment(ref this.observations) <= 2 ? initial : expires;
    }

    [Test]
    public async Task Two_independent_consumers_have_exactly_one_winner()
    {
        var (tenant, database) = await fixture.CreateDatabaseAsync();
        var first = new AuthStore(database, TimeProvider.System);
        var second = new AuthStore(fixture.Server.Documents(tenant.ApiKey).Database(tenant.Project.Id, "auth"), TimeProvider.System);
        var hash = new string('c', 64);
        await first.CreateChallengeAsync("tenant", "recovery", hash, "subject", DateTimeOffset.UtcNow.AddMinutes(5));

        var results = await Task.WhenAll(
            first.ConsumeChallengeAsync("tenant", "recovery", hash, "subject"),
            second.ConsumeChallengeAsync("tenant", "recovery", hash, "subject"));

        await Assert.That(results.Count(result => result is not null)).IsEqualTo(1);
        await Assert.That(await second.ConsumeChallengeAsync("tenant", "recovery", hash, "subject")).IsNull();
    }

    [Test]
    public async Task Challenges_validate_every_binding_and_remain_consumed_across_clients()
    {
        const string secret = "synthetic-verification-bearer-secret";
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
        var (tenant, database) = await fixture.CreateDatabaseAsync();
        var clock = new StoreTestClock();
        var first = new AuthStore(database, clock);
        var second = new AuthStore(fixture.Server.Documents(tenant.ApiKey).Database(tenant.Project.Id, "auth"), clock);
        var expires = clock.UtcNow.AddMinutes(5);
        await first.CreateChallengeAsync("tenant-a", "verification", hash, "subject-a", expires);

        var stored = (await database.ListDocumentsAsync("verification_tokens")).Items.Single();
        await Assert.That(stored.Document.GetRawText()).Contains(hash);
        await Assert.That(stored.Document.GetRawText()).DoesNotContain(secret);
        await Assert.That(await second.ConsumeChallengeAsync("tenant-b", "verification", hash, "subject-a")).IsNull();
        await Assert.That(await second.ConsumeChallengeAsync("tenant-a", "recovery", hash, "subject-a")).IsNull();
        await Assert.That(await second.ConsumeChallengeAsync("tenant-a", "verification", hash, "subject-b")).IsNull();
        await Assert.That(await second.ConsumeChallengeAsync("tenant-a", "verification", new string('0', 64), "subject-a")).IsNull();

        var consumed = await second.ConsumeChallengeAsync("tenant-a", "verification", hash, "subject-a");
        await Assert.That(consumed).IsNotNull();
        await Assert.That(consumed!.InstanceId).IsEqualTo("tenant-a");
        await Assert.That(consumed.Purpose).IsEqualTo("verification");
        await Assert.That(consumed.TokenHash).IsEqualTo(hash);
        await Assert.That(consumed.SubjectId).IsEqualTo("subject-a");
        await Assert.That(consumed.ExpiresAt).IsEqualTo(expires);
        await Assert.That(consumed.Consumed).IsTrue();
        await Assert.That(await first.ConsumeChallengeAsync("tenant-a", "verification", hash, "subject-a")).IsNull();
        await Assert.ThrowsAsync<TerraBasePreconditionFailedException>(() => first.CreateChallengeAsync("tenant-a", "verification", hash, "subject-a", expires.AddHours(1)));

        var restarted = new AuthStore(fixture.Server.Documents(tenant.ApiKey).Database(tenant.Project.Id, "auth"), clock);
        await Assert.That(await restarted.ConsumeChallengeAsync("tenant-a", "verification", hash, "subject-a")).IsNull();
        var retained = (await database.ListDocumentsAsync("verification_tokens")).Items.Single();
        await Assert.That(retained.Version).IsNotEqualTo(stored.Version);
        await Assert.That(retained.Document.GetProperty("consumed").GetBoolean()).IsTrue();
        await Assert.That(retained.Document.GetRawText()).Contains(hash);
        await Assert.That(retained.Document.GetRawText()).DoesNotContain(secret);
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(60)]
    public async Task Expired_challenges_are_rejected_at_the_exact_expiry_boundary(int secondsAfterExpiry)
    {
        var (_, database) = await fixture.CreateDatabaseAsync();
        var clock = new StoreTestClock();
        var store = new AuthStore(database, clock);
        var hash = new string('a', 64);
        var expires = clock.UtcNow.AddMinutes(5);
        await store.CreateChallengeAsync("tenant", "recovery", hash, "subject", expires);
        var before = (await database.ListDocumentsAsync("verification_tokens")).Items.Single();
        clock.UtcNow = expires.AddSeconds(secondsAfterExpiry);

        await Assert.That(await store.ConsumeChallengeAsync("tenant", "recovery", hash, "subject")).IsNull();
        var after = (await database.ListDocumentsAsync("verification_tokens")).Items.Single();
        await Assert.That(after.Version).IsEqualTo(before.Version);
        await Assert.ThrowsAsync<TerraBasePreconditionFailedException>(() => store.CreateChallengeAsync("tenant", "recovery", hash, "subject", clock.UtcNow.AddHours(1)));
    }

    [Test]
    [Arguments("plaintext-secret")]
    [Arguments("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [Arguments("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [Arguments("gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    public async Task Challenges_require_a_lowercase_sha256_digest(string invalidHash)
    {
        var (_, database) = await fixture.CreateDatabaseAsync();
        var clock = new StoreTestClock();
        var store = new AuthStore(database, clock);

        await Assert.ThrowsAsync<ArgumentException>(() => store.CreateChallengeAsync("tenant", "verification", invalidHash, "subject", clock.UtcNow.AddMinutes(5)));
        await Assert.ThrowsAsync<ArgumentException>(() => store.ConsumeChallengeAsync("tenant", "verification", invalidHash, "subject"));
        await Assert.That((await database.ListDocumentsAsync("verification_tokens")).Items.Count).IsEqualTo(0);
    }
}
