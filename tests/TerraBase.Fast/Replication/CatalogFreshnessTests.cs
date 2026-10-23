using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine.Cluster;
using TerraBase.Engine.Lattices;
using TerraBase.Features.Replication;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Replication;

public sealed class CatalogFreshnessTests
{
    [Test]
    public async Task A_proof_that_expires_during_its_incarnation_read_is_not_fresh()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var window = TimeSpan.FromSeconds(10);
        var members = new FakeMemberStore();
        var localName = EntityIds.New().ToString();
        var remoteName = EntityIds.New().ToString();
        ExpiringStates? delayed = null;
        await using var local = await TerraBaseTestServer.StartAsync(settings => settings["node-name"] = localName, services =>
        {
            services.AddSingleton<TimeProvider>(clock);
            services.AddSingleton<IMemberDirectory>(members);
            var registration = services.Last(service => service.ServiceType == typeof(IReplicaStates));
            services.AddSingleton<IReplicaStates>(provider =>
                delayed = new ExpiringStates((IReplicaStates)registration.ImplementationFactory!(provider), clock, window));
        });
        await using var remote = await TerraBaseTestServer.StartAsync(settings => settings["node-name"] = remoteName, services =>
        {
            services.AddSingleton<TimeProvider>(clock);
            services.AddSingleton<IMemberDirectory>(members);
        });
        clock.Advance(TimeSpan.FromSeconds(6));
        await Assert.That(await local.Services.GetRequiredService<CatalogGate>().VerifyAsync(token)).IsTrue();
        await Assert.That(await remote.Services.GetRequiredService<CatalogGate>().VerifyAsync(token)).IsTrue();
        var peer = (await members.ListAsync(token)).Single(member => member.Name == remoteName);
        var repair = local.Services.GetRequiredService<AntiEntropy>();
        await Assert.That(await repair.PullAsync(ChangeGroups.System, peer, token)).IsNotNull();
        var freshness = local.Services.GetRequiredService<ICatalogFreshness>();
        clock.Advance(TimeSpan.FromSeconds(9));
        await Assert.That(await freshness.IsFreshAsync(window, token)).IsTrue();

        // Background incarnation reads must not consume the expiry intended for this freshness check.
        using var expiry = CancellationTokenSource.CreateLinkedTokenSource(token);
        delayed!.ExpireOn = expiry.Token;
        await Assert.That(await freshness.IsFreshAsync(window, expiry.Token)).IsFalse();
        await Assert.That(delayed.Expired).IsTrue();

        await Assert.That(await repair.PullAsync(ChangeGroups.System, peer, token)).IsNotNull();
        await Assert.That(await freshness.IsFreshAsync(window, token)).IsTrue();
    }

    private sealed class ExpiringStates(IReplicaStates inner, FakeTimeProvider clock, TimeSpan window) : IReplicaStates
    {
        private int expired;

        public CancellationToken ExpireOn { get; set; }

        public bool Expired => Volatile.Read(ref this.expired) != 0;

        public ValueTask<ReplicaDigest?> DigestAsync(TenantContext database, Ulid? expected, CancellationToken cancellationToken) =>
            inner.DigestAsync(database, expected, cancellationToken);

        public ValueTask<bool> MatchedAsync(TenantContext database, ReplicaDigest digest, CancellationToken cancellationToken) =>
            inner.MatchedAsync(database, digest, cancellationToken);

        public async ValueTask<(IReadOnlyList<RepairState> States, BucketCursor? Next)?> ReadBucketsAsync(
            TenantContext database, Ulid incarnation, IReadOnlyList<int> buckets, BucketCursor? after, int maxStates, int maxBytes, CancellationToken cancellationToken)
        {
            var page = await inner.ReadBucketsAsync(database, incarnation, buckets, after, maxStates, maxBytes, cancellationToken);
            if (ExpireOn.CanBeCanceled && cancellationToken == ExpireOn && database == ChangeGroups.System && buckets.Count == 0
                && Interlocked.Exchange(ref this.expired, 1) == 0)
            {
                // Even a concurrent refill's newer proof must expire before this incarnation read returns.
                clock.Advance(window);
            }

            return page;
        }

        public ValueTask<Hlc> StableAsync(TenantContext database, CancellationToken cancellationToken) => inner.StableAsync(database, cancellationToken);

        public ValueTask<Hlc> HorizonAsync(TenantContext database, CancellationToken cancellationToken) => inner.HorizonAsync(database, cancellationToken);

        public ValueTask<int?> ForgetAsync(
            TenantContext database, Ulid incarnation, IReadOnlyList<(string Collection, string Id)> keys, Hlc horizon, CancellationToken cancellationToken) =>
            inner.ForgetAsync(database, incarnation, keys, horizon, cancellationToken);
    }
}
