using System.Diagnostics;

namespace TerraScale.Auth.Slow;

/// <summary>
/// Runs the real NuGet vulnerability gate (scripts/check-nuget-vulnerabilities.cs) against
/// throwaway projects, so its fail-closed behaviour is proven end to end, not only when the
/// current dependency graph happens to be clean.
/// </summary>
// Every case runs `dotnet run` on the same file-based script, which builds into one shared
// runfile cache directory; concurrent builds collide there, so the cases run one at a time.
[NotInParallel(nameof(DependencyAuditTests))]
public sealed class DependencyAuditTests
{
    [Test]
    public async Task A_project_with_a_known_vulnerable_package_fails_the_gate()
    {
        // System.Text.Json 8.0.0 carries published advisories (for example GHSA-hh2w-p6rv-4g7w).
        var (exitCode, output) = await AuditAsync("""<PackageReference Include="System.Text.Json" Version="8.0.0" />""");

        await Assert.That(exitCode).IsEqualTo(1).Because(output);
        await Assert.That(output).Contains("vulnerable NuGet package");
        await Assert.That(output).Contains("System.Text.Json");
    }

    [Test]
    public async Task A_project_without_vulnerable_packages_passes_the_gate()
    {
        var (exitCode, output) = await AuditAsync(string.Empty);

        await Assert.That(exitCode).IsEqualTo(0).Because(output);
        await Assert.That(output).Contains("0 vulnerable packages");
    }

    [Test]
    public async Task A_project_that_cannot_be_audited_fails_the_gate()
    {
        var (exitCode, output) = await AuditAsync("""<PackageReference Include="TerraScale.Package.That.Does.Not.Exist" Version="1.0.0" />""");

        // The gate's own scanner-failure diagnostic, not merely some failing exit code.
        await Assert.That(exitCode).IsEqualTo(1).Because(output);
        await Assert.That(output).Contains("NuGet audit failed for");
    }

    // The real gate, fed crafted scanner reports through a stand-in `dotnet list`, so every
    // fail-closed validation path is exercised end to end.
    [Test]
    [Arguments("missing-transitive", """{"version":1,"parameters":"--vulnerable","sources":["https://api.nuget.org/v3/index.json"],"projects":[{"path":"PROJECT"}]}""", "does not cover transitive")]
    [Arguments("unsupported-version", """{"version":2,"parameters":"--vulnerable --include-transitive","sources":["https://api.nuget.org/v3/index.json"],"projects":[{"path":"PROJECT"}]}""", "unsupported or missing report version")]
    [Arguments("no-sources", """{"version":1,"parameters":"--vulnerable --include-transitive","sources":[],"projects":[{"path":"PROJECT"}]}""", "missing advisory sources")]
    [Arguments("wrong-project", """{"version":1,"parameters":"--vulnerable --include-transitive","sources":["https://api.nuget.org/v3/index.json"],"projects":[{"path":"/elsewhere/Other.csproj"}]}""", "does not match requested project")]
    [Arguments("bad-package", """{"version":1,"parameters":"--vulnerable --include-transitive","sources":["https://api.nuget.org/v3/index.json"],"projects":[{"path":"PROJECT","frameworks":[{"framework":"net10.0","topLevelPackages":[{"id":1}]}]}]}""", "invalid package identity")]
    [Arguments("not-json", "this is not json", "invalid JSON")]
    public async Task Malformed_scanner_reports_fail_the_gate(string name, string report, string diagnostic)
    {
        _ = name;
        var (exitCode, output) = await AuditWithReportAsync(report);

        await Assert.That(exitCode).IsEqualTo(1).Because(output);
        await Assert.That(output).Contains(diagnostic);
    }

    [Test]
    public async Task A_complete_clean_scanner_report_passes_the_gate()
    {
        var (exitCode, output) = await AuditWithReportAsync(
            """{"version":1,"parameters":"--vulnerable --include-transitive","sources":["https://api.nuget.org/v3/index.json"],"projects":[{"path":"PROJECT"}]}""");

        await Assert.That(exitCode).IsEqualTo(0).Because(output);
        await Assert.That(output).Contains("0 vulnerable packages");
    }

    private static async Task<(int ExitCode, string Output)> AuditWithReportAsync(string report)
    {
        var repo = FindRepository();
        var directory = Directory.CreateTempSubdirectory("ts-auth-audit-fake-");
        try
        {
            var project = Path.Combine(directory.FullName, "Probe.csproj");
            File.WriteAllText(project, "<Project />");
            // The path is inserted as a JSON string value (quotes, backslashes escaped).
            var jsonPath = System.Text.Json.JsonSerializer.Serialize(project)[1..^1];
            File.WriteAllText(Path.Combine(directory.FullName, "report.json"), report.Replace("PROJECT", jsonPath, StringComparison.Ordinal));
            var bin = Directory.CreateDirectory(Path.Combine(directory.FullName, "bin")).FullName;
            // An absolute path to the real SDK host: a bare "dotnet" inside the stand-in would
            // resolve back to the stand-in itself.
            var realDotnet = RealDotnet();
            var fake = Path.Combine(bin, "dotnet");
            File.WriteAllText(fake, $$"""
                #!/bin/sh
                # Stand-in for `dotnet list … package` only; everything else is the real SDK.
                if [ "$1" = "list" ]; then cat {{ShellQuote(Path.Combine(directory.FullName, "report.json"))}}; exit 0; fi
                exec {{ShellQuote(realDotnet)}} "$@"
                """);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            return await RunAsync(
                realDotnet,
                ["run", Path.Combine(repo, "scripts", "check-nuget-vulnerabilities.cs"), project],
                repo,
                new Dictionary<string, string> { ["PATH"] = $"{bin}:{Environment.GetEnvironmentVariable("PATH")}" });
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    // Single-quoted for sh: nothing inside is expanded; embedded quotes are spliced in.
    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private static string RealDotnet()
    {
        if (Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host && File.Exists(host))
        {
            return host;
        }

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            var candidate = Path.Combine(directory, "dotnet");
            // Skip wrapper shims (such as mise's) that are scripts rather than the SDK host.
            if (File.Exists(candidate) && IsElf(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("The dotnet SDK host was not found.");
    }

    private static bool IsElf(string path)
    {
        Span<byte> magic = stackalloc byte[4];
        using var file = File.OpenRead(path);
        return file.Read(magic) == 4 && magic.SequenceEqual(ElfMagic);
    }

    private static ReadOnlySpan<byte> ElfMagic => [0x7f, (byte)'E', (byte)'L', (byte)'F'];

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
            if (File.Exists(Path.Combine(directory.FullName, "TerraScale.Auth.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Run the slow suite from inside the ts-auth checkout.");
    }
}
