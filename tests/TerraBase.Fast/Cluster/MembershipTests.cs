using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Engine.Cluster;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Cluster;

// TS-647 step 3: a database's primary on the ring of live members ships it while it holds its member lease, and a
// handover never lets two members ship the same database at once.
public sealed class MembershipTests
{
    private const int Databases = 24;

    [Test]
    public async Task Every_database_has_at_most_one_shipper_through_a_join_a_cut_off_and_a_shutdown()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
        var store = new FakeMemberStore(time);
        var databases = Enumerable.Range(0, Databases)
            .Select(_ => new TenantContext(EntityIds.New().ToString(), PlanTier.Hobby, EntityIds.New().ToString(), EntityIds.New().ToString())).ToArray();
        var members = new Dictionary<string, TerraBaseTestServer>(StringComparer.Ordinal);
        try
        {
            members["member-a"] = await MemberAsync("member-a");
            members["member-b"] = await MemberAsync("member-b");

            // Two members: once their first terms began, every database has exactly one shipper.
            var settled = await StepsAsync(4);
            await Assert.That(settled.All(shippers => shippers.Count == 1)).IsTrue();

            // A third member joins: some databases move to it, and none is ever shipped by two members meanwhile.
            members["member-c"] = await MemberAsync("member-c");
            var joined = await StepsAsync(8);
            await Assert.That(joined.All(shippers => shippers.Count == 1)).IsTrue();
            await Assert.That(joined.Count(shippers => shippers.Contains("member-c"))).IsGreaterThan(0);

            // member-b loses NATS: it stops before its key expires, and its databases move to the others.
            store.Partition("member-b");
            var cutOff = await StepsAsync(10);
            await Assert.That(cutOff.All(shippers => shippers.Count == 1 && !shippers.Contains("member-b"))).IsTrue();

            // member-c shuts down: it leaves the view, and member-a takes its databases over at the next heartbeat.
            await members["member-c"].DisposeAsync();
            _ = members.Remove("member-c");
            var left = await StepsAsync(1);
            await Assert.That(left.All(shippers => shippers.SetEquals(["member-a"]))).IsTrue();
        }
        finally
        {
            foreach (var member in members.Values)
            {
                await member.DisposeAsync();
            }
        }

        // Advances the clock in heartbeats; at every step no database has two shippers. Returns the last step's shippers.
        async Task<List<HashSet<string>>> StepsAsync(int steps)
        {
            List<HashSet<string>> shippers = [];
            for (var step = 0; step < steps; step++)
            {
                time.Advance(StoreMembership.Heartbeat);
                shippers = [.. databases.Select(_ => new HashSet<string>(StringComparer.Ordinal))];
                foreach (var (name, member) in members)
                {
                    var leases = member.Services.GetRequiredService<IShipperLeases>();
                    for (var index = 0; index < databases.Length; index++)
                    {
                        if (await leases.ShipperEpochAsync(databases[index], CancellationToken.None) is not null)
                        {
                            shippers[index].Add(name);
                        }
                    }
                }

                await Assert.That(shippers.All(names => names.Count <= 1)).IsTrue().Because($"two members shipped one database at step {step}");
            }

            return shippers;
        }

        Task<TerraBaseTestServer> MemberAsync(string node)
        {
            return TerraBaseTestServer.StartAsync(settings => settings["node-name"] = node, services =>
            {
                services.AddSingleton<TimeProvider>(time);
                services.AddSingleton<IMemberStore>(store);
            });
        }
    }

    [Test]
    public async Task A_single_member_ships_every_database_at_once()
    {
        await using var server = await TerraBaseTestServer.StartAsync();
        var leases = server.Services.GetRequiredService<IShipperLeases>();

        await Assert.That(await leases.ShipperEpochAsync(null, CancellationToken.None)).IsEqualTo(1UL);
        await Assert.That(await leases.ShipperEpochAsync(
            new TenantContext(EntityIds.New().ToString(), PlanTier.Hobby, EntityIds.New().ToString(), EntityIds.New().ToString()), CancellationToken.None)).IsEqualTo(1UL);
    }
}
