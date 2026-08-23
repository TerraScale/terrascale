using System.Diagnostics;

namespace TerraScale.DotNet.Tests;

public sealed class TerraScaleQuickstartProcessTests
{
    private const string MissingConfigurationMessage =
        "Set TERRASCALE_API_BASE_ADDRESS plus either TERRASCALE_BEARER_TOKEN or both TERRASCALE_API_KEY and TERRASCALE_PROJECT_ID.";

    private static readonly (string SampleName, string FixtureName)[] QuickstartEnvironmentNames =
    [
        ("TERRASCALE_API_BASE_ADDRESS", "TERRASCALE_E2E_QUICKSTART_BASE_URL"),
        ("TERRASCALE_API_KEY", "TERRASCALE_E2E_QUICKSTART_API_KEY"),
        ("TERRASCALE_BEARER_TOKEN", "TERRASCALE_E2E_QUICKSTART_BEARER_TOKEN"),
        ("TERRASCALE_PROJECT_ID", "TERRASCALE_E2E_QUICKSTART_PROJECT_ID"),
        ("TERRASCALE_DATABASE_ID", "TERRASCALE_E2E_QUICKSTART_DATABASE_ID"),
        ("TERRASCALE_ENVIRONMENT_ID", "TERRASCALE_E2E_QUICKSTART_ENVIRONMENT_ID")
    ];

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

    [Test]
    [RequiresTerraScaleQuickstartApi]
    [NotInParallel("quickstart-public-api")]
    public async Task ConfiguredQuickstart_CompletesPublicApiJourneyAndCleansUp()
    {
        var result = await RunQuickstartAsync(
            QuickstartEnvironment(),
            TimeSpan.FromMinutes(2));

        await Assert.That(result.TimedOut).IsFalse();
        await Assert.That(result.ExitCode).IsEqualTo(0);
    }

    private static async Task<QuickstartProcessResult> RunQuickstartAsync(
        IReadOnlyDictionary<string, string?> environment,
        TimeSpan? timeout = null)
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
        using var timeoutSource = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(5));
        var timedOut = false;

        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
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

    private static IReadOnlyDictionary<string, string?> QuickstartEnvironment() =>
        QuickstartEnvironmentNames.ToDictionary(
            static mapping => mapping.SampleName,
            static mapping => Environment.GetEnvironmentVariable(mapping.FixtureName));

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

internal sealed class RequiresTerraScaleQuickstartApiAttribute()
    : SkipAttribute(QuickstartEnvironmentConfiguration.SkipReason)
{
    public override Task<bool> ShouldSkip(TestRegisteredContext context) =>
        Task.FromResult(!QuickstartEnvironmentConfiguration.IsConfigured);
}

internal static class QuickstartEnvironmentConfiguration
{
    public const string SkipReason =
        "Set the dedicated TERRASCALE_E2E_QUICKSTART_BASE_URL, TERRASCALE_E2E_QUICKSTART_DATABASE_ID, TERRASCALE_E2E_QUICKSTART_ENVIRONMENT_ID, and either TERRASCALE_E2E_QUICKSTART_BEARER_TOKEN or TERRASCALE_E2E_QUICKSTART_API_KEY plus TERRASCALE_E2E_QUICKSTART_PROJECT_ID, then set TERRASCALE_E2E_ENABLE_QUICKSTART=true and TERRASCALE_E2E_QUICKSTART_CONFIRMED_DISPOSABLE=true to run the quickstart public-API process test.";

    public static bool IsConfigured =>
        IsAbsoluteBaseAddress() &&
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TERRASCALE_E2E_QUICKSTART_DATABASE_ID")) &&
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TERRASCALE_E2E_QUICKSTART_ENVIRONMENT_ID")) &&
        (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TERRASCALE_E2E_QUICKSTART_BEARER_TOKEN")) ||
            (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TERRASCALE_E2E_QUICKSTART_API_KEY")) &&
                !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TERRASCALE_E2E_QUICKSTART_PROJECT_ID")))) &&
        IsEnabled("TERRASCALE_E2E_ENABLE_QUICKSTART") &&
        IsEnabled("TERRASCALE_E2E_QUICKSTART_CONFIRMED_DISPOSABLE");

    private static bool IsAbsoluteBaseAddress() =>
        Uri.TryCreate(
            Environment.GetEnvironmentVariable("TERRASCALE_E2E_QUICKSTART_BASE_URL"),
            UriKind.Absolute,
            out _);

    private static bool IsEnabled(string name) =>
        string.Equals(Environment.GetEnvironmentVariable(name), "true", StringComparison.OrdinalIgnoreCase);
}
