using System.Diagnostics;

namespace TerraScale.Auth.Slow;

/// <summary>
/// Runs the real slow-suite failure reporter (scripts/ci/report-slow-failure.sh) in dry-run
/// mode. The public report must name the failing tests and link the run, but carry no log
/// text, so nothing from test output (credentials, argument values) can reach the issue.
/// </summary>
public sealed class FailureReporterTests
{
    [Test]
    public async Task The_report_names_failing_tests_and_carries_no_log_text()
    {
        var log = string.Join('\n',
            "failed Malformed_scanner_reports_fail_the_gate(not-json, password=argument-secret) (1s)",
            "failed Malformed_scanner_reports_fail_the_gate(wrong-project, other) (1s)",
            "failed Readiness_recovers_once_a_late_check_finishes (3s)",
            "  AssertionException: Expected 200 but received assertion-secret",
            "PASSWORD=\"multi-line",
            "log-secret\"",
            "Authorization: Bearer header-secret",
            "failed \u001b[31mInjected `code` <b>markup</b>",
            "failed secret_token_value_shaped_like_a_test");

        var report = await ReportAsync(log);

        await Assert.That(report).Contains("# Slow test suite failed");
        await Assert.That(report).Contains("- Malformed_scanner_reports_fail_the_gate (2 cases)");
        await Assert.That(report).Contains("- Readiness_recovers_once_a_late_check_finishes");
        await Assert.That(report).Contains("https://git.example.invalid/example/ts-auth/actions/runs/1");
        foreach (var leaked in new[] { "argument-secret", "assertion-secret", "log-secret", "header-secret", "multi-line", "<b>", "Injected", "secret_token_value" })
        {
            await Assert.That(report).DoesNotContain(leaked);
        }
    }

    [Test]
    public async Task A_huge_log_is_reported_quickly_and_compactly()
    {
        var lines = new List<string> { "failed A_huge_log_is_reported_quickly_and_compactly (1s)" };
        lines.AddRange(Enumerable.Range(0, 5000).Select(i => $"PASSWORD=secret-line-{i}"));
        lines.Add(new string('x', 2_000_000));

        var started = Stopwatch.StartNew();
        var report = await ReportAsync(string.Join('\n', lines));

        await Assert.That(report).Contains("- A_huge_log_is_reported_quickly_and_compactly");
        await Assert.That(report).DoesNotContain("secret-line-");
        await Assert.That(report.Length).IsLessThan(10_000);
        await Assert.That(started.Elapsed).IsLessThan(TimeSpan.FromSeconds(60));
    }

    private static async Task<string> ReportAsync(string log)
    {
        var repo = FindRepository();
        var file = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(file, log + "\n");
            // setsid: the reporter leads its own process group, so cleanup reaches every child.
            var start = new ProcessStartInfo("setsid", ["bash", Path.Combine(repo, "scripts", "ci", "report-slow-failure.sh"), file])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            start.Environment["REPORT_DRY_RUN"] = "1";
            start.Environment["GITHUB_SERVER_URL"] = "https://git.example.invalid";
            start.Environment["GITHUB_REPOSITORY"] = "example/ts-auth";
            start.Environment["GITHUB_RUN_ID"] = "1";
            start.Environment["GITHUB_RUN_NUMBER"] = "1";
            start.Environment["GITHUB_SHA"] = "0000000000000000000000000000000000000000";
            using var process = Process.Start(start)!;
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                var stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
                var stderr = process.StandardError.ReadToEndAsync(deadline.Token);
                await Task.WhenAll(process.WaitForExitAsync(deadline.Token), stdout, stderr);
                await Assert.That(process.ExitCode).IsEqualTo(0).Because(await stderr);
                return await stdout;
            }
            finally
            {
                // A hung reporter must not outlive the test: end its whole group, then wait.
                using var kill = Process.Start(new ProcessStartInfo("kill", ["-KILL", "--", $"-{process.Id}"]) { RedirectStandardError = true });
                await kill!.WaitForExitAsync();
                if (!process.HasExited)
                {
                    await process.WaitForExitAsync();
                }
            }
        }
        finally
        {
            File.Delete(file);
        }
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
