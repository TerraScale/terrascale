using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using TUnit.Core;
using TUnit.Core.Enums;

namespace BenchClient.Tests;

public sealed class FailureTests
{
    [Test]
    public async Task UnexpectedFailureDoesNotExposeExceptionDetails()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = await Cli.RunAsync(
            ["run", "--workload", "unused", "--region", "gru", "--out", "unused.jsonl"],
            output, error, () => throw new TypeInitializationException("do-not-print-me", new Exception("secret")));
        await Assert.That(code).IsEqualTo(2);
        await Assert.That(error.ToString()).IsEqualTo("PREFLIGHT_FAILED: Unexpected internal failure; refusing to run." + Environment.NewLine);
        await Assert.That(output.ToString()).IsEqualTo("");
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task LauncherDistinguishesKilledProcessFromDeadline()
    {
        if (!OperatingSystem.IsLinux()) return;
        var launcher = ScriptPath("bench-client");
        RequireTools("bash", "timeout");
        var directory = Directory.CreateTempSubdirectory("dotnet-killed-");
        Process? process = null;
        try
        {
            var fakeDotnet = Path.Combine(directory.FullName, "dotnet");
            await File.WriteAllTextAsync(fakeDotnet, "#!/usr/bin/env bash\nexit 137\n");
            File.SetUnixFileMode(fakeDotnet, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var start = new ProcessStartInfo("bash")
            {
                ArgumentList = { launcher },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            start.Environment["PATH"] = directory.FullName + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
            process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.That(process.ExitCode).IsEqualTo(137);
            await Assert.That(await error.WaitAsync(TimeSpan.FromSeconds(2))).IsEqualTo("PREFLIGHT_FAILED: Preflight process was killed; resource exhaustion or forced termination may have occurred.\n");
            await Assert.That(await output.WaitAsync(TimeSpan.FromSeconds(2))).IsEqualTo("");
        }
        finally
        {
            if (process is not null)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                process.Dispose();
            }
            directory.Delete(recursive: true);
        }
    }

    [Test]
    public async Task HostnameSocketFailureReturnsSafeDiagnosticBeforeInputIsRead()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = await Cli.RunAsync(
            ["run", "--workload", "do-not-print-me", "--region", "gru", "--out", "unused.jsonl"],
            output, error, () => throw new SocketException((int)SocketError.HostNotFound));
        await Assert.That(code).IsEqualTo(2);
        await Assert.That(error.ToString().StartsWith("HOST_UNAVAILABLE:", StringComparison.Ordinal)).IsTrue();
        await Assert.That(error.ToString().Contains("do-not-print-me", StringComparison.Ordinal)).IsFalse();
        await Assert.That(output.ToString()).IsEqualTo("");
    }

    [Test]
    [Arguments("mariogk-desktop")]
    [Arguments("MARIOGK-DESKTOP.example.test")]
    public async Task ExcludedHostIsRejectedBeforeInputIsRead(string hostName)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = await Cli.RunAsync(
            ["run", "--workload", "do-not-print-me", "--region", "gru", "--out", "unused.jsonl"],
            output, error, () => hostName);
        await Assert.That(code).IsEqualTo(2);
        await Assert.That(error.ToString().StartsWith("HOST_EXCLUDED:", StringComparison.Ordinal)).IsTrue();
        await Assert.That(output.ToString()).IsEqualTo("");
    }

    [Test]
    [Arguments("")]
    [Arguments(" ")]
    public async Task EmptyHostnameFailsClosed(string hostName)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = await Cli.RunAsync(
            ["run", "--workload", "do-not-print-me", "--region", "gru", "--out", "unused.jsonl"],
            output, error, () => hostName);
        await Assert.That(code).IsEqualTo(2);
        await Assert.That(error.ToString().StartsWith("HOST_UNAVAILABLE:", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    [RunOn(OS.Linux)]
    [Arguments(false)]
    [Arguments(true)]
    public async Task LauncherBoundsFifoOpenAndStalledRead(bool stalledWriter)
    {
        string hostName;
        try { hostName = Dns.GetHostName(); }
        catch (SocketException)
        {
            Skip.Test("FIFO integration test requires a verifiable hostname.");
            return;
        }
        Skip.Unless(!string.IsNullOrWhiteSpace(hostName) &&
            !hostName.Split('.')[0].Equals("mariogk-desktop", StringComparison.OrdinalIgnoreCase),
            "FIFO integration test requires an eligible host.");
        var launcher = ScriptPath("bench-client");
        RequireTools("bash", "mkfifo", "timeout", "dotnet", "sleep");
        Skip.Unless(File.Exists(Path.Combine(Path.GetDirectoryName(launcher)!, "src/bin/Release/net11.0/bench-client.dll")),
            "FIFO integration test requires the Release client build.");
        var directory = Directory.CreateTempSubdirectory("dotnet-fifo-");
        Process? writer = null;
        Process? client = null;
        try
        {
            var path = Path.Combine(directory.FullName, "do-not-print-me");
            var results = Path.Combine(directory.FullName, "results.jsonl");
            await File.WriteAllTextAsync(results, "existing-evidence");
            using var fifo = Process.Start(new ProcessStartInfo("mkfifo") { ArgumentList = { path } })!;
            await fifo.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(fifo.ExitCode).IsEqualTo(0);
            if (stalledWriter)
                writer = Process.Start(new ProcessStartInfo("bash")
                {
                    ArgumentList = { "-c", "exec 3>\"$1\"; printf '{' >&3; sleep 30", "fifo-writer", path },
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                });

            client = Process.Start(new ProcessStartInfo(launcher)
            {
                ArgumentList = { "run", "--workload", path, "--region", "gru", "--out", results },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            var output = client.StandardOutput.ReadToEndAsync();
            var error = client.StandardError.ReadToEndAsync();
            // Test deadline is longer than the launcher's 5s + 1s termination bound.
            await client.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(12));
            await Assert.That(client.ExitCode).IsEqualTo(2);
            var diagnostic = await error.WaitAsync(TimeSpan.FromSeconds(2));
            await Assert.That(diagnostic.Contains("preflight exceeded its 5-second deadline", StringComparison.Ordinal)).IsTrue();
            await Assert.That(diagnostic.Contains("do-not-print-me", StringComparison.Ordinal)).IsFalse();
            await Assert.That(await output.WaitAsync(TimeSpan.FromSeconds(2))).IsEqualTo("");
            await Assert.That(await File.ReadAllTextAsync(results)).IsEqualTo("existing-evidence");
        }
        finally
        {
            foreach (var process in new[] { client, writer })
            {
                if (process is null) continue;
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                process.Dispose();
            }
            directory.Delete(recursive: true);
        }
    }

    private static string ScriptPath(string name, [CallerFilePath] string source = "")
    {
        var path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", name));
        Skip.Unless(File.Exists(path), "Shell integration test requires the source checkout.");
        return path;
    }

    private static void RequireTools(params string[] names)
    {
        var directories = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
        foreach (var name in names)
            Skip.Unless(directories.Any(directory => File.Exists(Path.Combine(directory, name))), $"Shell integration test requires {name}.");
    }
}
