using System.Text.Json;

namespace TerraScale.DotNet.Tests;

public sealed class PackageSetDeliveryContractTests
{
    [Test]
    public async Task DeployConfigurationBuildsOneAtomicPackageSetAndCandidateBoundLiveApphosts()
    {
        var repositoryRoot = FindRepositoryRoot();
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(repositoryRoot, ".terrascale", "deploy.json")));
        var root = document.RootElement;
        var buildTargets = root.GetProperty("buildTargets")
            .EnumerateArray()
            .ToDictionary(target => target.GetProperty("name").GetString()!, StringComparer.Ordinal);
        var releaseProfiles = root.GetProperty("releaseProfiles")
            .EnumerateArray()
            .ToDictionary(profile => profile.GetProperty("name").GetString()!, StringComparer.Ordinal);

        await Assert.That(buildTargets.ContainsKey("sdk-nuget")).IsFalse();
        await Assert.That(buildTargets.ContainsKey("ef-nuget")).IsFalse();

        var packageSet = buildTargets["dotnet-package-set-v2"];
        await Assert.That(packageSet.GetProperty("outputPath").GetString())
            .IsEqualTo("artifacts/candidate/dotnet-package-set-v2");
        var packageSetCommand = packageSet.GetProperty("commands").EnumerateArray().Single();
        await Assert.That(packageSetCommand.GetProperty("command").GetString()).IsEqualTo("python3");
        await Assert.That(packageSetCommand.GetProperty("arguments").EnumerateArray().Select(argument =>
                argument.GetString() ?? throw new InvalidDataException("Package-set command arguments must be strings.")))
            .IsEquivalentTo(["scripts/verify-package-set-candidate.py", "--emit-candidate"]);

        await AssertCandidateBoundProfileAsync(
            releaseProfiles["base-sdk-release-e2e"],
            "base-sdk-release-e2e-tests",
            "TerraScale.DotNet.Tests",
            225);
        await AssertCandidateBoundProfileAsync(
            releaseProfiles["ef-core-release-e2e"],
            "ef-core-release-e2e-tests",
            "TerraScale.EntityFrameworkCore.Tests",
            10);
    }

    private static async Task AssertCandidateBoundProfileAsync(
        JsonElement profile,
        string candidateTarget,
        string entrypoint,
        int minimumExpectedTests)
    {
        await Assert.That(profile.TryGetProperty("commands", out _)).IsFalse();
        await Assert.That(profile.GetProperty("required").GetBoolean()).IsTrue();
        await Assert.That(profile.GetProperty("candidateTarget").GetString()).IsEqualTo(candidateTarget);
        await Assert.That(profile.GetProperty("entrypoint").GetString()).IsEqualTo(entrypoint);
        await Assert.That(profile.GetProperty("minimumExpectedTests").GetInt32()).IsEqualTo(minimumExpectedTests);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, ".terrascale", "deploy.json")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("The TerraScale.DotNet repository root was not found.");
    }
}
