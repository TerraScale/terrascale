// <copyright file="EntryPointTests.cs" company="TerraScale">
// TerraBaseV2 (SE-08). See LICENSE.md.
// </copyright>

namespace TerraBase.Api.Tests;

using System.Diagnostics;

/// <summary>
/// The entry-point rows of the AC: a failed startup owns everything it
/// started — the managed engine process it spawned is killed and
/// disposed (never leaked to outlive the host that failed on it), and
/// the host answers exit code 1.
/// </summary>
public sealed class EntryPointTests
{
    /// <summary>A startup that never reaches the engine kills the engine process it spawned.</summary>
    [Test]
    public async Task AFailedStartupDisposesTheSpawnedEngineProcess()
    {
        if (!OperatingSystem.IsLinux())
        {
            // The managed-engine supervision surface is a Unix fact (UDS
            // sockets, shebang executables); nothing to prove elsewhere.
            return;
        }

        var socket = Path.Combine(Path.GetTempPath(), $"se08-entry-{Guid.NewGuid():N}.sock");
        var home = Path.Combine(Path.GetTempPath(), $"se08-entry-{Guid.NewGuid():N}");
        Directory.CreateDirectory(home);
        var pidFile = Path.Combine(home, "engine.pid");
        var script = Path.Combine(home, "engine.sh");
        await File.WriteAllTextAsync(script, $"""
            #!/bin/sh
            echo $$ > {pidFile}
            sleep 600
            """);
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        try
        {
            // A managed engine that starts but never serves: the startup
            // fails after spawning it (the socket never comes up).
            var exit = await Program.RunAsync(socket, script, "http://127.0.0.1:0");
            await Assert.That(exit).IsEqualTo(1);

            // The spawned process wrote its pid during the startup window
            // and must be gone once the entry point answered: a failed
            // startup owns everything it started.
            var pid = await WaitForPidAsync(pidFile);
            var alive = false;
            try
            {
                using var process = Process.GetProcessById(pid);
                alive = !process.WaitForExit(3000);
                if (alive)
                {
                    // The red-run cleanup: never leak the sleeper on a
                    // failing assertion either.
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (ArgumentException)
            {
                // The process is already gone — the truthful answer.
            }

            await Assert.That(alive).IsFalse();
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    private static async Task<int> WaitForPidAsync(string pidFile)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(pidFile) && int.TryParse((await File.ReadAllTextAsync(pidFile)).Trim(), out var pid))
            {
                return pid;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        throw new TimeoutException($"the spawned engine never wrote {pidFile}");
    }
}
