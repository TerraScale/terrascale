using System.Diagnostics;
using System.Net.Sockets;
using TUnit.Core;

namespace BenchClient.Tests;

public sealed class FailureTests
{
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
    [Arguments(false)]
    [Arguments(true)]
    public async Task LauncherBoundsFifoOpenAndStalledRead(bool stalledWriter)
    {
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

            var launcher = Path.GetFullPath("../../../../bench-client", AppContext.BaseDirectory);
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
}
