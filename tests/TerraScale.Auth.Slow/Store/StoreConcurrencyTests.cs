using TerraScale.Auth.Store;
using TerraScale.Auth.Tests;
using TerraScale.DotNet.TerraBase;
using static TerraScale.Auth.Tests.TerraBaseFixture;

namespace TerraScale.Auth.Slow.Store;

[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class StoreConcurrencyTests(TerraBaseFixture fixture)
{
    [Test]
    public async Task Concurrent_independent_stores_have_exactly_one_unique_claim_winner()
    {
        var (tenant, database) = await fixture.CreateDatabaseAsync();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = Enumerable.Range(0, 32).Select(async index =>
        {
            var store = new AuthStore(fixture.Server.Documents(tenant.ApiKey).Database(tenant.Project.Id, "auth"), TimeProvider.System);
            await start.Task;
            return await store.ClaimUniqueAsync("tenant", "email", "contended@example.test", $"owner-{index}");
        }).ToArray();
        start.SetResult();
        var results = await Task.WhenAll(attempts);

        await Assert.That(results.Count(won => won)).IsEqualTo(1);
        await Assert.That((await database.ListDocumentsAsync("uniques")).Items.Count).IsEqualTo(1);
        var winner = Array.IndexOf(results, true);
        var independent = new AuthStore(fixture.Server.Documents(tenant.ApiKey).Database(tenant.Project.Id, "auth"), TimeProvider.System);
        await Assert.That(await independent.ReleaseUniqueAsync("tenant", "email", "contended@example.test", $"owner-{winner}")).IsTrue();
    }

    [Test]
    public async Task Concurrent_independent_stores_consume_a_challenge_exactly_once()
    {
        var (tenant, database) = await fixture.CreateDatabaseAsync();
        var clock = new StoreTestClock();
        var hash = new string('b', 64);
        await new AuthStore(database, clock).CreateChallengeAsync("tenant", "recovery", hash, "subject", clock.UtcNow.AddMinutes(5));
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = Enumerable.Range(0, 32).Select(async _ =>
        {
            var store = new AuthStore(fixture.Server.Documents(tenant.ApiKey).Database(tenant.Project.Id, "auth"), clock);
            await start.Task;
            return await store.ConsumeChallengeAsync("tenant", "recovery", hash, "subject");
        }).ToArray();
        start.SetResult();
        var results = await Task.WhenAll(attempts);

        await Assert.That(results.Count(result => result is not null)).IsEqualTo(1);
        await Assert.That(results.Single(result => result is not null)!.Consumed).IsTrue();
        var retained = (await database.ListDocumentsAsync("verification_tokens")).Items.Single();
        await Assert.That(retained.Document.GetProperty("consumed").GetBoolean()).IsTrue();
        var independent = new AuthStore(fixture.Server.Documents(tenant.ApiKey).Database(tenant.Project.Id, "auth"), clock);
        await Assert.That(await independent.ConsumeChallengeAsync("tenant", "recovery", hash, "subject")).IsNull();
        await Assert.ThrowsAsync<TerraBasePreconditionFailedException>(() => independent.CreateChallengeAsync("tenant", "recovery", hash, "subject", clock.UtcNow.AddHours(1)));
    }

    [Test]
    public async Task Stale_deletion_cannot_remove_a_replacement_at_the_same_scoped_key()
    {
        var (tenant, database) = await fixture.CreateDatabaseAsync();
        await database.CreateCollectionAsync("items");
        var first = new AuthStore(database, TimeProvider.System);
        var second = new AuthStore(fixture.Server.Documents(tenant.ApiKey).Database(tenant.Project.Id, "auth"), TimeProvider.System);
        var original = await first.CreateAsync("items", "tenant", "replaceable", Json("""{"owner":"original"}"""));
        await Assert.That(await second.CompareAndDeleteAsync("items", "tenant", "replaceable", original.Version)).IsTrue();
        var replacement = await second.CreateAsync("items", "tenant", "replaceable", Json("""{"owner":"replacement"}"""));
        await Assert.That(replacement.Version).IsNotEqualTo(original.Version);

        var attempts = Enumerable.Range(0, 32).Select(_ =>
            new AuthStore(fixture.Server.Documents(tenant.ApiKey).Database(tenant.Project.Id, "auth"), TimeProvider.System)
                .CompareAndDeleteAsync("items", "tenant", "replaceable", original.Version));
        await Assert.That((await Task.WhenAll(attempts)).Any(deleted => deleted)).IsFalse();
        var current = await first.GetAsync("items", "tenant", "replaceable");
        await Assert.That(current!.Version).IsEqualTo(replacement.Version);
        await Assert.That(current.Document.GetProperty("owner").GetString()).IsEqualTo("replacement");
    }
}
