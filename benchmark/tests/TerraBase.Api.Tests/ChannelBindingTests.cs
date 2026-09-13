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

    /// <summary>Two principals in one tenant receive distinct channels whose immutable identities stay isolated.</summary>
    [Test]
    public async Task SameTenantDifferentPrincipalsDoNotShareAChannel()
    {
        var path = SocketPath();
        await using var engine = new ScriptedTbepEngine(path);
        await using var host = await HostHarness.StartAsync(new EngineOptions { SocketPath = path });

        var first = await host.Supervisor.ChannelForAsync("tenant-a", "principal-a");
        var second = await host.Supervisor.ChannelForAsync("tenant-a", "principal-b");

        await Assert.That(second).IsNotEqualTo(first);
        await Assert.That(first.Tenant).IsEqualTo("tenant-a");
        await Assert.That(first.Principal).IsEqualTo("principal-a");
        await Assert.That(second.Tenant).IsEqualTo("tenant-a");
        await Assert.That(second.Principal).IsEqualTo("principal-b");

        var selected = await host.Supervisor.CallAsync(
            "tenant-a",
            "principal-b",
            "identity.probe",
            channel => Task.FromResult(channel.Principal));
        await Assert.That(selected).IsEqualTo("principal-b");
    }

    /// <summary>A bind response for another tenant is rejected before the channel records any identity.</summary>
    [Test]
    public async Task BindResponseForAnotherTenantIsRejected()
    {
        var path = SocketPath();
        await using var engine = new ScriptedTbepEngine(path);
        await using var host = await HostHarness.StartAsync(new EngineOptions { SocketPath = path });
        engine.BindResponseTenant = "tenant-b";

        var failure = await Assert.ThrowsAsync<TbepException>(
            () => host.Supervisor.ChannelForAsync("tenant-a", "principal-a"));

        await Assert.That(failure).IsNotNull();
        await Assert.That(failure!.Code).IsNotNull();
        await Assert.That(failure.Code!.Variant).IsEqualTo("OperationRefused");
        await Assert.That(host.Supervisor.Status).IsEqualTo(EngineStatus.Serving);
    }
}
