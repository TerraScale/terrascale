#!/usr/bin/env dotnet run
#:property NuGetLockFilePath=check-nuget-vulnerabilities.tests.packages.lock.json
#:package TUnit@1.72.10
#:include check-nuget-vulnerabilities.lib.cs

// Port of the former scripts/test_check_nuget_vulnerabilities.py unittest suite.

using System.Text.Json;

public sealed class AuditTests
{
    private const string Project = "service.csproj";

    private static string CleanReport(string frameworks = "") =>
        "{\"version\": 1, \"parameters\": \"--vulnerable --include-transitive\", " +
        "\"sources\": [\"https://api.nuget.org/v3/index.json\"], " +
        $"\"projects\": [{{\"path\": \"{Path.GetFullPath(Project)}\"{(frameworks.Length > 0 ? ", \"frameworks\": " + frameworks : "")}}}]}}";

    private static JsonDocument Parse(string json) => JsonDocument.Parse(json);

    private static string? ExpectRejection(string json)
    {
        try
        {
            TerraScale.Ci.CheckNugetVulnerabilities.ValidateReport(Parse(json), Project);
            return "expected AuditReportException";
        }
        catch (TerraScale.Ci.AuditReportException)
        {
            return null;
        }
    }

    [Test]
    public async Task Real_clean_shape_without_frameworks_yields_no_findings()
    {
        var findings = TerraScale.Ci.CheckNugetVulnerabilities.ValidateReport(Parse(CleanReport()), Project);
        await Assert.That(findings.Count).IsEqualTo(0);
    }

    public static string[] RejectedReports() =>
    [
        CleanReport().Replace("\"version\": 1", "\"version\": true"),
        CleanReport().Replace("\"version\": 1", "\"version\": 2"),
        CleanReport().Replace("\"sources\": [\"https://api.nuget.org/v3/index.json\"]", "\"sources\": []"),
        CleanReport().Replace($"\"projects\": [{{\"path\": \"{Path.GetFullPath(Project)}\"}}]",
            "\"projects\": [{\"path\": \"other.csproj\"}]"),
        CleanReport().Replace("--vulnerable --include-transitive", "--vulnerable"),
        CleanReport().Replace("\"projects\":",
            "\"logs\": [{\"level\": \"Warning\", \"message\": \"source unavailable\"}], \"projects\":"),
        CleanReport().Replace($"\"path\": \"{Path.GetFullPath(Project)}\"",
            $"\"path\": \"{Path.GetFullPath(Project)}\", \"frameworks\": null"),
        "{}",
        // Valid JSON strings that are not valid paths fail as a mismatch, not an unhandled exception.
        CleanReport().Replace($"\"path\": \"{Path.GetFullPath(Project)}\"", "\"path\": \"\""),
        CleanReport().Replace($"\"path\": \"{Path.GetFullPath(Project)}\"", "\"path\": \"a\\u0000b.csproj\""),
    ];

    [Test]
    [MethodDataSource(nameof(RejectedReports))]
    public async Task Incomplete_reports_are_rejected(string report)
    {
        await Assert.That(ExpectRejection(report)).IsNull();
    }

    [Test]
    [Arguments("topLevelPackages", "Low")]
    [Arguments("transitivePackages", "Moderate")]
    [Arguments("topLevelPackages", "High")]
    [Arguments("transitivePackages", "Critical")]
    public async Task Direct_and_transitive_advisories_are_reported(string kind, string severity)
    {
        var report = CleanReport(
            "[{\"framework\": \"net11.0\", \"" + kind + "\": [{\"id\": \"Example\", \"resolvedVersion\": \"1.0\", " +
            "\"vulnerabilities\": [{\"severity\": \"" + severity + "\", \"advisoryurl\": \"https://example.test/advisory\"}]}]}]");
        var findings = TerraScale.Ci.CheckNugetVulnerabilities.ValidateReport(Parse(report), Project);
        await Assert.That(findings.Count).IsEqualTo(1);
        await Assert.That(findings[0].GetProperty("id").GetString()).IsEqualTo("Example");

        // main() exits 1 when the seam returns a vulnerable report.
        var exitCode = TerraScale.Ci.CheckNugetVulnerabilities.Run([Project],
            _ => (0, report, ""));
        await Assert.That(exitCode).IsEqualTo(1);

        // vulnerabilities: null is a malformed report, not a clean one.
        await Assert.That(ExpectRejection(report.Replace("\"vulnerabilities\": [{",
                "\"vulnerabilities\": null, \"_\": [{")))
            .IsNull();
    }

    [Test]
    public async Task Cli_exit_status_matches_the_python_tool()
    {
        // (stdout, runner exit code) -> expected exit: clean 0; malformed/`{}`/not-json/nonzero -> 1.
        var cases = new (string Output, int RunnerExit, int Expected)[]
        {
            (CleanReport(), 0, 0),
            ("{}", 0, 1),
            ("not json", 0, 1),
            (CleanReport(), 1, 1),
        };
        foreach (var (output, runnerExit, expected) in cases)
        {
            var rc = TerraScale.Ci.CheckNugetVulnerabilities.Run([Project],
                _ => (runnerExit, output, ""));
            await Assert.That(rc).IsEqualTo(expected);
        }
    }

    [Test]
    public async Task Runner_failures_fail_closed()
    {
        var rc = TerraScale.Ci.CheckNugetVulnerabilities.Run([Project],
            _ => throw new System.ComponentModel.Win32Exception("dotnet not found"));
        await Assert.That(rc).IsEqualTo(1);
    }

    [Test]
    public async Task Scanner_output_beyond_both_pipe_buffers_is_drained()
    {
        // Fills stderr before writing stdout: reading stdout to the end first would deadlock.
        var (exitCode, stdOut, stdErr) = TerraScale.Ci.CheckNugetVulnerabilities.RunScanner(
            ["sh", "-c", "head -c 1000000 /dev/zero | tr '\\0' e >&2; head -c 1000000 /dev/zero | tr '\\0' o"],
            TimeSpan.FromSeconds(60));
        await Assert.That(exitCode).IsEqualTo(0);
        await Assert.That(stdOut.Length).IsEqualTo(1_000_000);
        await Assert.That(stdErr.Length).IsEqualTo(1_000_000);
    }

    [Test]
    public async Task Descendant_holding_a_pipe_fails_within_the_deadline_and_is_killed()
    {
        var pidFile = Path.GetTempFileName();
        try
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var (exitCode, _, stdErr) = TerraScale.Ci.CheckNugetVulnerabilities.RunScanner(
                ["sh", "-c", $"sleep 120 & echo $! > '{pidFile}'; exit 0"],
                TimeSpan.FromSeconds(2));
            await Assert.That(clock.Elapsed).IsLessThan(TimeSpan.FromSeconds(30));
            await Assert.That(exitCode).IsEqualTo(1);
            await Assert.That(stdErr).IsEqualTo("sh -c did not finish within 2 seconds");

            var stat = $"/proc/{File.ReadAllText(pidFile).Trim()}/stat";
            var dead = false;
            for (var attempt = 0; attempt < 100 && !dead; attempt++)
            {
                // Gone, or a zombie waiting for a reaper that is not this test.
                dead = !File.Exists(stat) || File.ReadAllText(stat).Split(')')[^1].TrimStart().StartsWith('Z');
                if (!dead)
                {
                    await Task.Delay(100);
                }
            }
            await Assert.That(dead).IsTrue();
        }
        finally
        {
            File.Delete(pidFile);
        }
    }

        [Test]
    public async Task No_projects_prints_usage_and_exits_2()
    {
        var rc = TerraScale.Ci.CheckNugetVulnerabilities.Run([]);
        await Assert.That(rc).IsEqualTo(2);
    }
}
