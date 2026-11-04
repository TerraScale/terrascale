using System.Diagnostics;
using TUnit.Core.Interfaces;

namespace TerraScale.Authentication.Slow;

/// <summary>
/// Runs the real NuGet vulnerability gate (scripts/auth/check-nuget-vulnerabilities.cs) against
/// throwaway projects, so its fail-closed behaviour is proven end to end, not only when the
/// current dependency graph happens to be clean.
/// </summary>
// `dotnet run` on a file-based script shares one runfile cache per script across processes, so
// the gate is built once for the class and every case executes that build concurrently.
[ClassDataSource<DependencyAuditTests.AuditGate>(Shared = SharedType.PerClass)]
public sealed class DependencyAuditTests(DependencyAuditTests.AuditGate gate)
{
    /// <summary>The vulnerability gate script, built once into a directory this fixture owns.</summary>
    public sealed class AuditGate : IAsyncInitializer, IAsyncDisposable
    {
        private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("ts-auth-audit-gate-");

        /// <summary>The built gate assembly.</summary>
        public string Assembly { get; private set; } = string.Empty;

        public async Task InitializeAsync()
        {
            var repo = FindRepository();
            var (exitCode, output) = await RunAsync(
                "dotnet",
                ["build", Path.Combine(repo, "scripts", "auth", "check-nuget-vulnerabilities.cs"), "--artifacts-path", this.directory.FullName],
                repo);
            this.Assembly = exitCode == 0
                ? Directory.GetFiles(Path.Combine(this.directory.FullName, "bin"), "check-nuget-vulnerabilities.dll", SearchOption.AllDirectories).Single()
                : throw new InvalidOperationException($"The gate script did not build: {output}");
        }

        public ValueTask DisposeAsync()
        {
            this.directory.Delete(recursive: true);
            return ValueTask.CompletedTask;
        }
    }

    [Test]
    public async Task A_project_with_a_known_vulnerable_package_fails_the_gate()
    {
        // System.Text.Json 8.0.0 carries published advisories (for example GHSA-hh2w-p6rv-4g7w).
        var (exitCode, output) = await AuditAsync("""<PackageReference Include="System.Text.Json" Version="8.0.0" />""");

        await Assert.That(exitCode).IsEqualTo(1).Because(output);
        await Assert.That(output).Contains("vulnerable NuGet package");
        await Assert.That(output).Contains("System.Text.Json");
    }

    private async Task<(int ExitCode, string Output)> AuditAsync(string references)
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
                ["exec", gate.Assembly, Path.Combine(directory.FullName, "Probe.csproj")],
                repo);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(string file, string[] arguments, string workingDirectory, Dictionary<string, string>? environment = null)
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
        foreach (var (key, value) in environment ?? [])
        {
            start.Environment[key] = value;
        }
        using var process = Process.Start(start)!;
        // Longer than the gate's own 10-minute scanner deadline plus a cold script build, so
        // the gate normally cleans up its scanner itself; the sweep below covers the rest.
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        var stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var stderr = process.StandardError.ReadToEndAsync(deadline.Token);
        try
        {
            // One deadline covers the exit and both pipes, which a descendant could hold open.
            await Task.WhenAll(process.WaitForExitAsync(deadline.Token), stdout, stderr);
        }
        finally
        {
            // Only this command's own process group is signalled. The gate cleans up its
            // separately grouped scanner itself within its 10-minute deadline, which the
            // harness deadline above always outlasts.
            using var kill = Process.Start(new ProcessStartInfo("kill", ["-KILL", "--", $"-{process.Id}"]) { RedirectStandardError = true });
            await kill!.WaitForExitAsync();
        }

        return (process.ExitCode, await stdout + await stderr);
    }

    private static string FindRepository()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "TerraScale.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Run the slow suite from inside the ts-auth checkout.");
    }
}
