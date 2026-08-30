using System.Diagnostics;

namespace TerraScale.DotNet.Tests;

public sealed class TerraScaleApiE2EConfigurationTests
{
    [Test]
    public async Task LiveTestGate_OptionalMissingConfigurationRemainsAVisibleSkip()
    {
        var shouldSkip = TerraScaleLiveTestGate.ShouldSkip(
            configured: false,
            requireConfigured: false,
            "Configure the live test profile.");

        await Assert.That(shouldSkip).IsTrue();
    }

    [Test]
    public async Task LiveTestGate_StrictMissingConfigurationFailsClosed()
    {
        var failure = Assert.Throws<InvalidOperationException>(() => TerraScaleLiveTestGate.ShouldSkip(
            configured: false,
            requireConfigured: true,
            "Configure the live test profile."));

        await Assert.That(failure).IsNotNull();
        await Assert.That(failure!.Message).Contains(TerraScaleLiveTestGate.RequireConfiguredVariable);
        await Assert.That(failure.Message).Contains("Configure the live test profile.");
    }

    [Test]
    public async Task LiveTestGate_ConfiguredProfileNeverSkips()
    {
        await Assert.That(TerraScaleLiveTestGate.ShouldSkip(
            configured: true,
            requireConfigured: false,
            "Configure the live test profile.")).IsFalse();
        await Assert.That(TerraScaleLiveTestGate.ShouldSkip(
            configured: true,
            requireConfigured: true,
            "Configure the live test profile.")).IsFalse();
    }

    [Test]
    public async Task LiveTestGate_InvalidStrictSettingFailsClosed()
    {
        var failure = Assert.Throws<InvalidOperationException>(() =>
            TerraScaleLiveTestGate.ParseRequireConfigured("required"));

        await Assert.That(failure).IsNotNull();
        await Assert.That(failure!.Message)
            .Contains($"{TerraScaleLiveTestGate.RequireConfiguredVariable} must be true or false");
    }

    [Test]
    public async Task StrictMode_MakesARealUnconfiguredLiveTestFailInsteadOfSkip()
    {
        var assembly = typeof(TerraScaleApiE2EConfigurationTests).Assembly;
        var entityFrameworkAssembly = string.Equals(
            assembly.GetName().Name,
            "TerraScale.EntityFrameworkCore.Tests",
            StringComparison.Ordinal);
        var testName = entityFrameworkAssembly
            ? "EntitySet_UpsertFindQueryAndDelete_RoundTripsAgainstRealRepository"
            : "Regions_ListAsync_ReturnsCanonicalCatalogAgainstRealApi";
        var gateAttributeName = entityFrameworkAssembly
            ? nameof(RequiresTerraScaleApiAttribute)
            : nameof(RequiresTerraScaleBaseAddressAttribute);
        var executable = Path.ChangeExtension(assembly.Location, null);
        if (OperatingSystem.IsWindows())
        {
            executable += ".exe";
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = FindRepositoryRoot(),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("--no-ansi");
        startInfo.ArgumentList.Add("--treenode-filter");
        startInfo.ArgumentList.Add($"/*/*/*/*{testName}");
        foreach (var variable in startInfo.Environment.Keys
                     .Where(static key => key.StartsWith("TERRASCALE_E2E_", StringComparison.Ordinal))
                     .ToArray())
        {
            startInfo.Environment.Remove(variable);
        }

        startInfo.Environment[TerraScaleLiveTestGate.RequireConfiguredVariable] = "true";

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The strict live-test subprocess did not start.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        var output = $"{await standardOutput}\n{await standardError}";

        await Assert.That(process.ExitCode).IsNotEqualTo(0);
        await Assert.That(output).Contains(gateAttributeName);
        await Assert.That(output).Contains(TerraScaleLiveTestGate.RequireConfiguredVariable);
    }

    [Test]
    public async Task TerraScaleE2EConfiguration_IsCompleteWhenAnyVariableIsSet()
    {
        var configuredValues = TerraScaleApiE2E.ConfigurationVariables
            .Where(static variable => !string.IsNullOrWhiteSpace(variable.Value))
            .Select(static variable => variable.Name)
            .ToArray();
        var configuredCredentialValues = configuredValues
            .Where(static name => name != "TERRASCALE_E2E_BASE_URL")
            .ToArray();

        if (configuredValues.Length == 0)
        {
            await Assert.That(TerraScaleApiE2E.HasCompleteAuthenticationConfiguration).IsFalse();
            return;
        }

        if (configuredCredentialValues.Length == 0)
        {
            await Assert.That(TerraScaleApiE2E.IsBaseAddressConfigured).IsTrue();
            return;
        }

        if (!TerraScaleApiE2E.HasCompleteAuthenticationConfiguration)
        {
            throw new InvalidOperationException(
                $"Partially configured TerraScale E2E variables: {string.Join(", ", configuredValues)}.");
        }

        await Assert.That(TerraScaleApiE2E.HasCompleteAuthenticationConfiguration).IsTrue();
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "TerraScale.DotNet.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("The TerraScale.DotNet repository root was not found.");
    }
}
