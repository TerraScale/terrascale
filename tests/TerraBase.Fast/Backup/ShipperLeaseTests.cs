using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Engine.Cluster;
using TerraBase.Testing;

namespace TerraBase.Fast.Backup;

public sealed class ShipperLeaseTests
{
    [Test]
    public async Task An_observed_lease_loss_changes_epoch_without_a_membership_change()
    {
        var time = new FakeTimeProvider();
        var directory = new FakeMemberStore();
        await using var a = await StartAsync("member-a", directory, time);
        await using var b = await StartAsync("member-b", directory, time);
        var view = await a.Services.GetRequiredService<IMembership>().RefreshAsync(CancellationToken.None);
        time.Advance(DirectoryMembership.Heartbeat);
        view = await a.Services.GetRequiredService<IMembership>().RefreshAsync(CancellationToken.None);
        var name = new Ring(view).Primary("system")!.Name;
        var holder = name == "member-a" ? a : b;
        var leases = holder.Services.GetRequiredService<IShipperLeases>();
        _ = await leases.ShipperEpochAsync(null, CancellationToken.None);
        time.Advance(2 * DirectoryMembership.Heartbeat);
        var first = await leases.ShipperEpochAsync(null, CancellationToken.None);
        await Assert.That(first).IsNotNull();
        var membership = holder.Services.GetRequiredService<IMembership>();
        var before = await membership.RefreshAsync(CancellationToken.None);

        var failed = 1;
        directory.Map = (node, member) => node == name && Volatile.Read(ref failed) != 0
            ? throw new IOException("directory unavailable") : member;
        time.Advance(membership.Fence);
        await Assert.That(await leases.ShipperEpochAsync(null, CancellationToken.None)).IsNull();
        Volatile.Write(ref failed, 0);
        time.Advance(DirectoryMembership.Heartbeat);
        await Assert.That(await leases.ShipperEpochAsync(null, CancellationToken.None)).IsNull();
        time.Advance(membership.Fence);
        var next = await leases.ShipperEpochAsync(null, CancellationToken.None);
        await Assert.That(next).IsNotNull().And.IsNotEqualTo(first);
        await Assert.That((await membership.RefreshAsync(CancellationToken.None)).Revision).IsEqualTo(before.Revision);
        await Assert.That(await leases.ShipperEpochAsync(null, CancellationToken.None)).IsEqualTo(next);
    }

    [Test]
    public async Task A_disappearing_predecessor_does_not_bypass_the_fence()
    {
        var time = new FakeTimeProvider();
        var directory = new FakeMemberStore();
        await using var a = await StartAsync("member-a", directory, time);
        await using var b = await StartAsync("member-b", directory, time);
        time.Advance(DirectoryMembership.Heartbeat);
        var view = await a.Services.GetRequiredService<IMembership>().RefreshAsync(CancellationToken.None);
        var name = new Ring(view).Primary("system")!.Name;
        var successor = name == "member-a" ? b : a;
        var leases = successor.Services.GetRequiredService<IShipperLeases>();
        await Assert.That(await leases.ShipperEpochAsync(null, CancellationToken.None)).IsNull();

        directory.Leave(name);
        time.Advance(DirectoryMembership.Heartbeat);
        await Assert.That(await leases.ShipperEpochAsync(null, CancellationToken.None)).IsNull();
        var fence = successor.Services.GetRequiredService<IMembership>().Fence;
        time.Advance(fence - TimeSpan.FromTicks(1));
        await Assert.That(await leases.ShipperEpochAsync(null, CancellationToken.None)).IsNull();
        time.Advance(TimeSpan.FromTicks(1));
        var epoch = await leases.ShipperEpochAsync(null, CancellationToken.None);
        await Assert.That(epoch).IsNotNull();
        await Assert.That(await leases.ShipperEpochAsync(null, CancellationToken.None)).IsEqualTo(epoch);
    }

    private static async Task<TerraBaseTestServer> StartAsync(string name, FakeMemberStore directory, TimeProvider time)
    {
        var server = await TerraBaseTestServer.StartAsync(settings => settings["node-name"] = name, services =>
        {
            services.AddSingleton(time);
            services.AddSingleton<IMemberDirectory>(directory);
        });
        using var response = await server.Client.GetAsync(new Uri("/healthz", UriKind.Relative));
        response.EnsureSuccessStatusCode();
        return server;
    }
}
