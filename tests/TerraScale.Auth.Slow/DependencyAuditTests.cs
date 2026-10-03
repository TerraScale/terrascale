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
        var (exitCode, _) = await AuditAsync("""<PackageReference Include="TerraScale.Package.That.Does.Not.Exist" Version="1.0.0" />""");

        await Assert.That(exitCode).IsEqualTo(1);
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
        var start = new ProcessStartInfo(file, arguments)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        start.Environment.Remove("CI");
        using var process = Process.Start(start)!;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var stderr = process.StandardError.ReadToEndAsync(deadline.Token);
        try
        {
            // One deadline covers the exit and both pipes, which a descendant could hold open.
            await Task.WhenAll(process.WaitForExitAsync(deadline.Token), stdout, stderr);
        }
        finally
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }
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
