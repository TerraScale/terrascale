
using System.Diagnostics;
using System.Text.Json;

namespace TerraScale.Ci;

public sealed class AuditReportException(string message) : Exception(message);

public static class CheckNugetVulnerabilities
{
    public delegate (int ExitCode, string StdOut, string StdErr) AuditRunner(string project);

    public static List<JsonElement> ValidateReport(JsonDocument report, string project)
    {
        var root = report.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new AuditReportException("unsupported or missing report version");
        }
        if (
            !root.TryGetProperty("version", out var version)
            || version.ValueKind != JsonValueKind.Number
            || !long.TryParse(version.GetRawText(), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var versionValue)
            || versionValue != 1
        )
        {
            throw new AuditReportException("unsupported or missing report version");
        }
        if (
            !root.TryGetProperty("parameters", out var parameters)
            || parameters.ValueKind != JsonValueKind.String
        )
        {
            throw new AuditReportException("report does not cover transitive vulnerability scanning");
        }
        var tokens = parameters.GetString()!.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (!tokens.Contains("--vulnerable") || !tokens.Contains("--include-transitive"))
        {
            throw new AuditReportException("report does not cover transitive vulnerability scanning");
        }
        if (
            !root.TryGetProperty("sources", out var sources)
            || sources.ValueKind != JsonValueKind.Array
            || sources.GetArrayLength() == 0
            || sources.EnumerateArray().Any(s => s.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(s.GetString()))
        )
        {
            throw new AuditReportException("missing advisory sources");
        }
        RequireEmptyLogs(root);
        if (
            !root.TryGetProperty("projects", out var projects)
            || projects.ValueKind != JsonValueKind.Array
            || projects.GetArrayLength() != 1
        )
        {
            throw new AuditReportException("expected exactly one audited project");
        }
        var entry = projects[0];
        if (
            entry.ValueKind != JsonValueKind.Object
            || !entry.TryGetProperty("path", out var path)
            || path.ValueKind != JsonValueKind.String
            || !SameProject(path.GetString()!, project)
        )
        {
            throw new AuditReportException("audited project does not match requested project");
        }
        RequireEmptyLogs(entry);
        if (!entry.TryGetProperty("frameworks", out var frameworks))
        {
            frameworks = default;
        }
        if (frameworks.ValueKind != JsonValueKind.Array && frameworks.ValueKind != JsonValueKind.Undefined)
        {
            throw new AuditReportException("invalid framework list");
        }
        var findings = new List<JsonElement>();
        if (frameworks.ValueKind == JsonValueKind.Undefined)
        {
            return findings;
        }
        foreach (var framework in frameworks.EnumerateArray())
        {
            if (
                framework.ValueKind != JsonValueKind.Object
                || !framework.TryGetProperty("framework", out var frameworkName)
                || frameworkName.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(frameworkName.GetString())
            )
            {
                throw new AuditReportException("invalid framework identity");
            }
            RequireEmptyLogs(framework);
            foreach (var kind in (string[])["topLevelPackages", "transitivePackages"])
            {
                if (!framework.TryGetProperty(kind, out var packages))
                {
                    packages = default;
                }
                if (packages.ValueKind != JsonValueKind.Array && packages.ValueKind != JsonValueKind.Undefined)
                {
                    throw new AuditReportException("invalid package list");
                }
                if (packages.ValueKind == JsonValueKind.Undefined)
                {
                    continue;
                }
                foreach (var package in packages.EnumerateArray())
                {
                    if (
                        package.ValueKind != JsonValueKind.Object
                        || !package.TryGetProperty("id", out var id)
                        || id.ValueKind != JsonValueKind.String
                        || string.IsNullOrWhiteSpace(id.GetString())
                        || !package.TryGetProperty("resolvedVersion", out var resolvedVersion)
                        || resolvedVersion.ValueKind != JsonValueKind.String
                        || string.IsNullOrWhiteSpace(resolvedVersion.GetString())
                    )
                    {
                        throw new AuditReportException("invalid package identity");
                    }
                    if (
                        !package.TryGetProperty("vulnerabilities", out var vulnerabilities)
                        || vulnerabilities.ValueKind != JsonValueKind.Array
                        || vulnerabilities.GetArrayLength() == 0
                    )
                    {
                        throw new AuditReportException("vulnerable package is missing advisory details");
                    }
                    foreach (var advisory in vulnerabilities.EnumerateArray())
                    {
                        if (
                            advisory.ValueKind != JsonValueKind.Object
                            || !advisory.TryGetProperty("severity", out var severity)
                            || severity.ValueKind != JsonValueKind.String
                            || severity.GetString() is not ("Low" or "Moderate" or "High" or "Critical")
                            || !advisory.TryGetProperty("advisoryurl", out var advisoryUrl)
                            || advisoryUrl.ValueKind != JsonValueKind.String
                            || string.IsNullOrWhiteSpace(advisoryUrl.GetString())
                        )
                        {
                            throw new AuditReportException("invalid vulnerability advisory");
                        }
                    }
                    findings.Add(package);
                }
            }
        }
        return findings;
    }

    private static void RequireEmptyLogs(JsonElement element)
    {
        if (!element.TryGetProperty("logs", out var logs))
        {
            return;
        }
        if (logs.ValueKind != JsonValueKind.Array || logs.GetArrayLength() != 0)
        {
            throw new AuditReportException("scanner reported diagnostics; audit completeness is unverified");
        }
    }

    public static int Run(IReadOnlyList<string> projects, AuditRunner? runAudit = null)
    {
        if (projects.Count == 0)
        {
            Console.Error.WriteLine("usage: check-nuget-vulnerabilities.cs <project> [...]");
            return 2;
        }
        runAudit ??= RunDotnetList;
        var vulnerable = 0;
        foreach (var project in projects)
        {
            int exitCode;
            string stdOut, stdErr;
            try
            {
                (exitCode, stdOut, stdErr) = runAudit(project);
            }
            catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
            {
                Console.Error.WriteLine($"NuGet audit could not run for {project}: {exception.Message}");
                return 1;
            }
            if (exitCode != 0)
            {
                Console.Error.WriteLine($"NuGet audit failed for {project}: {stdErr.Trim()}");
                return 1;
            }
            JsonDocument report;
            try
            {
                report = JsonDocument.Parse(stdOut);
            }
            catch (JsonException)
            {
                Console.Error.WriteLine($"NuGet audit returned invalid JSON for {project}");
                return 1;
            }
            using (report)
            {
                List<JsonElement> findings;
                try
                {
                    findings = ValidateReport(report, project);
                }
                catch (AuditReportException exception)
                {
                    Console.Error.WriteLine(
                        $"NuGet audit returned incomplete or malformed output for {project}: {exception.Message}");
                    return 1;
                }
                foreach (var package in findings)
                {
                    vulnerable++;
                    Console.Error.WriteLine(
                        $"vulnerable NuGet package in {project}: {package.GetProperty("id").GetString()} " +
                        $"{package.GetProperty("resolvedVersion").GetString()}");
                }
            }
        }
        if (vulnerable > 0)
        {
            Console.Error.WriteLine($"{vulnerable} vulnerable NuGet package(s) require documented disposition.");
            return 1;
        }
        Console.WriteLine($"NuGet vulnerability audit: {projects.Count} projects, 0 vulnerable packages.");
        return 0;
    }

    private static string KillGroup(Process process)
    {
        string failure;
        try
        {
            using var kill = Process.Start(new ProcessStartInfo("kill", ["-KILL", "--", $"-{process.Id}"])
            {
                RedirectStandardError = true,
            })!;
            var killErr = kill.StandardError.ReadToEndAsync();
            if (!kill.WaitForExit(TimeSpan.FromSeconds(10)))
            {
                kill.Kill();
                failure = "kill did not exit within 10 seconds";
            }
            else if (kill.ExitCode != 0)
            {
                failure = $"kill exited {kill.ExitCode}: {killErr.Result.Trim()}";
            }
            else
            {
                return string.Empty;
            }
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            failure = $"kill could not run: {exception.Message}";
        }

        try
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(TimeSpan.FromSeconds(10));
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            failure += $"; fallback kill failed: {exception.Message}";
        }
        return $" (process group cleanup failed, descendants may remain: {failure})";
    }

    private static bool SameProject(string reported, string requested)
    {
        if (string.IsNullOrWhiteSpace(reported) || reported.Contains('\0', StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            return Path.GetFullPath(reported) == Path.GetFullPath(requested);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static (int, string, string) RunDotnetList(string project) =>
        RunScanner(
            ["dotnet", "list", project, "package", "--no-restore", "--vulnerable", "--include-transitive", "--format", "json"],
            TimeSpan.FromMinutes(10));

    public static (int ExitCode, string StdOut, string StdErr) RunScanner(IReadOnlyList<string> command, TimeSpan timeout)
    {
        var start = new ProcessStartInfo("setsid")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in command)
        {
            start.ArgumentList.Add(argument);
        }
        var name = string.Join(' ', command.Take(2));
        using var process = Process.Start(start)!;
        var stdOut = process.StandardOutput.ReadToEndAsync();
        var stdErr = process.StandardError.ReadToEndAsync();
        var clock = Stopwatch.StartNew();
        var exited = process.WaitForExit(timeout);
        var remaining = timeout - clock.Elapsed;
        bool drained;
        try
        {
            drained = exited && Task.WaitAll([stdOut, stdErr], remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
        }
        catch (AggregateException readFailure)
        {
            var cleanup = KillGroup(process);
            return (1, string.Empty, $"reading {name} output failed: {readFailure.InnerException?.Message}{cleanup}");
        }

        if (!drained)
        {
            var cleanup = KillGroup(process);
            return (1, string.Empty, $"{name} did not finish within {timeout.TotalSeconds:0} seconds{cleanup}");
        }

        return (process.ExitCode, stdOut.Result, stdErr.Result);
    }
}
