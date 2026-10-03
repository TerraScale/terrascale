using System.Diagnostics;

namespace TerraScale.Auth.Slow;

/// <summary>
/// Runs the real NuGet vulnerability gate (scripts/check-nuget-vulnerabilities.cs) against
/// throwaway projects, so its fail-closed behaviour is proven end to end, not only when the
/// current dependency graph happens to be clean.
/// </summary>
public sealed class DependencyAuditTests
{
    [Test]
    public async Task A_project_with_a_known_vulnerable_package_fails_the_gate()
    {
        // System.Text.Json 8.0.0 carries published advisories (for example GHSA-hh2w-p6rv-4g7w).
        var (exitCode, output) = await AuditAsync("""<PackageReference Include="System.Text.Json" Version="8.0.0" />""");

        await Assert.That(exitCode).IsEqualTo(1);
        await Assert.That(output).Contains("vulnerable NuGet package");
        await Assert.That(output).Contains("System.Text.Json");
    }

    [Test]
    public async Task A_project_without_vulnerable_packages_passes_the_gate()
    {
        var (exitCode, output) = await AuditAsync(string.Empty);

        await Assert.That(exitCode).IsEqualTo(0);
        await Assert.That(output).Contains("0 vulnerable packages");
    }

    [Test]
    public async Task A_project_that_cannot_be_audited_fails_the_gate()
    {
        var (exitCode, output) = await AuditAsync("""<PackageReference Include="TerraScale.Package.That.Does.Not.Exist" Version="1.0.0" />""");

        // The gate's own scanner-failure diagnostic, not merely some failing exit code.
        await Assert.That(exitCode).IsEqualTo(1);
        await Assert.That(output).Contains("NuGet audit failed for");
    }

    private static async Task<(int ExitCode, string Output)> AuditAsync(string references)
    {
        var repo = FindRepository();
        var directory = Directory.CreateTempSubdirectory("ts-auth-audit-");
        try
        {
            // Outside the repository, so ts-auth's build props and central versions do not apply.
            File.WriteAllText(Path.Combine(directory.FullName, "Probe.csproj"), $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework><NuGetAudit>false</NuGetAudit></PropertyGroup>
                  <ItemGroup>{references}</ItemGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(directory.FullName, "nuget.config"), """
                <configuration><packageSources><clear /><add key="nuget.org" value="https://api.nuget.org/v3/index.json" /></packageSources></configuration>
                """);
            // Restore may fail (the unrestorable case); the gate itself must then report it.
            await RunAsync("dotnet", ["restore", directory.FullName], directory.FullName);
            return await RunAsync(
                "dotnet",
                ["run", Path.Combine(repo, "scripts", "check-nuget-vulnerabilities.cs"), Path.Combine(directory.FullName, "Probe.csproj")],
                repo);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(string file, string[] arguments, string workingDirectory)
    {
        // setsid: the command leads its own process group, so cleanup reaches every
        // descendant even after the direct child has exited.
        var start = new ProcessStartInfo("setsid", [file, .. arguments])
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        start.Environment.Remove("CI");
        using var process = Process.Start(start)!;
        // Longer than the gate's own 10-minute scanner deadline, so the gate always gets to
        // clean up its separately grouped scanner before this harness gives up.
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(12));
        var stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var stderr = process.StandardError.ReadToEndAsync(deadline.Token);
        try
        {
            // One deadline covers the exit and both pipes, which a descendant could hold open.
            await Task.WhenAll(process.WaitForExitAsync(deadline.Token), stdout, stderr);
        }
        finally
        {
            using var kill = Process.Start(new ProcessStartInfo("kill", ["-KILL", "--", $"-{process.Id}"]) { RedirectStandardError = true });
            await kill!.WaitForExitAsync();
        }

        return (process.ExitCode, await stdout + await stderr);
    }

    private static string FindRepository()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "TerraScale.Auth.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Run the slow suite from inside the ts-auth checkout.");
    }
}
