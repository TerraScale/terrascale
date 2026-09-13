// <copyright file="EngineHarness.cs" company="TerraScale">
// TerraBase (SE-08). See LICENSE.md.
// </copyright>

namespace TerraBase.Api.Tests;

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

/// <summary>
/// The real-engine harness of the SE-08 e2e rows: it builds the
/// repository's actual Rust engine server — <c>TbepServer</c> over
/// <c>SyncDeliveryHost</c> from <c>crates/terrabase-tbep</c> — into a
/// throwaway cargo project outside the repository (the packet owns no
/// Rust diff; the crate is consumed exactly as the crate's own UDS
/// harness consumes it: <c>TbepServer::bind(path, Arc::new(
/// SyncDeliveryHost::new(instance)))</c>) and hands the binary to the
/// host's supervisor, which spawns, watches, and restarts the engine
/// process. The harness home is stable per worktree so the cargo target
/// directory is reused across runs.
/// </summary>
public static class EngineHarness
{
    private static readonly Lazy<Task<string>> Binary = new(() => BuildAsync());

    /// <summary>The engine binary path, built once per test run.</summary>
    /// <returns>The path of the real engine server binary.</returns>
    public static Task<string> BinaryPathAsync() => Binary.Value;

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "crates", "terrabase-tbep")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new TUnit.Core.Exceptions.SkipTestException(
            "the external Rust engine is unavailable: crates/terrabase-tbep is not present in this worktree");
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
            edition = "2024"
            publish = false

            [dependencies]
            terrabase-tbep = { path = "{{root}}/crates/terrabase-tbep" }

            [workspace]

            [profile.release]
            debug = false
            """).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(source, "main.rs"), """
            use std::io::BufRead as _;
    use std::path::Path;
    use std::sync::Arc;
    use terrabase_tbep::engine_host::EngineHost;
    use terrabase_tbep::quic::TbepQuicServer;
    use terrabase_tbep::{SyncDeliveryHost, TbepHost, TbepServer};

    fn main() {
        let mut args = std::env::args().skip(1);
        let socket = args.next().expect("usage: tbep-engine-harness <socket> <instance> [engine:<location>] [quic:<port>:<cert-pem>:<key-pem>:<client-root-pem>]");
        let instance: u64 = args.next().and_then(|s| s.parse().ok()).unwrap_or(1);
        // The optional third argument selects the host: `engine:<dir>`
        // binds the real durable Engine at that location (the P4
        // document-spine rows); anything else binds the sync-delivery
        // specialist.
        let engine_location = args
            .next()
            .and_then(|argument| argument.strip_prefix("engine:").map(str::to_owned));
        // The optional fourth argument binds the QUIC+TLS listener beside
        // the UDS one: `quic:<port>:<cert-pem>:<key-pem>:<client-root-pem>`
        // serves the same host over mutual TLS 1.3 at 127.0.0.1:<port>.
        let quic = args.next().and_then(|argument| argument.strip_prefix("quic:").map(str::to_owned));
        let host: Arc<dyn TbepHost> = match engine_location {
            Some(location) => Arc::new(
                EngineHost::open(Path::new(&location), instance)
                    .expect("the engine host opens its durable location"),
            ),
            None => Arc::new(SyncDeliveryHost::new(instance)),
        };
        let mut server = TbepServer::bind(&socket, Arc::clone(&host))
            .expect("the engine harness binds its UDS listener");
        let mut quic_server = quic.map(|spec| {
            let mut parts = spec.splitn(4, ':');
            let port: u16 = parts.next().and_then(|p| p.parse().ok()).expect("quic port");
            let cert_path = parts.next().expect("quic cert path");
            let key_path = parts.next().expect("quic key path");
            let client_root_path = parts.next().expect("quic client root path");
            let certificate = std::fs::read(cert_path).expect("the certificate reads");
            let key = std::fs::read(key_path).expect("the key reads");
            let client_root = std::fs::read(client_root_path).expect("the client root reads");
            // PEM-decode the pair the operator (or the row) provisioned.
            let certificate_chain = terrabase_tbep::quic::decode_pem_certificates(&certificate)
                .expect("the certificate is a PEM chain");
            let key = terrabase_tbep::quic::decode_pem_key(&key).expect("the key is PEM PKCS#8");
            let client_roots = terrabase_tbep::quic::decode_pem_certificates(&client_root)
                .expect("the client roots are PEM");
            let mut listener = TbepQuicServer::bind(
                std::net::SocketAddr::from(([127, 0, 0, 1], port)),
                certificate_chain,
                key,
                &client_roots,
                host,
            )
            .expect("the engine harness binds its QUIC listener");
            println!("quic 127.0.0.1:{port}");
            listener
        });
        println!("serving {socket} instance {instance}");
        // The supervisor owns the process: it ends on stdin close, on
        // SIGTERM, or by process loss — exactly the supervision surface.
        let mut line = String::new();
        let _ = std::io::stdin().lock().read_line(&mut line);
        server.shutdown();
        if let Some(listener) = quic_server.as_mut() {
            listener.shutdown();
        }
    }
    """).ConfigureAwait(false);

        var cargo = ResolveCargo() ?? throw new TUnit.Core.Exceptions.SkipTestException(
            "the external Rust engine is unavailable: cargo is not on PATH");
        var arguments = new List<string> { "+1.97.1", "build", "--release" };
        if (Environment.GetEnvironmentVariable("TERRABASE_TEST_CARGO_ONLINE") != "1")
        {
            arguments.Add("--offline");
        }

        var build = await RunAsync(cargo, [.. arguments], home).ConfigureAwait(false);
        if (!build)
        {
            // A stale lockless cache is the one offline failure mode that a
            // networked retry fixes; retry once without the offline pin.
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
}
