using System.Diagnostics;

namespace TerraScale.DotNet.Tests;

public sealed class TerraScaleQuickstartProcessTests
{
    private const string MissingConfigurationMessage =
        "Set TERRASCALE_API_BASE_ADDRESS plus either TERRASCALE_BEARER_TOKEN or both TERRASCALE_API_KEY and TERRASCALE_PROJECT_ID.";

    [Test]
    public async Task MissingApiBaseAddress_WithCredentials_ExitsBeforeNetwork()
    {
        var result = await RunQuickstartAsync(new Dictionary<string, string?>
        {
            ["TERRASCALE_API_KEY"] = "test-api-key",
            ["TERRASCALE_PROJECT_ID"] = "test-project"
        });

        await Assert.That(result.TimedOut).IsFalse();
        await Assert.That(result.ExitCode).IsEqualTo(2);
        await Assert.That(result.StandardError).Contains(MissingConfigurationMessage);
        await Assert.That(result.StandardError.Contains("Unhandled exception", StringComparison.Ordinal)).IsFalse();
    }

    [Test]
    public async Task ConfiguredApiBaseAddress_ReachesAuthenticationGateWithoutNetworkCall()
    {
        var result = await RunQuickstartAsync(new Dictionary<string, string?>
        {
            ["TERRASCALE_API_BASE_ADDRESS"] = "https://api.terrascale.tech"
        });

        await Assert.That(result.TimedOut).IsFalse();
        await Assert.That(result.ExitCode).IsEqualTo(2);
        await Assert.That(result.StandardError).Contains(MissingConfigurationMessage);
        await Assert.That(result.StandardError.Contains("Unhandled exception", StringComparison.Ordinal)).IsFalse();
    }

    private static async Task<QuickstartProcessResult> RunQuickstartAsync(
        IReadOnlyDictionary<string, string?> environment)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = FindDotnetHost(),
            WorkingDirectory = FindRepositoryRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(FindQuickstartAssembly(startInfo.WorkingDirectory));

        foreach (var name in new[]
        {
            "TERRASCALE_API_BASE_ADDRESS",
            "TERRASCALE_API_KEY",
            "TERRASCALE_BEARER_TOKEN",
            "TERRASCALE_PROJECT_ID",
            "TERRASCALE_DATABASE_ID",
            "TERRASCALE_ENVIRONMENT_ID"
        })
        {
            startInfo.Environment.Remove(name);
        }

        foreach (var pair in environment)
        {
            if (pair.Value is not null)
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The quickstart process could not be started.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var timedOut = false;

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = true;
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await process.WaitForExitAsync();
        }

        await Task.WhenAll(standardOutput, standardError);
        return new QuickstartProcessResult(
            timedOut ? null : process.ExitCode,
            standardOutput.Result,
            standardError.Result,
            timedOut);
    }

    private static string FindDotnetHost()
    {
        var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrWhiteSpace(dotnetRoot))
        {
            var dotnetHost = Path.Combine(dotnetRoot, "dotnet");
            if (File.Exists(dotnetHost))
            {
                return dotnetHost;
            }
        }

        return "dotnet";
    }

    private static string FindQuickstartAssembly(string repositoryRoot)
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name
            ?? throw new InvalidOperationException("The test configuration directory is unavailable.");
        var path = Path.Combine(
            repositoryRoot,
            "samples",
            "TerraScale.Samples.Quickstart",
            "bin",
            configuration,
            "net10.0",
            "TerraScale.Samples.Quickstart.dll");
        return File.Exists(path)
            ? path
            : throw new FileNotFoundException("The built quickstart assembly was not found.", path);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "TerraScale.DotNet.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("The TerraScale.DotNet repository root was not found.");
    }

    private sealed record QuickstartProcessResult(
        int? ExitCode,
        string StandardOutput,
        string StandardError,
        bool TimedOut);
}
