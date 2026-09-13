// <copyright file="ChannelBindingTests.cs" company="TerraScale">
// TerraBase (SE-08). See LICENSE.md.
// </copyright>

namespace TerraBase.Api.Tests;

/// <summary>
/// The first-bind rows of the AC (against the scripted engine): the
/// first use of a tenant channel binds it exactly once — concurrent
/// first callers never race a second bind onto the same channel (the
/// engine answers <c>AlreadyBound</c>) and never dispose the live
/// binding another caller is already serving on
/// (PD-2026-08-26-11 D1: the channel, not the caller, owns the binding).
/// </summary>
public sealed class ChannelBindingTests
{
    private static string SocketPath() => Path.Combine(Path.GetTempPath(), $"se08-binding-{Guid.NewGuid():N}.sock");

    /// <summary>Concurrent first binds of one tenant share one live channel: the late arrival adopts the first bind's outcome.</summary>
    [Test]
    public async Task ConcurrentFirstBindsShareOneLiveChannel()
    {
        var path = SocketPath();
        await using var engine = new ScriptedTbepEngine(path) { BindDelay = TimeSpan.FromMilliseconds(400) };
        await using var host = await HostHarness.StartAsync(new EngineOptions { SocketPath = path });

        // Two callers need tenant-a's channel at the same time, while the
        // engine is still answering the first binding: the deterministic
        // first-bind race window.
        var first = Task.Run(() => host.Supervisor.ChannelForAsync("tenant-a", "backend-operator"));
        var second = Task.Run(() => host.Supervisor.ChannelForAsync("tenant-a", "backend-operator"));
        var channels = await Task.WhenAll(first, second);

        // One live bound channel serves both callers — the late arrival
        // adopted the first bind's outcome instead of disposing it.
        await Assert.That(channels[1]).IsEqualTo(channels[0]);
        await Assert.That(channels[0].IsClosed).IsFalse();
        await Assert.That(channels[0].Tenant).IsEqualTo("tenant-a");
        await Assert.That(host.Audit.Recent(AuditLog.Capacity).Count(record => record.Kind == AuditKind.ChannelBound && record.Tenant == "tenant-a")).IsEqualTo(1);

        // The shared channel keeps serving typed calls.
        var answer = await host.Supervisor
            .CallAsync("tenant-a", "backend-operator", "document.read", channel => channel.DocumentReadAsync("orders", "d-01"))
            .ConfigureAwait(false);
        await Assert.That(answer.Found).IsFalse();
    }
}
