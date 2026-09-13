// <copyright file="EngineSupervisorStartupTests.cs" company="TerraScale">
// TerraBase (SE-08). See LICENSE.md.
// </copyright>

namespace TerraBase.Api.Tests;

/// <summary>The supervisor's startup publication and loss boundaries.</summary>
public sealed class EngineSupervisorStartupTests
{
    /// <summary>An unbound admin channel cannot publish a serving generation.</summary>
    [Test]
    public async Task AdminBindingFailureDoesNotPublishServing()
    {
        var socket = Path.Combine(Path.GetTempPath(), $"tbep-admin-bind-{Guid.NewGuid():N}.sock");
        await using var engine = new ScriptedTbepEngine(socket) { Draining = true };
        await using var supervisor = new EngineSupervisor(
            new EngineOptions { SocketPath = socket },
            new AuditLog());

        await Assert.ThrowsAsync<EngineUnavailableException>(() => supervisor.StartAsync());
        await Assert.That(supervisor.Status).IsEqualTo(EngineStatus.Unavailable);
        await Assert.That(supervisor.Generation).IsEqualTo(0);
    }

    /// <summary>
    /// A managed endpoint that exits after accepting the readiness probe
    /// never gets published as a serving generation while startup is still
    /// trying to bind the supervisor's admin channel.
    /// </summary>
    [Test]
    public async Task EngineExitDuringStartupNeverPublishesServing()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var home = Path.Combine(Path.GetTempPath(), $"tbep-startup-loss-{Guid.NewGuid():N}");
        Directory.CreateDirectory(home);
        var socket = Path.Combine(home, "engine.sock");
        var script = Path.Combine(home, "engine.py");
        await File.WriteAllTextAsync(script, """
            #!/usr/bin/env python3
            import os
            import socket
            import sys
            import time

            path = sys.argv[1]
            try:
                os.unlink(path)
            except FileNotFoundError:
                pass

            listener = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
            listener.bind(path)
            listener.listen(8)
            listener.settimeout(0.02)
            connections = []
            deadline = time.monotonic() + 0.25
            while time.monotonic() < deadline:
                try:
                    connection, _ = listener.accept()
                    connections.append(connection)
                except socket.timeout:
                    pass

            for connection in connections:
                connection.close()
            listener.close()
            """);
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        try
        {
            await using var supervisor = new EngineSupervisor(
                new EngineOptions
                {
                    SocketPath = socket,
                    ManagedBinary = script,
                    MaxRestarts = 0,
                    StartTimeout = TimeSpan.FromSeconds(2),
                },
                new AuditLog());

            await Assert.ThrowsAsync<EngineUnavailableException>(() => supervisor.StartAsync());
            await Assert.That(supervisor.Status).IsEqualTo(EngineStatus.Unavailable);
            await Assert.That(supervisor.Generation).IsEqualTo(0);
            await Assert.That(supervisor.Restarts).IsEqualTo(0);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    /// <summary>A launch failure is surfaced as the supervisor's typed unavailability.</summary>
    [Test]
    public async Task MissingManagedEngineIsTypedUnavailable()
    {
        var socket = Path.Combine(Path.GetTempPath(), $"tbep-missing-engine-{Guid.NewGuid():N}.sock");
        await using var supervisor = new EngineSupervisor(
            new EngineOptions
            {
                SocketPath = socket,
                ManagedBinary = Path.Combine(Path.GetTempPath(), $"missing-engine-{Guid.NewGuid():N}"),
            },
            new AuditLog());

        await Assert.ThrowsAsync<EngineUnavailableException>(() => supervisor.StartAsync());
        await Assert.That(supervisor.Status).IsEqualTo(EngineStatus.Unavailable);
    }

    /// <summary>A disposed supervisor cannot reuse its former admin channel.</summary>
    [Test]
    public async Task DisposedSupervisorRefusesShutdown()
    {
        var socket = Path.Combine(Path.GetTempPath(), $"tbep-disposed-{Guid.NewGuid():N}.sock");
        await using var engine = new ScriptedTbepEngine(socket);
        var supervisor = new EngineSupervisor(
            new EngineOptions { SocketPath = socket },
            new AuditLog());
        await supervisor.StartAsync();
        await supervisor.DisposeAsync();

        await Assert.ThrowsAsync<EngineUnavailableException>(() => supervisor.ShutdownAsync(ShutdownReason.Operator()));
    }

    /// <summary>A disposed supervisor cannot be started again.</summary>
    [Test]
    public async Task DisposedSupervisorRefusesStart()
    {
        var supervisor = new EngineSupervisor(
            new EngineOptions { SocketPath = Path.Combine(Path.GetTempPath(), $"tbep-disposed-start-{Guid.NewGuid():N}.sock") },
            new AuditLog());
        await supervisor.DisposeAsync();

        await Assert.ThrowsAsync<EngineUnavailableException>(() => supervisor.StartAsync());
    }
}
