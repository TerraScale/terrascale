using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Engine.Cluster;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Cluster;

// TS-647 step 3: a database's primary on the peer directory's ring ships it while it holds its member lease,
// and a handover never lets two members ship the same database at once.
public sealed class MembershipTests
{
    private const int Databases = 24;

    [Test]
    public async Task Every_database_has_at_most_one_shipper_through_a_join_a_cut_off_and_a_shutdown()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
        var store = new FakeMemberStore();
        // Fixed ULID keys cover both handoff directions and retained primaries. A random sample can miss member-c.
        // These contexts only query ownership; they create no shared tenant data.
        var databases = Enumerable.Range(0, Databases)
            .Select(index => new TenantContext("01K6ZEG0000000000000000000", PlanTier.Hobby,
                "01K6ZEG0000000000000000001", $"01K6ZEG000000000000000{index:D4}")).ToArray();
        (int Index, string Before, string After)[] witnesses =
        [
            (0, "member-a", "member-c"),
            (1, "member-b", "member-b"),
            (3, "member-a", "member-a"),
            (5, "member-b", "member-c"),
        ];
        var members = new Dictionary<string, TerraBaseTestServer>(StringComparer.Ordinal);
        try
        {
            members["member-a"] = await MemberAsync("member-a");
            members["member-b"] = await MemberAsync("member-b");

            // Two members: once their first terms began, every database has exactly one shipper.
            var settled = await StepsAsync(4);
            await Assert.That(settled.All(shippers => shippers.Count == 1)).IsTrue();
            foreach (var (index, before, _) in witnesses)
            {
                await Assert.That(settled[index].SetEquals([before])).IsTrue().Because($"database witness {index} starts on {before}");
            }

            // A third member joins: some databases move to it, and none is ever shipped by two members meanwhile.
            members["member-c"] = await MemberAsync("member-c");
            var joined = await StepsAsync(8);
            await Assert.That(joined.All(shippers => shippers.Count == 1)).IsTrue();
            await Assert.That(joined.Count(shippers => shippers.Contains("member-c"))).IsGreaterThan(0);
            foreach (var (index, _, after) in witnesses)
            {
                await Assert.That(joined[index].SetEquals([after])).IsTrue().Because($"database witness {index} belongs to {after} after the join");
            }

            // member-b cannot read the DNS: it stops shipping once its fence passes, while the others still list it.
            store.Partition("member-b");
            var cutOff = await StepsAsync(10);
            await Assert.That(cutOff.All(shippers => shippers.Count <= 1 && !shippers.Contains("member-b"))).IsTrue();
            for (var index = 0; index < databases.Length; index++)
            {
                await Assert.That(cutOff[index].SetEquals(joined[index].Where(name => name != "member-b"))).IsTrue()
                    .Because($"only member-b's databases lose their shipper after the cut off (database {index})");
            }

            // member-b and member-c stop: member-a observes their departure and waits its fence before taking over.
            foreach (var name in (string[])["member-b", "member-c"])
            {
                await members[name].DisposeAsync();
                _ = members.Remove(name);
            }

            var left = await StepsAsync(3);
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
                time.Advance(DirectoryMembership.Heartbeat);
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
                services.AddSingleton<IMemberDirectory>(store);
            });
        }
    }

    [Test]
    public async Task Three_regions_in_the_dns_each_see_their_own_members_and_the_other_regions_members()
    {
        var store = new FakeMemberStore();
        var machines = new (string Name, string Region)[] { ("gru-a", "gru"), ("gru-b", "gru"), ("sjc-a", "sjc"), ("fra-a", "fra") };
        var servers = new List<TerraBaseTestServer>();
        try
        {
            foreach (var (name, region) in machines)
            {
                servers.Add(await TerraBaseTestServer.StartAsync(settings =>
                {
                    settings["node-name"] = name;
                    settings["region"] = region;
                }, services => services.AddSingleton<IMemberDirectory>(store)));
            }

            foreach (var (server, (name, region)) in servers.Zip(machines))
            {
                var membership = server.Services.GetRequiredService<IMembership>();
                string[] own = [.. machines.Where(machine => machine.Region == region).Select(machine => machine.Name).Order(StringComparer.Ordinal)];
                string[] elsewhere = [.. machines.Where(machine => machine.Region != region).Select(machine => machine.Name).Order(StringComparer.Ordinal)];
                await TerraBaseTestServer.EventuallyAsync(async token =>
                {
                    var view = await membership.RefreshAsync(token);
                    return view.Members.Select(member => member.Name).Order(StringComparer.Ordinal).SequenceEqual(own)
                        && view.Elsewhere.Select(member => member.Name).Order(StringComparer.Ordinal).SequenceEqual(elsewhere);
                });
                await Assert.That(membership.Self.Name).IsEqualTo(name);
            }
        }
        finally
        {
            foreach (var server in servers)
            {
                await server.DisposeAsync();
            }
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
