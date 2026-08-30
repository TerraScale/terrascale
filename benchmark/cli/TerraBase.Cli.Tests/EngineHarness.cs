// <copyright file="EngineHarness.cs" company="TerraScale">
// TerraBase (SE-13). See LICENSE.md.
// </copyright>

namespace TerraBase.Cli.Tests;

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

/// <summary>
/// The real-engine harness of the CLI test family, following the SE-08
/// <c>Api.Tests</c> pattern exactly: it builds the repository's actual
/// Rust engine server — <c>TbepServer</c> over <c>SyncDeliveryHost</c>
/// from <c>crates/terrabase-tbep</c> — into a throwaway cargo project
/// outside the repository (the packet owns no Rust diff) and hands the
/// binary to the host fixture (or spawns it directly for the
/// direct-TBEP rows). The harness home is byte-identical to the SE-08
/// harness's (same digest of the same worktree root), so the cargo
/// target directory — and the built engine — is shared and built once
/// per worktree, not once per test family.
/// </summary>
public static class EngineHarness
{
    private static readonly Lazy<Task<string>> Binary = new(() => BuildAsync());

    /// <summary>The engine binary path, built once per test run.</summary>
    /// <returns>The path of the real engine server binary.</returns>
    public static Task<string> BinaryPathAsync() => Binary.Value;

    /// <summary>A fresh UDS path for one engine process to bind.</summary>
    /// <returns>A unique socket path in the system temporary directory.</returns>
    public static string SocketPath() => Path.Combine(Path.GetTempPath(), $"se13-engine-{Guid.NewGuid():N}.sock");

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "crates", "terrabase-tbep")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("the worktree root (crates/terrabase-tbep) is not reachable from the test output");
    }

    private static string Home()
    {
        var root = RepoRoot();
        var digest = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(root)))[..8].ToLowerInvariant();
        return Path.Combine(Path.GetTempPath(), $"terrabase-engine-harness-{digest}");
    }

    private static async Task<string> BuildAsync()
    {
        var home = Home();
        var source = Path.Combine(home, "src");
        Directory.CreateDirectory(source);
        var root = RepoRoot();
        await File.WriteAllTextAsync(Path.Combine(home, "Cargo.toml"), $$"""
            # Generated at test time by TerraBase.Api.Tests (SE-08): a
            # throwaway cargo project that binds the repository's real
            # TBEP engine server (crates/terrabase-tbep) for the C# host's
            # real-engine end-to-end rows. Nothing here is part of the
            # repository's Rust scope.
            [package]
            name = "tbep-engine-harness"
            version = "0.1.0"
            edition = "2021"
            publish = false

            [dependencies]
            terrabase-tbep = { path = "{{root}}/crates/terrabase-tbep" }

            [workspace]

            [profile.release]
            debug = false
            """).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(source, "main.rs"), """
            use std::io::BufRead as _;
            use std::sync::Arc;
            use terrabase_tbep::{SyncDeliveryHost, TbepServer};

            fn main() {
                let mut args = std::env::args().skip(1);
                let socket = args.next().expect("usage: tbep-engine-harness <socket> <instance>");
                let instance: u64 = args.next().and_then(|s| s.parse().ok()).unwrap_or(1);
                let mut server = TbepServer::bind(&socket, Arc::new(SyncDeliveryHost::new(instance)))
                    .expect("the engine harness binds its UDS listener");
                println!("serving {socket} instance {instance}");
                // The supervisor owns the process: it ends on stdin close, on
                // SIGTERM, or by process loss — exactly the supervision surface.
                let mut line = String::new();
                let _ = std::io::stdin().lock().read_line(&mut line);
                server.shutdown();
            }
            """).ConfigureAwait(false);

        var cargo = ResolveCargo() ?? throw new InvalidOperationException("cargo is not on PATH; the real-engine rows cannot build the engine server");
        var arguments = new List<string> { "+1.97.1", "build", "--release" };
        if (Environment.GetEnvironmentVariable("TERRABASE_TEST_CARGO_ONLINE") != "1")
        {
            arguments.Add("--offline");
        }

        var build = await RunAsync(cargo, [.. arguments], home).ConfigureAwait(false);
        if (!build)
        {
            // A networked retry is opt-in (TERRABASE_TEST_CARGO_ONLINE=1):
            // the documented contract never reaches for the network on a
            // build the operator pinned to offline.
            if (Environment.GetEnvironmentVariable("TERRABASE_TEST_CARGO_ONLINE") != "1")
            {
                throw new InvalidOperationException(
                    "the real-engine harness failed to build offline; set TERRABASE_TEST_CARGO_ONLINE=1 to allow a networked retry (see the cargo diagnostics above)");
            }

            var online = await RunAsync(cargo, ["+1.97.1", "build", "--release"], home).ConfigureAwait(false);
            if (!online)
            {
                throw new InvalidOperationException("the real-engine harness failed to build; see the cargo diagnostics above");
            }
        }

        return Path.Combine(home, "target", "release", "tbep-engine-harness");
    }

    private static string? ResolveCargo()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(directory, "cargo");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        var home = Environment.GetEnvironmentVariable("HOME");
        return home is null ? null : Path.Combine(home, ".cargo", "bin", "cargo");
    }

    private static async Task<bool> RunAsync(string fileName, string[] arguments, string workingDirectory)
    {
        var info = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info) ?? throw new InvalidOperationException($"{fileName} did not start");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException("the engine harness build exceeded its bound");
        }

        if (process.ExitCode != 0)
        {
            Console.WriteLine($"engine harness build failed:\n{await stdout.ConfigureAwait(false)}\n{await stderr.ConfigureAwait(false)}");
            return false;
        }

        return true;
    }

    /// <summary>Spawns one local engine process bound to a fresh UDS path (the direct-TBEP fixture).</summary>
    /// <returns>The running engine process and its socket path.</returns>
    public static Task<(Process Process, string Socket)> SpawnAsync() => SpawnAsync(null);

    /// <summary>
    /// Spawns one local engine process, either on a fresh UDS path or on a
    /// caller-named path — the engine-loss row restarts the engine on the
    /// same socket a following tail still points at (the server replaces a
    /// stale socket file on bind), so the tail's reconnection is real.
    /// </summary>
    /// <param name="socket">The UDS path to bind, or <c>null</c> for a fresh path.</param>
    /// <returns>The running engine process and its socket path.</returns>
    public static async Task<(Process Process, string Socket)> SpawnAsync(string? socket)
    {
        var binary = await BinaryPathAsync().ConfigureAwait(false);
        if (socket is null)
        {
            socket = SocketPath();
        }
        else
        {
            // A named path is a restart: the killed engine's socket file
            // still exists, so its presence would not mean the new engine
            // bound. Removing it first makes the file's return the signal
            // the wait below polls for.
            try
            {
                File.Delete(socket);
            }
            catch (IOException)
            {
                // The engine removes a stale path on bind regardless.
            }
        }
        var info = new ProcessStartInfo
        {
            FileName = binary,
            Arguments = $"\"{socket}\" 1",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        var process = Process.Start(info) ?? throw new InvalidOperationException("the engine process did not start");
        _ = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline && !File.Exists(socket))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(25)).ConfigureAwait(false);
        }

        return (process, socket);
    }
}
